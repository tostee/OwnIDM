using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using OwnIDM.Core.Dash;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Tools;

namespace OwnIDM.Core.Hls;

public sealed record HlsProbe(List<HlsVariant> Variants, Uri MediaUrl, int SegmentCount, double TotalSeconds,
    bool IsLive, bool Encrypted, bool SeparateAudio, bool Unsupported);

/// <summary>
/// Downloads VOD HLS and DASH streams: segments are fetched in parallel and appended in order to a
/// .part file (a second one for a separate audio rendition), so a download resumes after the
/// last complete segment. Encrypted (DRM/AES) and live streams are refused. HLS playlists and
/// DASH manifests are told apart by their content.
/// </summary>
public sealed class HlsDownloader : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public HlsDownloader(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false, // see Redirects: cookies stay on their own site
            UseCookies = false,
            MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private TimeSpan IoTimeout => TimeSpan.FromSeconds(Math.Max(5, _settings.TimeoutSeconds));

    public string TempPathFor(DownloadItem item) =>
        Path.Combine(_settings.TempDirectory, item.Id.ToString("N") + ".hls.part");

    public string AudioTempPathFor(DownloadItem item) => TempPathFor(item) + ".audio";

    /// <summary>Reads the playlist or manifest; for several qualities, describes the best one.</summary>
    public async Task<HlsProbe> ProbeAsync(string url, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        var origin = new Uri(url);
        var context = new RequestContext(origin, referrer, cookies, userAgent);
        var text = await GetTextAsync(origin, context, ct).ConfigureAwait(false);
        if (DashParser.IsManifest(text))
            return await ProbeDashAsync(DashParser.Parse(text, origin), context, ct).ConfigureAwait(false);
        var variants = new List<HlsVariant>();
        var uri = origin;
        bool separateAudio = false;
        if (HlsParser.IsMaster(text))
        {
            variants = HlsParser.ParseMaster(text, origin);
            if (variants.Count == 0)
                throw new FormatException("La lista HLS no contiene ninguna calidad.");
            separateAudio = HlsParser.AudioFor(variants[0], HlsParser.ParseAudio(text, origin)) is not null;
            uri = variants[0].Url;
            text = await GetTextAsync(uri, context, ct).ConfigureAwait(false);
        }
        var media = HlsParser.ParseMedia(text, uri);
        return new HlsProbe(variants, uri, media.Segments.Count, media.TotalSeconds, media.IsLive, media.Encrypted,
            separateAudio, media.MultipleInits);
    }

    private async Task<HlsProbe> ProbeDashAsync(DashManifest manifest, RequestContext context, CancellationToken ct)
    {
        var video = manifest.VideoFor(null) ?? throw new FormatException("El manifiesto DASH no contiene ningún vídeo.");
        var audio = manifest.AudioFor(video);
        // Encrypted or live content is refused anyway: no need to read an index for it.
        bool encrypted = video.Encrypted || audio?.Encrypted == true;
        var media = encrypted || manifest.IsLive || manifest.MultiplePeriods ? null : await SegmentsOfAsync(video, context, ct).ConfigureAwait(false);
        return new HlsProbe(manifest.Video.Select(r => r.ToVariant()).ToList(), video.Key, media?.Segments.Count ?? 0,
            media?.TotalSeconds > 0 ? media.TotalSeconds : manifest.TotalSeconds, manifest.IsLive, encrypted,
            audio is not null, manifest.MultiplePeriods);
    }

    /// <summary>The segments of a DASH representation, reading its 'sidx' index when the manifest lists none.</summary>
    private async Task<HlsMediaPlaylist> SegmentsOfAsync(DashRepresentation rep, RequestContext context, CancellationToken ct)
    {
        if (rep.Playlist is not null)
            return rep.Playlist;
        var index = rep.Index!;
        var bytes = await GetBytesAsync(index.Url, context, index.IndexOffset, index.IndexLength, ct).ConfigureAwait(false);
        return DashParser.FromSidx(index, bytes);
    }

    /// <summary>Never throws; the outcome is reflected in <see cref="DownloadItem.Status"/>.</summary>
    public Task RunAsync(DownloadItem item, CancellationToken ct) => new Job(this, item, ct).RunAsync();

    public void Dispose() => _http.Dispose();

    /// <summary>Headers of one download; cookies only go to the site they were captured for.</summary>
    private sealed record RequestContext(Uri CookieOrigin, string? Referrer, string? Cookies, string? UserAgent);

    private static HttpRequestMessage Build(Uri url, RequestContext c, long? offset = null, long? length = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11 };
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(c.UserAgent) ? DownloadEngine.DefaultUserAgent : c.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(c.Referrer))
            req.Headers.TryAddWithoutValidation("Referer", c.Referrer);
        if (!string.IsNullOrWhiteSpace(c.Cookies) && Redirects.CookiesAllowed(url, c.CookieOrigin))
            req.Headers.TryAddWithoutValidation("Cookie", c.Cookies);
        if (length is not null)
            req.Headers.Range = new RangeHeaderValue(offset ?? 0, (offset ?? 0) + length - 1);
        return req;
    }

    private async Task<string> GetTextAsync(Uri url, RequestContext context, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IoTimeout);
        try
        {
            using var resp = await Redirects.SendAsync(_http, url, u => Build(u, context), HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"El servidor respondió {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd(), null, resp.StatusCode);
            return await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tiempo de espera agotado al leer la lista HLS.");
        }
    }

    private async Task<byte[]> GetBytesAsync(Uri url, RequestContext context, long offset, long length, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IoTimeout);
        try
        {
            using var resp = await Redirects.SendAsync(_http, url, u => Build(u, context, offset, length), HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"El servidor respondió {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd(), null, resp.StatusCode);
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new IOException("El servidor ignoró el rango de bytes del índice del vídeo.");
            return await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tiempo de espera agotado al leer el índice del vídeo.");
        }
    }

    private sealed class Track(string path, HlsMediaPlaylist playlist)
    {
        public string Path { get; } = path;
        public HlsMediaPlaylist Playlist { get; } = playlist;
        public FileStream? File { get; set; }
    }

    private sealed class Job
    {
        private readonly HlsDownloader _owner;
        private readonly DownloadItem _item;
        private readonly CancellationToken _ct;
        private readonly AppSettings _settings;
        private readonly RateLimiter _limiter;
        private RequestContext _context = null!;
        private long _inFlight; // bytes of segments received but not yet appended
        private bool _webm; // DASH WebM tracks: the joined segments form a .webm, not an .mp4

        public Job(HlsDownloader owner, DownloadItem item, CancellationToken ct)
        {
            _owner = owner;
            _item = item;
            _ct = ct;
            _settings = owner._settings;
            _limiter = new RateLimiter(() => _settings.EffectiveSpeedLimit);
        }

        public async Task RunAsync()
        {
            _item.LastTryAt = DateTime.Now;
            _item.ErrorMessage = null;
            _item.Status = DownloadStatus.Connecting;
            var tracks = new List<Track>();
            try
            {
                Directory.CreateDirectory(_settings.TempDirectory);
                _context = new RequestContext(new Uri(_item.SourceUrl ?? _item.Url), _item.Referrer, _item.Cookies, _item.UserAgent);
                var (video, audio) = await LoadPlaylistsAsync().ConfigureAwait(false);
                foreach (var p in audio is null ? new[] { video } : new[] { video, audio })
                {
                    if (p.Encrypted)
                        throw new NotSupportedException("El vídeo está cifrado o protegido (DRM); OwnIDM no puede descargarlo.");
                    if (p.IsLive)
                        throw new NotSupportedException("Es una emisión en directo; solo se pueden descargar vídeos completos.");
                    if (p.MultipleInits)
                        throw new NotSupportedException("La lista cambia de formato a mitad del vídeo; no está soportada.");
                    if (p.Segments.Count == 0)
                        throw new FormatException("La lista de reproducción no contiene segmentos.");
                }
                var ffmpeg = Ffmpeg.Find(_settings);
                if (audio is not null && ffmpeg is null)
                    throw new NotSupportedException("Este vídeo trae el audio por separado y hace falta ffmpeg para unirlo. " +
                                                    "Instálalo o indica su ruta en Opciones → Vídeo.");

                tracks.Add(new Track(_owner.TempPathFor(_item), video));
                if (audio is not null)
                    tracks.Add(new Track(_owner.AudioTempPathFor(_item), audio));
                var entries = tracks.SelectMany((t, i) => t.Playlist.Segments.Select(s => (Segment: s, Track: i))).ToList();

                OpenTracks(tracks, entries.Count);
                _item.Status = DownloadStatus.Downloading;
                await DownloadSegmentsAsync(entries, tracks).ConfigureAwait(false);
                foreach (var t in tracks)
                {
                    await t.File!.DisposeAsync().ConfigureAwait(false);
                    t.File = null;
                }

                await FinishAsync(tracks, ffmpeg).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                _item.Status = DownloadStatus.Paused;
            }
            catch (Exception ex)
            {
                _item.ErrorMessage = ex.Message;
                _item.Status = DownloadStatus.Error;
            }
            finally
            {
                foreach (var t in tracks)
                    t.File?.Dispose();
                _item.Downloaded = _item.Status == DownloadStatus.Completed ? _item.TotalSize : _item.HlsBytesDone + _item.HlsAudioBytesDone;
                _item.ActiveConnections = 0;
                _item.Speed = 0;
                _item.NotifyProgressChanged();
            }
        }

        /// <summary>Resolves the media playlist (the variant chosen, or the best) and its separate audio, if any.</summary>
        private async Task<(HlsMediaPlaylist Video, HlsMediaPlaylist? Audio)> LoadPlaylistsAsync()
        {
            var source = new Uri(_item.SourceUrl ?? _item.Url);
            var text = await _owner.GetTextAsync(source, _context, _ct).ConfigureAwait(false);
            if (DashParser.IsManifest(text))
                return await LoadDashAsync(DashParser.Parse(text, source), source).ConfigureAwait(false);
            if (!HlsParser.IsMaster(text))
                return (HlsParser.ParseMedia(text, source), null);

            var variants = HlsParser.ParseMaster(text, source);
            var variant = variants.FirstOrDefault(v => v.Url.ToString() == _item.Url) ?? variants.FirstOrDefault()
                          ?? throw new FormatException("La lista HLS no contiene ninguna calidad.");
            // Pin the variant so a resume never switches quality.
            _item.SourceUrl = source.ToString();
            _item.Url = variant.Url.ToString();

            var audioUrl = HlsParser.AudioFor(variant, HlsParser.ParseAudio(text, source));
            var videoText = await _owner.GetTextAsync(variant.Url, _context, _ct).ConfigureAwait(false);
            var video = HlsParser.ParseMedia(videoText, variant.Url);
            if (audioUrl is null)
                return (video, null);
            var audioText = await _owner.GetTextAsync(audioUrl, _context, _ct).ConfigureAwait(false);
            return (video, HlsParser.ParseMedia(audioText, audioUrl));
        }

        /// <summary>Resolves the representation chosen (or the best) and the separate audio to join with it.</summary>
        private async Task<(HlsMediaPlaylist Video, HlsMediaPlaylist? Audio)> LoadDashAsync(DashManifest manifest, Uri source)
        {
            if (manifest.MultiplePeriods)
                throw new NotSupportedException("El vídeo DASH tiene varios periodos (p. ej. anuncios intercalados); no está soportado.");
            var video = manifest.VideoFor(_item.Url) ?? throw new FormatException("El manifiesto DASH no contiene ningún vídeo.");
            // Pin the representation so a resume never switches quality.
            _item.SourceUrl = source.ToString();
            _item.Url = video.Key.ToString();
            _webm = video.IsWebM;

            var audio = manifest.AudioFor(video);
            var videoList = await _owner.SegmentsOfAsync(video, _context, _ct).ConfigureAwait(false);
            videoList.Encrypted |= video.Encrypted;
            if (audio is null)
                return (videoList, null);
            var audioList = await _owner.SegmentsOfAsync(audio, _context, _ct).ConfigureAwait(false);
            audioList.Encrypted |= audio.Encrypted;
            return (videoList, audioList);
        }

        /// <summary>Opens the .part files positioned after the last complete segment, or starts over.</summary>
        private void OpenTracks(List<Track> tracks, int count)
        {
            bool fresh = _item.HlsSegmentCount != count;
            foreach (var t in tracks)
                t.File = new FileStream(t.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

            // A .part shorter than the recorded progress (e.g. lost in a crash) cannot be trusted.
            long[] expected = [_item.HlsBytesDone, _item.HlsAudioBytesDone];
            for (int i = 0; i < tracks.Count; i++)
                fresh |= tracks[i].File!.Length < expected[i];
            if (fresh)
            {
                lock (_item.SyncRoot)
                {
                    _item.HlsSegmentsDone = 0;
                    _item.HlsBytesDone = 0;
                    _item.HlsAudioBytesDone = 0;
                }
            }
            _item.HlsSegmentCount = count;
            tracks[0].File!.SetLength(_item.HlsBytesDone);
            tracks[0].File!.Position = _item.HlsBytesDone;
            if (tracks.Count > 1)
            {
                tracks[1].File!.SetLength(_item.HlsAudioBytesDone);
                tracks[1].File!.Position = _item.HlsAudioBytesDone;
            }
        }

        /// <summary>Keeps up to N segment downloads in flight and appends them strictly in order.</summary>
        private async Task DownloadSegmentsAsync(List<(HlsSegment Segment, int Track)> entries, List<Track> tracks)
        {
            int parallel = Math.Clamp(_settings.MaxConnectionsPerDownload, 1, 8);
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var window = new Queue<Task<byte[]>>();
            int scheduled = _item.HlsSegmentsDone;
            try
            {
                while (_item.HlsSegmentsDone < entries.Count)
                {
                    while (window.Count < parallel && scheduled < entries.Count)
                        window.Enqueue(FetchWithRetryAsync(entries[scheduled++].Segment, abort.Token));
                    _item.ActiveConnections = window.Count;

                    var (_, trackIndex) = entries[_item.HlsSegmentsDone];
                    var track = tracks[trackIndex];
                    // fMP4: the init section goes first, written whenever the track file is still empty.
                    if (track.File!.Length == 0 && track.Playlist.Init is { } init)
                    {
                        var initData = await FetchWithRetryAsync(init, _ct).ConfigureAwait(false);
                        await track.File.WriteAsync(initData, _ct).ConfigureAwait(false);
                        Interlocked.Add(ref _inFlight, -initData.Length);
                    }

                    var data = await window.Dequeue().ConfigureAwait(false);
                    await track.File.WriteAsync(data, _ct).ConfigureAwait(false);
                    await track.File.FlushAsync(_ct).ConfigureAwait(false);
                    Interlocked.Add(ref _inFlight, -data.Length);

                    lock (_item.SyncRoot)
                    {
                        if (trackIndex == 0)
                            _item.HlsBytesDone = track.File.Position;
                        else
                            _item.HlsAudioBytesDone = track.File.Position;
                        _item.HlsSegmentsDone++;
                    }
                    // Estimate the final size from the average segment so far.
                    long done = _item.HlsBytesDone + _item.HlsAudioBytesDone;
                    _item.TotalSize = done * entries.Count / _item.HlsSegmentsDone;
                    ReportProgress();
                }
            }
            finally
            {
                abort.Cancel();
                foreach (var pending in window)
                {
                    try { await pending.ConfigureAwait(false); }
                    catch (Exception) { /* abandoned by the cancellation above */ }
                }
                Interlocked.Exchange(ref _inFlight, 0);
            }
        }

        private void ReportProgress()
        {
            _item.Downloaded = _item.HlsBytesDone + _item.HlsAudioBytesDone + Interlocked.Read(ref _inFlight);
            _item.NotifyProgressChanged();
        }

        private async Task<byte[]> FetchWithRetryAsync(HlsSegment segment, CancellationToken ct)
        {
            for (int attempt = 1; ; attempt++)
            {
                long received = 0;
                try
                {
                    return await FetchAsync(segment, b => { received += b; Interlocked.Add(ref _inFlight, b); ReportProgress(); }, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && attempt <= _settings.MaxRetries &&
                                           ex is IOException or TimeoutException or HttpRequestException)
                {
                    Interlocked.Add(ref _inFlight, -received);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt - 1, 4))), ct).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Add(ref _inFlight, -received);
                    throw;
                }
            }
        }

        private async Task<byte[]> FetchAsync(HlsSegment segment, Action<int> onBytes, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_owner.IoTimeout);
            HttpResponseMessage resp;
            try
            {
                resp = await Redirects.SendAsync(_owner._http, segment.Url, u => Build(u, _context, segment.Offset, segment.Length),
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Tiempo de espera agotado al pedir un segmento.");
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"Segmento: el servidor respondió {(int)resp.StatusCode}", null, resp.StatusCode);
                if (segment.Length is not null && resp.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException("El servidor ignoró el rango de bytes del segmento.");

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var output = new MemoryStream(resp.Content.Headers.ContentLength is long len and < int.MaxValue ? (int)len : BufferSize);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    while (true)
                    {
                        timeout.CancelAfter(_owner.IoTimeout);
                        int n;
                        try
                        {
                            n = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), timeout.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new TimeoutException("El servidor dejó de enviar el segmento.");
                        }
                        if (n == 0)
                            break;
                        await _limiter.WaitAsync(n, ct).ConfigureAwait(false);
                        output.Write(buffer, 0, n);
                        onBytes(n);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                return output.ToArray();
            }
        }

        private async Task FinishAsync(List<Track> tracks, string? ffmpeg)
        {
            if (string.IsNullOrWhiteSpace(_item.SaveDirectory))
                _item.SaveDirectory = _settings.DirectoryFor(_item.Category);
            Directory.CreateDirectory(_item.SaveDirectory);
            var stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(_item.FileName) ? "video" : _item.FileName);
            var mp4Target = FileNameHelper.MakeUnique(Path.Combine(_item.SaveDirectory, stem + ".mp4"));
            bool fragmentedMp4 = tracks[0].Playlist.Init is not null && !_webm;

            string target;
            if (tracks.Count > 1)
            {
                // Separate audio: ffmpeg is mandatory (checked before downloading).
                if (!await Ffmpeg.MuxAsync(ffmpeg!, tracks[0].Path, tracks[1].Path, mp4Target, _ct).ConfigureAwait(false))
                    throw new IOException("ffmpeg no pudo unir el vídeo y el audio. Los segmentos descargados se conservan.");
                target = mp4Target;
                foreach (var t in tracks)
                    File.Delete(t.Path);
            }
            else if (!fragmentedMp4 && !_webm && _settings.ConvertHlsToMp4 && ffmpeg is not null &&
                     await Ffmpeg.RemuxAsync(ffmpeg, tracks[0].Path, mp4Target, _ct).ConfigureAwait(false))
            {
                target = mp4Target;
                File.Delete(tracks[0].Path);
            }
            else
            {
                // fMP4 segments already form an .mp4 (WebM ones a .webm); plain MPEG-TS stays .ts without ffmpeg.
                target = fragmentedMp4 ? mp4Target
                    : FileNameHelper.MakeUnique(Path.Combine(_item.SaveDirectory, stem + (_webm ? ".webm" : ".ts")));
                File.Move(tracks[0].Path, target);
            }

            _item.FileName = Path.GetFileName(target);
            _item.TotalSize = new FileInfo(target).Length;
            _item.Downloaded = _item.TotalSize;
            _item.Status = DownloadStatus.Completed;
        }
    }
}

