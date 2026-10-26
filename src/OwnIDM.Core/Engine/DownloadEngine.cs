using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Engine;

public sealed record ProbeResult(string FileName, long TotalSize, bool ResumeSupported, string FinalUrl, string? ContentType);

/// <summary>The server answered a ranged request with the whole file.</summary>
public sealed class RangeIgnoredException() : IOException("El servidor ignoró la petición de rango.");

/// <summary>
/// Multi-connection HTTP downloader. Each connection serves a <see cref="Segment"/>; an idle
/// connection takes a pending segment or splits the active one with the most bytes left
/// (dynamic segmentation), so all connections stay busy until the end.
/// </summary>
public sealed class DownloadEngine : IDisposable
{
    public const long MinSplitBytes = 256 * 1024;
    private const int BufferSize = 64 * 1024;
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public DownloadEngine(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        // Ranges refer to the encoded bytes; decompressing would break offsets.
        AutomaticDecompression = DecompressionMethods.None,
        // Redirects are followed by hand (see Redirects) to keep cookies on their own site.
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 64,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    };

    private TimeSpan IoTimeout => TimeSpan.FromSeconds(Math.Max(5, _settings.TimeoutSeconds));

    public string TempPathFor(DownloadItem item) =>
        Path.Combine(_settings.TempDirectory, item.Id.ToString("N") + ".part");

    /// <summary>Asks for the first byte to learn name, size and range support.</summary>
    public async Task<ProbeResult> ProbeAsync(string url, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        using var resp = await SendAsync(url, referrer, cookies, userAgent, 0, 0, ct).ConfigureAwait(false);
        var (total, resumable) = ParseSize(resp);
        return new ProbeResult(
            FileNameHelper.FromResponse(resp, url),
            total,
            resumable,
            resp.RequestMessage?.RequestUri?.ToString() ?? url,
            resp.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Downloads (or resumes) <paramref name="item"/>. Never throws: the outcome is reflected in
    /// <see cref="DownloadItem.Status"/> (Completed, Paused when cancelled, Error otherwise).
    /// </summary>
    public Task RunAsync(DownloadItem item, CancellationToken ct) => new Job(this, item, ct).RunAsync();

    public void Dispose() => _http.Dispose();

    private Task<HttpResponseMessage> SendAsync(DownloadItem item, long from, long to, CancellationToken ct) =>
        SendAsync(item.Url, item.Referrer, item.Cookies, item.UserAgent, from, to, ct);

    private async Task<HttpResponseMessage> SendAsync(
        string url, string? referrer, string? cookies, string? userAgent, long from, long to, CancellationToken ct)
    {
        var origin = new Uri(url);
        HttpRequestMessage Build(Uri target)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, target) { Version = HttpVersion.Version11 };
            req.Headers.Range = new RangeHeaderValue(from, to < 0 ? null : to);
            req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (!string.IsNullOrWhiteSpace(referrer))
                req.Headers.TryAddWithoutValidation("Referer", referrer);
            if (!string.IsNullOrWhiteSpace(cookies) && Redirects.CookiesAllowed(target, origin))
                req.Headers.TryAddWithoutValidation("Cookie", cookies);
            return req;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IoTimeout);
        HttpResponseMessage resp;
        try
        {
            resp = await Redirects.SendAsync(_http, origin, Build, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tiempo de espera agotado al conectar con el servidor.");
        }

        // RFC 9110: "bytes=0-" of an empty resource is unsatisfiable, answered with "bytes */0".
        bool emptyFile = resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && from == 0 &&
                         resp.Content.Headers.ContentRange?.Length == 0;
        if (resp.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent) && !emptyFile)
        {
            var code = (int)resp.StatusCode;
            var reason = resp.ReasonPhrase;
            resp.Dispose();
            throw new HttpRequestException($"El servidor respondió {code} {reason}".TrimEnd(), null, (HttpStatusCode)code);
        }
        return resp;
    }

