using System.Net;
using MonoTorrent;
using MonoTorrent.Client;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Torrents;

/// <summary>What a torrent holds, read before adding it so the user can pick the files.</summary>
public sealed record TorrentProbe(string Name, string InfoHash, long TotalSize, List<TorrentFileEntry> Files, bool IsPrivate);

/// <summary>Recognising torrent sources: magnet links, .torrent files on disk and .torrent addresses.</summary>
public static class TorrentLink
{
    public static bool IsMagnet(string? text) =>
        text?.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>An existing .torrent file on this computer.</summary>
    public static bool IsTorrentFile(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) &&
        Path.IsPathFullyQualified(text) && File.Exists(text);

    /// <summary>An http(s) address of a .torrent file, by its path or by the file name the browser announced.</summary>
    public static bool IsTorrentUrl(string url, string? fileName = null) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" &&
        (u.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) ||
         fileName?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>The info-hash a download is known by: hex, lower case (v1 for hybrid torrents).</summary>
    public static string Hash(InfoHashes hashes) => hashes.V1OrV2.ToHex().ToLowerInvariant();

    /// <summary>The info-hash of a magnet link, or null when it is not a valid one.</summary>
    public static string? MagnetHash(string text) =>
        IsMagnet(text) && MagnetLink.TryParse(text.Trim(), out var magnet) ? Hash(magnet.InfoHashes) : null;

    /// <summary>The name shown until the metadata arrives: the magnet's "dn", else the .torrent file name.</summary>
    public static string DisplayName(string source)
    {
        if (IsMagnet(source))
        {
            if (!MagnetLink.TryParse(source.Trim(), out var magnet))
                return "torrent";
            return string.IsNullOrWhiteSpace(magnet.Name) ? "torrent " + Hash(magnet.InfoHashes)[..8] : magnet.Name;
        }
        var name = Uri.TryCreate(source, UriKind.Absolute, out var u) && !u.IsFile ? FileNameHelper.FromUrl(source) : Path.GetFileName(source);
        return Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : "torrent";
    }

    /// <summary>The biggest selected file, which decides the category (a film with its subtitles is "Vídeo").</summary>
    public static string? MainFile(IEnumerable<TorrentFileEntry> files) =>
        files.Where(f => f.Selected).MaxBy(f => f.Length)?.Path;
}

/// <summary>
/// Downloads torrents with MonoTorrent: one shared engine (DHT, port mapping) and one TorrentManager per
/// running download. Data goes straight to the destination folder; pausing keeps fast-resume data, so
/// resuming does not check the files again.
/// </summary>
public sealed class TorrentDownloader : IDisposable
{
    /// <summary>How long a magnet link may look for peers that send its file list before giving up.</summary>
    public static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(3);

    private const int MaxTorrentFileBytes = 32 * 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _engineLock = new(1, 1);
    private ClientEngine? _engine;