/// <summary>Optional ffmpeg integration to build MP4 files without re-encoding.</summary>
public static class Ffmpeg
{
    /// <summary>The path set in Options, else the copy OwnIDM installed, else one on PATH.</summary>
    public static string? Find(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
            return settings.FfmpegPath;
        if (ToolPaths.Installed(settings, Tool.Ffmpeg) is { } managed)
            return managed;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                   .Select(dir => Path.Combine(dir.Trim('"'), "ffmpeg.exe"))
                   .FirstOrDefault(File.Exists);
    }

    /// <summary>MPEG-TS (or any single input) to MP4, streams copied.</summary>
    public static Task<bool> RemuxAsync(string ffmpeg, string input, string output, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-i", input, "-map", "0", "-c", "copy"], output, ct);

    /// <summary>Joins a video-only and an audio-only file into one MP4, streams copied.</summary>
    public static Task<bool> MuxAsync(string ffmpeg, string video, string audio, string output, CancellationToken ct) =>
        RunAsync(ffmpeg, ["-i", video, "-i", audio, "-map", "0:v", "-map", "1:a", "-c", "copy"], output, ct);

    /// <summary>
    /// True when ffmpeg produced <paramref name="output"/>. Cancellation kills ffmpeg, removes the
    /// partial output and rethrows, so the download ends Paused rather than Completed or Error.
    /// </summary>
    private static async Task<bool> RunAsync(string ffmpeg, string[] args, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-nostdin", "-y", "-hide_banner", "-loglevel", "error" }
                     .Concat(args).Concat(["-movflags", "+faststart", output]))
            psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        using (process)
        {
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                process.WaitForExit();
                TryDelete(output);
                throw;
            }
            await stderr.ConfigureAwait(false);
            if (process.ExitCode == 0 && File.Exists(output))
                return true;
        }
        TryDelete(output);
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