    /// <summary>Total size (-1 if unknown) and whether byte ranges are honoured.</summary>
    private static (long Total, bool Resumable) ParseSize(HttpResponseMessage resp)
    {
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return (0, false);
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is long len)
            return (len, true);
        return (resp.Content.Headers.ContentLength ?? -1, false);
    }

    private sealed class Job
    {
        private readonly DownloadEngine _engine;
        private readonly DownloadItem _item;
        private readonly CancellationToken _ct;
        private readonly AppSettings _settings;
        private readonly RateLimiter _limiter;
        private readonly string _tempPath;
        private SafeFileHandle? _file;
        private volatile bool _canSplit;
        private volatile Exception? _lastError;

        public Job(DownloadEngine engine, DownloadItem item, CancellationToken ct)
        {
            _engine = engine;
            _item = item;
            _ct = ct;
            _settings = engine._settings;
            _limiter = new RateLimiter(() => _settings.EffectiveSpeedLimit);
            _tempPath = engine.TempPathFor(item);
        }

        public async Task RunAsync()
        {
            _item.LastTryAt = DateTime.Now;
            _item.ErrorMessage = null;
            _item.Status = DownloadStatus.Connecting;
            HttpResponseMessage? first = null;
            try
            {
                Directory.CreateDirectory(_settings.TempDirectory);

                bool resume;
                lock (_item.SyncRoot)
                    resume = _item.Segments.Count > 0 && _item.ResumeSupported == true &&
                             _item.TotalSize > 0 && File.Exists(_tempPath);

                Segment? firstSegment = null;
                if (resume)
                {
                    _file = File.OpenHandle(_tempPath, FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
                    _canSplit = true;
                }
                else
                {
                    first = await SendFirstAsync().ConfigureAwait(false);
                    var (total, resumable) = ParseSize(first);
                    if (string.IsNullOrWhiteSpace(_item.FileName))
                        _item.FileName = FileNameHelper.FromResponse(first, _item.Url);
                    _item.TotalSize = total;
                    _item.ResumeSupported = resumable;
                    _item.Downloaded = 0;

                    lock (_item.SyncRoot)
                    {
                        _item.Segments.Clear();
                        if (total != 0)
                        {
                            firstSegment = new Segment { Start = 0, End = total > 0 ? total - 1 : -1, Active = true };
                            _item.Segments.Add(firstSegment);
                        }
                    }
                    _file = File.OpenHandle(_tempPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                        FileOptions.Asynchronous, preallocationSize: Math.Max(0, total));
                    _canSplit = resumable && total > 0;
                }

                _item.Status = DownloadStatus.Downloading;

                var workers = new List<Task>();
                if (firstSegment is not null)
                {
                    workers.Add(WorkerAsync(first, firstSegment));
                    first = null; // owned by the worker now
                }
                int maxConnections = Math.Max(1, _settings.MaxConnectionsPerDownload);
                int extra = _canSplit ? maxConnections - workers.Count : (workers.Count == 0 ? 1 : 0);
                for (int i = 0; i < extra; i++)
                    workers.Add(WorkerAsync(null, null));
                await Task.WhenAll(workers).ConfigureAwait(false);

                _ct.ThrowIfCancellationRequested();

                long finalSize;
                lock (_item.SyncRoot)
                {
                    if (!_item.Segments.All(s => s.IsDone))
                        throw _lastError ?? new IOException("La descarga no se completó.");
                    finalSize = _item.Segments.Count == 0 ? 0 : _item.Segments.Max(s => s.End) + 1;
                }

                _file.Dispose();
                _file = null;
                Finish(finalSize);
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
                first?.Dispose();
                _file?.Dispose();
                lock (_item.SyncRoot)
                    foreach (var s in _item.Segments)
                        s.Active = false;
                _item.RefreshProgress();
                _item.ActiveConnections = 0;
                _item.Speed = 0;
            }
        }

        private void Finish(long finalSize)
        {
            if (_item.TotalSize <= 0)
                _item.TotalSize = finalSize;
            if (string.IsNullOrWhiteSpace(_item.SaveDirectory))
                _item.SaveDirectory = _settings.DirectoryFor(_item.Category);
            Directory.CreateDirectory(_item.SaveDirectory);

            string target;
            for (int attempt = 0; ; attempt++)
            {
                target = FileNameHelper.MakeUnique(_item.FullPath);
                try
                {
                    File.Move(_tempPath, target);
                    break;
                }
                catch (IOException) when (attempt < 5 && File.Exists(target))
                {
                    // Another download took the same name in the meantime; pick the next one.
                }
            }
            _item.FileName = Path.GetFileName(target);

            lock (_item.SyncRoot)
                _item.Segments.Clear();
            _item.Downloaded = _item.TotalSize;
            _item.Status = DownloadStatus.Completed;
        }

        private async Task WorkerAsync(HttpResponseMessage? initial, Segment? pending)
        {
            int failures = 0;
            while (!_ct.IsCancellationRequested)
            {
                var seg = pending ?? Acquire();
                pending = null;
                if (seg is null)
                    return;

                long before = DownloadedOf(seg);
                var response = initial;
                initial = null;
                try
                {
                    await DownloadSegmentAsync(seg, response).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested)
                {
                    return;
                }
                catch (RangeIgnoredException ex)
                {
                    _lastError = ex;
                    _canSplit = false;
                    _item.ResumeSupported = false; // next Start begins again from 0
                    return;
                }
                catch (Exception ex)
                {
                    _lastError = ex;
                    failures = DownloadedOf(seg) > before ? 1 : failures + 1;
                    if (failures > _settings.MaxRetries)
                        return;
                    try
                    {
                        await Task.Delay(Backoff(failures), _ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                finally
                {
                    response?.Dispose();
                    lock (_item.SyncRoot)
                        seg.Active = false;
                }
            }
        }

        private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(failures - 1, 4)));

        private static bool IsTransient(Exception ex) => ex switch
        {
            TimeoutException or IOException => true,
            HttpRequestException { StatusCode: null } => true, // DNS, refused, reset...
            HttpRequestException h => (int)h.StatusCode!.Value >= 500 || h.StatusCode == HttpStatusCode.TooManyRequests,
            _ => false,
        };

        private async Task<HttpResponseMessage> SendFirstAsync()
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await _engine.SendAsync(_item, 0, -1, _ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt <= _settings.MaxRetries && IsTransient(ex) && !_ct.IsCancellationRequested)
                {
                    await Task.Delay(Backoff(attempt), _ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>Takes a pending segment, or splits the active one with the most bytes left.</summary>
        private Segment? Acquire()
        {
            lock (_item.SyncRoot)
            {
                var free = _item.Segments.FirstOrDefault(s => !s.Active && !s.IsDone);
                if (free is not null)
                {
                    free.Active = true;
                    return free;
                }
                if (!_canSplit)
                    return null;

                Segment? biggest = null;
                foreach (var s in _item.Segments)
                    if (s.Active && s.End >= 0 && (biggest is null || s.Remaining > biggest.Remaining))
                        biggest = s;
                if (biggest is null || biggest.Remaining < 2 * MinSplitBytes)
                    return null;

                long mid = biggest.Position + biggest.Remaining / 2;
                var split = new Segment { Start = mid, End = biggest.End, Active = true };
                biggest.End = mid - 1;
                _item.Segments.Insert(_item.Segments.IndexOf(biggest) + 1, split);
                return split;
            }
        }

        private long DownloadedOf(Segment seg)
        {
            lock (_item.SyncRoot)
                return seg.Downloaded;
        }

        private async Task DownloadSegmentAsync(Segment seg, HttpResponseMessage? response)
        {
            long from, to;
            lock (_item.SyncRoot)
            {
                if (seg.IsDone)
                    return;
                from = seg.Position;
                to = seg.End;
            }

            if (response is null)
            {
                response = await _engine.SendAsync(_item, from, to, _ct).ConfigureAwait(false);
                try
                {
                    Validate(response, seg, from);
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }

            using var owned = response;
            await using var stream = await response.Content.ReadAsStreamAsync(_ct).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            try
            {
                while (true)
                {
                    long pos;
                    int want;
                    lock (_item.SyncRoot)
                    {
                        if (seg.IsDone)
                            return; // reached the (possibly shrunk) end
                        pos = seg.Position;
                        want = seg.End < 0 ? BufferSize : (int)Math.Min(BufferSize, seg.Remaining);
                    }
                    long rate = _settings.EffectiveSpeedLimit;
                    if (rate > 0)
                        want = (int)Math.Min(want, Math.Max(1024, rate / 10));

                    int n;
                    readTimeout.CancelAfter(_engine.IoTimeout);
                    try
                    {
                        n = await stream.ReadAsync(buffer.AsMemory(0, want), readTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("El servidor dejó de enviar datos.");
                    }
                    readTimeout.CancelAfter(Timeout.Infinite);

                    if (n == 0)
                    {
                        lock (_item.SyncRoot)
                        {
                            if (seg.End < 0)
                            {
                                // Unknown size: EOF marks the end of the file.
                                if (seg.Position == 0)
                                    _item.Segments.Remove(seg);
                                else
                                    seg.End = seg.Position - 1;
                                return;
                            }
                        }
                        throw new IOException("La conexión se cerró antes de tiempo.");
                    }

                    await _limiter.WaitAsync(n, _ct).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(_file!, buffer.AsMemory(0, n), pos, _ct).ConfigureAwait(false);

                    lock (_item.SyncRoot)
                    {
                        // The segment may have been split meanwhile; bytes past the new end are
                        // identical to what the other connection writes, so only cap the count.
                        long counted = seg.End < 0 ? n : Math.Min(n, seg.End - seg.Position + 1);
                        if (counted > 0)
                            seg.Downloaded += counted;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void Validate(HttpResponseMessage resp, Segment seg, long from)
        {
            if (resp.StatusCode != HttpStatusCode.PartialContent)
            {
                if (resp.Content.Headers.ContentLength is long bodyLength && _item.TotalSize > 0 && bodyLength != _item.TotalSize)
                    throw new IOException("El archivo cambió en el servidor; vuelve a descargarlo.");
                if (from == 0)
                    return; // full body from the start is still usable
                lock (_item.SyncRoot)
                {
                    if (!_canSplit && _item.Segments.Count == 1)
                    {
                        // Single connection without ranges: start the body over from byte 0.
                        seg.Downloaded = 0;
                        return;
                    }
                }
                throw new RangeIgnoredException();
            }
            var range = resp.Content.Headers.ContentRange;
            if (range?.From is long start && start != from)
                throw new IOException("El servidor devolvió un rango distinto al solicitado.");
            if (range?.Length is long len && _item.TotalSize > 0 && len != _item.TotalSize)
                throw new IOException("El archivo cambió en el servidor; vuelve a descargarlo.");
        }
    }
}