    public TorrentDownloader(AppSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = new HttpClient(handler ?? DownloadEngine.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>Raised with each torrent once it has started (diagnostics, and tests that add a local peer).</summary>
    public event Action<TorrentManager>? Started;

    public string MetadataPathFor(string infoHash) => Path.Combine(_settings.TorrentDirectory, infoHash + ".torrent");

    private string CacheDirectory => Path.Combine(_settings.TorrentDirectory, "cache");

    // ---------------------------------------------------------------- probing

    /// <summary>
    /// Reads the torrent (for a magnet link, asks peers for its metadata) and keeps a copy of it, so the
    /// download starts from it without fetching it again.
    /// </summary>
    public async Task<TorrentProbe> ProbeAsync(string source, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        var data = await ReadMetadataAsync(source.Trim(), referrer, cookies, userAgent, ct).ConfigureAwait(false);
        var torrent = Parse(data);
        var hash = TorrentLink.Hash(torrent.InfoHashes);
        ct.ThrowIfCancellationRequested(); // its dialog closed: nobody would ever clean the copy up
        SaveMetadata(hash, data);
        var files = torrent.Files.Select(f => new TorrentFileEntry { Path = f.Path, Length = f.Length }).ToList();
        return new TorrentProbe(torrent.Name, hash, torrent.Size, files, torrent.IsPrivate);
    }

    private async Task<byte[]> ReadMetadataAsync(string source, string? referrer, string? cookies, string? userAgent, CancellationToken ct)
    {
        if (TorrentLink.IsMagnet(source))
        {
            if (!MagnetLink.TryParse(source, out var magnet))
                throw new InvalidDataException("El enlace magnet no es válido.");
            var saved = MetadataPathFor(TorrentLink.Hash(magnet.InfoHashes));
            if (File.Exists(saved))
                return await File.ReadAllBytesAsync(saved, ct).ConfigureAwait(false);
            var engine = await EngineAsync().ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MetadataTimeout);
            try
            {
                return (await engine.DownloadMetadataAsync(magnet, timeout.Token).ConfigureAwait(false)).ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Ningún par envió la lista de archivos del torrent. Puede que no quede nadie compartiéndolo.");
            }
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DownloadEngine.DefaultUserAgent : userAgent);
            if (!string.IsNullOrWhiteSpace(referrer))
                request.Headers.TryAddWithoutValidation("Referer", referrer);
            if (!string.IsNullOrWhiteSpace(cookies))
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
                throw new InvalidDataException("El archivo .torrent es demasiado grande.");
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return data.Length <= MaxTorrentFileBytes ? data : throw new InvalidDataException("El archivo .torrent es demasiado grande.");
        }
        if (Path.IsPathFullyQualified(source) && File.Exists(source))
        {
            if (new FileInfo(source).Length > MaxTorrentFileBytes)
                throw new InvalidDataException("El archivo .torrent es demasiado grande.");
            return await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false);
        }
        throw new FileNotFoundException("No se encontró el archivo .torrent: " + source);
    }

    private static Torrent Parse(byte[] data) =>
        Torrent.TryLoad(data, out var torrent) ? torrent : throw new InvalidDataException("El archivo no es un torrent válido.");

    /// <summary>Written whole or not at all: a download may start while it is being saved.</summary>
    private void SaveMetadata(string infoHash, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(_settings.TorrentDirectory);
            var path = MetadataPathFor(infoHash);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- downloading

    /// <summary>Never throws; the outcome is reflected in <see cref="DownloadItem.Status"/>.</summary>
    public async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        item.LastTryAt = DateTime.Now;
        item.ErrorMessage = null;
        item.Status = DownloadStatus.Connecting;
        item.ResumeSupported = true; // verified pieces are kept, whatever the peers
        ClientEngine? engine = null;
        TorrentManager? manager = null;
        try
        {
            engine = await EngineAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(item.SaveDirectory))
                item.SaveDirectory = _settings.DirectoryFor(item.Category);
            Directory.CreateDirectory(item.SaveDirectory);

            var torrent = await LoadForRunAsync(item, ct).ConfigureAwait(false);
            if (torrent is not null)
            {
                await WaitUntilFreeAsync(engine, torrent.InfoHashes, ct).ConfigureAwait(false);
                manager = await engine.AddAsync(torrent, item.SaveDirectory, TorrentSettings()).ConfigureAwait(false);
                await ApplyFilesAsync(item, manager).ConfigureAwait(false);
            }
            else
            {
                // A magnet link whose metadata was not fetched before: it arrives once peers are found.
                var magnet = MagnetLink.Parse(item.Url.Trim());
                item.InfoHash ??= TorrentLink.Hash(magnet.InfoHashes);
                await WaitUntilFreeAsync(engine, magnet.InfoHashes, ct).ConfigureAwait(false);
                manager = await engine.AddAsync(magnet, item.SaveDirectory, TorrentSettings()).ConfigureAwait(false);
            }

            await manager.StartAsync().ConfigureAwait(false);
            Started?.Invoke(manager);
            if (torrent is null)
            {
                // The engine may also have had the metadata in its own cache (HasMetadata already true).
                if (!manager.HasMetadata)
                    await manager.WaitForMetadataAsync(ct).ConfigureAwait(false);
                if (File.Exists(manager.MetadataPath))
                    SaveMetadata(item.InfoHash!, await File.ReadAllBytesAsync(manager.MetadataPath, CancellationToken.None).ConfigureAwait(false));
                await ApplyFilesAsync(item, manager).ConfigureAwait(false);
            }

            while (!Refresh(item, manager))
            {
                if (manager.State == TorrentState.Error)
                    throw new IOException(manager.Error?.Exception?.Message ?? "El torrent se detuvo por un error.");
                // Options (and the speed limiter) can change while torrents run.
                var wanted = TorrentSettings();
                if (!SameLimits(manager.Settings, wanted))
                    await manager.UpdateSettingsAsync(wanted).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            // Stop before reporting completion, so the files are closed when the user opens them.
            await StopAsync(engine, manager, forget: true).ConfigureAwait(false);
            manager = null;
            DeleteUnselectedFragments(item);
            item.Status = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            item.Status = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            item.Status = DownloadStatus.Error;
        }
        finally
        {
            if (engine is not null && manager is not null)
                await StopAsync(engine, manager, forget: false).ConfigureAwait(false);
            item.ActiveConnections = 0;
            item.Speed = 0;
            item.NotifyProgressChanged();
        }
    }

    /// <summary>The saved metadata, else the .torrent read again; null for a magnet link not probed yet.</summary>
    private async Task<Torrent?> LoadForRunAsync(DownloadItem item, CancellationToken ct)
    {
        var hash = item.InfoHash ?? TorrentLink.MagnetHash(item.Url);
        if (hash is not null && File.Exists(MetadataPathFor(hash)) &&
            Torrent.TryLoad(await File.ReadAllBytesAsync(MetadataPathFor(hash), ct).ConfigureAwait(false), out var saved))
            return saved;
        if (TorrentLink.IsMagnet(item.Url))
            return null;
        var data = await ReadMetadataAsync(item.Url.Trim(), item.Referrer, item.Cookies, item.UserAgent, ct).ConfigureAwait(false);
        var torrent = Parse(data);
        item.InfoHash = TorrentLink.Hash(torrent.InfoHashes);
        SaveMetadata(item.InfoHash, data);
        return torrent;
    }

    /// <summary>
    /// A probe that was just cancelled (its dialog closed on "Iniciar descarga") still holds the torrent
    /// in the engine until it stops, which waits up to a minute for the trackers' "stopped" reply.
    /// </summary>
    private static async Task WaitUntilFreeAsync(ClientEngine engine, InfoHashes hashes, CancellationToken ct)
    {
        for (int i = 0; engine.Contains(hashes) && i < 375; i++) // 75 s
            await Task.Delay(200, ct).ConfigureAwait(false);
        if (engine.Contains(hashes))
            throw new IOException("Este torrent ya se está descargando.");
    }

    /// <summary>Fills the file list the first time and applies the user's choice of files.</summary>
    private static async Task ApplyFilesAsync(DownloadItem item, TorrentManager manager)
    {
        var files = manager.Files;
        // A single file goes straight into the folder; several go into a folder named after the torrent.
        var root = files.Count == 1 ? manager.SavePath : manager.ContainingDirectory;
        List<bool> selected;
        lock (item.SyncRoot)
        {
            if (item.TorrentFiles.Count != files.Count)
            {
                item.TorrentFiles.Clear();
                item.TorrentFiles.AddRange(files.Select(f => new TorrentFileEntry { Path = f.Path, Length = f.Length }));
            }
            for (int i = 0; i < files.Count; i++)
            {
                var entry = item.TorrentFiles[i];
                entry.DiskPath = Path.GetRelativePath(root, files[i].FullPath);
                // Checked once, before the engine first writes: later runs find OwnIDM's own files there.
                entry.PreExisting ??= File.Exists(files[i].FullPath);
            }
            selected = item.TorrentFiles.Select(f => f.Selected).ToList();
        }
        for (int i = 0; i < files.Count; i++)
            await manager.SetFilePriorityAsync(files[i], selected[i] ? Priority.Normal : Priority.DoNotDownload).ConfigureAwait(false);
        item.InfoHash ??= TorrentLink.Hash(manager.InfoHashes);
        item.FileName = Path.GetFileName(files.Count == 1 ? files[0].FullPath : manager.ContainingDirectory);
        Refresh(item, manager);
    }

    /// <summary>Copies the torrent's progress into the item; true once every selected file is complete.</summary>
    private static bool Refresh(DownloadItem item, TorrentManager manager)
    {
        item.ActiveConnections = manager.OpenConnections;
        if (!manager.HasMetadata || manager.Torrent is null)
            return false;
        var bitfield = manager.Bitfield;
        int pieceLength = manager.Torrent.PieceLength;
        var files = manager.Files;
        long total = 0, done = 0;
        bool complete = true;
        lock (item.SyncRoot)
        {
            // Not set up yet (or no file wanted): nothing can count as finished.
            if (item.TorrentFiles.Count != files.Count || !item.TorrentFiles.Any(f => f.Selected))
                return false;
            for (int i = 0; i < item.TorrentFiles.Count && i < files.Count; i++)
            {
                var entry = item.TorrentFiles[i];
                entry.Downloaded = VerifiedBytes(files[i], bitfield, pieceLength);
                if (!entry.Selected)
                    continue;
                total += entry.Length;
                done += entry.Downloaded;
                complete &= entry.Downloaded >= entry.Length;
            }
        }
        item.TotalSize = total;
        item.Downloaded = done;
        item.Speed = manager.Monitor.DownloadRate;
        item.Status = manager.State is TorrentState.Downloading or TorrentState.Seeding
            ? DownloadStatus.Downloading
            : DownloadStatus.Connecting; // starting, checking the files, looking for metadata
        return complete;
    }

    /// <summary>Bytes of <paramref name="file"/> covered by verified pieces (a piece can span two files).</summary>
    public static long VerifiedBytes(ITorrentFile file, ReadOnlyBitField bitfield, int pieceLength)
    {
        if (file.Length == 0)
            return 0;
        long start = file.OffsetInTorrent, end = start + file.Length, sum = 0;
        for (int p = file.StartPieceIndex; p <= file.EndPieceIndex && p < bitfield.Length; p++)
        {
            if (!bitfield[p])
                continue;
            long pieceStart = (long)p * pieceLength;
            sum += Math.Max(0, Math.Min(end, pieceStart + pieceLength) - Math.Max(start, pieceStart));
        }
        return sum;
    }

    /// <param name="forget">Completed: drop the fast-resume data. Paused: keep it to resume without checking.</param>
    private static async Task StopAsync(ClientEngine engine, TorrentManager manager, bool forget)
    {
        try
        {
            if (manager.State is not (TorrentState.Stopped or TorrentState.Stopping))
                await manager.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await engine.RemoveAsync(manager, forget ? RemoveMode.CacheDataOnly : RemoveMode.KeepAllData).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TorrentException or OperationCanceledException)
        {
            // Best effort: the data on disk is what matters, and it is already written.
        }
    }

    // ---------------------------------------------------------------- engine and settings

    private int DownloadRate => (int)Math.Min(int.MaxValue, _settings.EffectiveSpeedLimit);

    private static bool SameLimits(TorrentSettings a, TorrentSettings b) =>
        a.MaximumDownloadRate == b.MaximumDownloadRate && a.MaximumUploadRate == b.MaximumUploadRate &&
        a.MaximumConnections == b.MaximumConnections && a.AllowDht == b.AllowDht && a.AllowPeerExchange == b.AllowPeerExchange;

    private TorrentSettings TorrentSettings() => new TorrentSettingsBuilder
    {
        AllowDht = _settings.TorrentDht,
        AllowPeerExchange = _settings.TorrentDht,
        CreateContainingDirectory = true,
        MaximumConnections = Math.Clamp(_settings.TorrentMaxConnections, 5, 500),
        MaximumDownloadRate = DownloadRate,
        MaximumUploadRate = (int)Math.Min(int.MaxValue, Math.Max(0, _settings.TorrentUploadLimitKBps) * 1024L),
    }.ToSettings();

    private EngineSettings EngineSettings()
    {
        int port = Math.Clamp(_settings.TorrentPort, 0, 65535); // 0: any free port
        return new EngineSettingsBuilder
        {
            CacheDirectory = CacheDirectory,
            AllowPortForwarding = _settings.TorrentPortForwarding,
            AllowLocalPeerDiscovery = _settings.TorrentDht,
            AutoSaveLoadDhtCache = true,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            ListenEndPoints = new()
            {
                ["ipv4"] = new IPEndPoint(IPAddress.Any, port),
                ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, port),
            },
            DhtEndPoint = _settings.TorrentDht ? new IPEndPoint(IPAddress.Any, port) : null,
        }.ToSettings();
    }

    private async Task<ClientEngine> EngineAsync()
    {
        await _engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is null)
            {
                Directory.CreateDirectory(CacheDirectory);
                _engine = new ClientEngine(EngineSettings());
            }
            return _engine;
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>Applies changed options (port, DHT, port mapping) to a running engine.</summary>
    public async Task ApplySettingsAsync()
    {
        if (_engine is { } engine)
            await engine.UpdateSettingsAsync(EngineSettings()).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- clean-up

    /// <summary>Drops fast-resume data (the next start checks the files) and, unless kept, the saved metadata.</summary>
    public void DeleteCache(DownloadItem item, bool keepMetadata)
    {
        var hash = item.InfoHash ?? TorrentLink.MagnetHash(item.Url);
        if (hash is null)
            return;
        if (!keepMetadata)
            TryDelete(MetadataPathFor(hash));
        if (!Directory.Exists(CacheDirectory))
            return;
        foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories))
            if (Path.GetFileName(file).Contains(hash, StringComparison.OrdinalIgnoreCase))
                TryDelete(file);
    }

    /// <summary>
    /// "Delete the files": the ones the user chose, plus any OwnIDM created for files not chosen. Files of
    /// the torrent that were already on disk and were not chosen are the user's and stay, as does anything
    /// the torrent does not list. Folders left empty go.
    /// </summary>
    public static void DeleteDownloadedFiles(DownloadItem item) =>
        DeleteFiles(item, f => f.Selected || f.PreExisting == false);

    /// <summary>
    /// Removing an unfinished torrent loses what it downloaded, as with any download: the files OwnIDM
    /// created go; files that were already there stay.
    /// </summary>
    public static void DeleteCreatedFiles(DownloadItem item) => DeleteFiles(item, f => f.PreExisting == false);

    /// <summary>
    /// A piece shared with a file the user did not want makes the engine create that file too, with only
    /// a fragment in it. Such files are removed once the torrent completes.
    /// </summary>
    private static void DeleteUnselectedFragments(DownloadItem item) =>
        DeleteFiles(item, f => !f.Selected && f.PreExisting == false);

    private static void DeleteFiles(DownloadItem item, Func<TorrentFileEntry, bool> which)
    {
        var files = FilesOnDisk(item, out var root);
        foreach (var (file, path) in files)
            if (which(file))
                TryDelete(path);
        if (files.Count > 1)
            DeleteEmptyFolders(root);
    }

    /// <summary>Each file of the torrent with its full path; none that would land outside the torrent's folder.</summary>
    private static List<(TorrentFileEntry File, string Path)> FilesOnDisk(DownloadItem item, out string root)
    {
        var files = item.SnapshotTorrentFiles();
        root = "";
        if (files.Count == 0 || string.IsNullOrWhiteSpace(item.SaveDirectory) || string.IsNullOrWhiteSpace(item.FileName))
            return [];
        root = Path.GetFullPath(files.Count == 1 ? item.SaveDirectory : item.FullPath);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = new List<(TorrentFileEntry, string)>();
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.DiskPath ?? file.Path));
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) // no "..\" escapes
                result.Add((file, path));
        }
        return result;
    }

    private static void DeleteEmptyFolders(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

    public void Dispose()
    {
        _engine?.Dispose();
        _http.Dispose();
        _engineLock.Dispose();
    }
}
