using System.Collections.ObjectModel;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Tools;
using OwnIDM.Core.Torrents;
using OwnIDM.Core.YtDlp;

namespace OwnIDM.Core.Manager;

/// <summary>
/// Owns the download list, the main queue and the running jobs.
/// Call its members from the UI thread; job continuations come back to it.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private sealed class Running
    {
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        public long LastBytes { get; set; }
        public DateTime LastSample { get; set; }
    }

    private readonly Dictionary<Guid, Running> _running = new();
    private readonly Persistence _store;
    private bool _dirty;
    // Current queue run: completed queue items, manual interventions, and a completion
    // waiting for unrelated downloads to finish. The completion action can power the PC off,
    // so it only fires for a run that ended on its own with real work done.
    private int _queueRunCompleted;
    private bool _queueRunInterrupted;
    private bool _completionPending;
    private DateTime _lastSave = DateTime.MinValue;

    public DownloadManager(Persistence store)
    {
        _store = store;
        Settings = store.LoadSettings();
        Engine = new DownloadEngine(Settings);
        Hls = new HlsDownloader(Settings);
        YtDlp = new YtDlpDownloader(Settings);
        Tools = new ToolManager(Settings, Engine);
        Torrents = new TorrentDownloader(Settings);
        foreach (var item in store.LoadDownloads())
            Items.Add(item);
    }

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AppSettings Settings { get; }
    public DownloadEngine Engine { get; }
    public HlsDownloader Hls { get; }
    public YtDlpDownloader YtDlp { get; }
    public ToolManager Tools { get; }
    public TorrentDownloader Torrents { get; }
    public bool QueueRunning { get; private set; }

    /// <summary>Raised when a download completes or fails.</summary>
    public event Action<DownloadItem>? DownloadFinished;

    public event Action? QueueStateChanged;

    /// <summary>Raised when a queue run that started at least one download has nothing left to do.</summary>
    public event Action? QueueCompleted;

    public bool IsRunning(DownloadItem item) => _running.ContainsKey(item.Id);

    public DownloadItem CreateItem(string url, string? fileName = null, string? referrer = null,
        string? cookies = null, string? userAgent = null, DownloadKind kind = DownloadKind.Http)
    {
        var name = FileNameHelper.Sanitize(kind == DownloadKind.Torrent ? TorrentLink.DisplayName(url)
            : string.IsNullOrWhiteSpace(fileName) ? FileNameHelper.FromUrl(url) : fileName);
        var category = Settings.CategoryFor(name);
        return new DownloadItem
        {
            Kind = kind,
            Url = url,
            FileName = name,
            Category = category,
            SaveDirectory = Settings.DirectoryFor(category),
            Referrer = referrer,
            Cookies = cookies,
            UserAgent = userAgent,
        };
    }

    public DownloadItem? FindByUrl(string url) =>
        Items.FirstOrDefault(i => string.Equals(i.Url, url, StringComparison.Ordinal) ||
                                  string.Equals(i.SourceUrl, url, StringComparison.Ordinal));

    public void Add(DownloadItem item, bool startNow)
    {
        Items.Add(item);
        if (startNow)
        {
            Start(item);
        }
        else
        {
            item.InQueue = true;
            item.Status = DownloadStatus.Queued;
            Pump();
        }
        MarkDirty();
    }

    public void Start(DownloadItem item)
    {
        if (_running.ContainsKey(item.Id) || item.Status == DownloadStatus.Completed)
            return;

        var running = new Running
        {
            Cts = new CancellationTokenSource(),
            LastBytes = item.Downloaded,
            LastSample = DateTime.UtcNow,
        };
        _running[item.Id] = running;
        running.Task = RunAsync(item, running);
    }

    private async Task RunAsync(DownloadItem item, Running running)
    {
        try
        {
            if (item.IsStream)
                await Hls.RunAsync(item, running.Cts.Token);
            else if (item.Kind == DownloadKind.YtDlp)
                await RunYtDlpAsync(item, running.Cts.Token);
            else if (item.Kind == DownloadKind.Torrent)
                await Torrents.RunAsync(item, running.Cts.Token);
            else
                await Engine.RunAsync(item, running.Cts.Token);
        }
        finally
        {
            _running.Remove(item.Id);
            running.Cts.Dispose();
            item.Speed = 0;
            item.ActiveConnections = 0;
            // Listeners still see InQueue, e.g. to skip the "completed" dialog during queue runs.
            if (item.Status is DownloadStatus.Completed or DownloadStatus.Error)
                DownloadFinished?.Invoke(item);
            if (item.Status == DownloadStatus.Completed)
            {
                if (item.InQueue && QueueRunning)
                    _queueRunCompleted++;
                item.InQueue = false;
                item.Cookies = null; // session cookies are not kept on disk after use
            }
            MarkDirty();
            SaveIfDirty(force: true);
            Pump();
        }
    }

    /// <summary>The user changed a queue item mid-run: that run must not trigger the completion action.</summary>
    private void NoteInterruption(DownloadItem item)
    {
        if (QueueRunning && item.InQueue)
            _queueRunInterrupted = true;
    }

    public void Pause(DownloadItem item)
    {
        NoteInterruption(item);
        if (_running.TryGetValue(item.Id, out var running))
            running.Cts.Cancel();
        else if (item.Status == DownloadStatus.Queued)
            item.Status = DownloadStatus.Paused;
        MarkDirty();
    }

    public void PauseAll()
    {
        _completionPending = false;
        SetQueueRunning(false);
        foreach (var r in _running.Values.ToList())
            r.Cts.Cancel();
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Queued))
            item.Status = DownloadStatus.Paused;
        MarkDirty();
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        NoteInterruption(item);
        await StopAndWaitAsync(item);
        Items.Remove(item);
        if (item.Kind == DownloadKind.Torrent)
        {
            // Another entry of the same torrent shares its metadata, fast-resume data and files.
            if (FindTorrent(TorrentHash(item)) is null)
            {
                Torrents.DeleteCache(item, keepMetadata: false);
                // A torrent writes straight into its folder: an unfinished one loses what it downloaded
                // there, as any other download loses its temporary file.
                if (deleteFile)
                    TorrentDownloader.DeleteDownloadedFiles(item);
                else if (item.Status != DownloadStatus.Completed)
                    TorrentDownloader.DeleteCreatedFiles(item);
            }
        }
        else
        {
            DeleteTempFiles(item);
            if (deleteFile && item.Status == DownloadStatus.Completed)
                TryDelete(item.FullPath);
        }
        MarkDirty();
        SaveIfDirty(force: true);
    }

    public void RemoveCompleted()
    {
        foreach (var item in Items.Where(i => i.Status == DownloadStatus.Completed).ToList())
        {
            Items.Remove(item);
            if (item.Kind == DownloadKind.Torrent && FindTorrent(TorrentHash(item)) is null)
                Torrents.DeleteCache(item, keepMetadata: false);
        }
        MarkDirty();
    }

    private static string? TorrentHash(DownloadItem item) =>
        item.Kind == DownloadKind.Torrent ? item.InfoHash ?? TorrentLink.MagnetHash(item.Url) : null;

    /// <summary>The entry of the list for this info-hash, if any (a magnet and a .torrent can be the same torrent).</summary>
    public DownloadItem? FindTorrent(string? infoHash) =>
        infoHash is null ? null : Items.FirstOrDefault(i => string.Equals(TorrentHash(i), infoHash, StringComparison.OrdinalIgnoreCase));

    public async Task RedownloadAsync(DownloadItem item)
    {
        NoteInterruption(item);
        await StopAndWaitAsync(item);
        DeleteTempFiles(item);
        lock (item.SyncRoot)
        {
            item.Segments.Clear();
            item.HlsSegmentsDone = 0;
            item.HlsBytesDone = 0;
            item.HlsAudioBytesDone = 0;
        }
        item.Downloaded = 0;
        item.ErrorMessage = null;
        item.Status = DownloadStatus.Paused;
        Start(item);
    }

    /// <summary>
    /// A failure is often YouTube having changed: with a yt-dlp OwnIDM installed, look for a newer one
    /// (at most hourly) and retry once with it.
    /// </summary>
    private async Task RunYtDlpAsync(DownloadItem item, CancellationToken ct)
    {
        await YtDlp.RunAsync(item, ct);
        if (item.Status != DownloadStatus.Error || ct.IsCancellationRequested)
            return;
        bool updated;
        try
        {
            updated = await Tools.UpdateYtDlpAsync(TimeSpan.FromHours(1), ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return;
        }
        if (updated)
            await YtDlp.RunAsync(item, ct);
    }

    public string TempPathFor(DownloadItem item) =>
        item.IsStream ? Hls.TempPathFor(item) : Engine.TempPathFor(item);

    private void DeleteTempFiles(DownloadItem item)
    {
        if (item.Kind == DownloadKind.Torrent)
        {
            // No temporary file: dropping the fast-resume data makes the next start check the files,
            // keeping the good pieces and fetching the rest again.
            Torrents.DeleteCache(item, keepMetadata: true);
            return;
        }
        if (item.Kind == DownloadKind.YtDlp)
        {
            YtDlpDownloader.TryDeleteDirectory(YtDlp.TempDirectoryFor(item));
            return;
        }
        TryDelete(TempPathFor(item));
        if (item.IsStream)
            TryDelete(Hls.AudioTempPathFor(item));
    }

    /// <summary>Adds an item to (or takes it out of) the main queue.</summary>
    public void SetInQueue(DownloadItem item, bool inQueue)
    {
        if (inQueue && item.Status == DownloadStatus.Completed)
            return;
        if (!inQueue)
            NoteInterruption(item);
        item.InQueue = inQueue;
        if (inQueue && !_running.ContainsKey(item.Id) && item.Status is DownloadStatus.Paused or DownloadStatus.Error)
            item.Status = DownloadStatus.Queued;
        else if (!inQueue && item.Status == DownloadStatus.Queued)
            item.Status = DownloadStatus.Paused;
        MarkDirty();
        Pump();
    }

    /// <summary>Moves an item one place up (-1) or down (+1) among the queued items.</summary>
    public void MoveInQueue(DownloadItem item, int direction)
    {
        var queue = Items.Where(i => i.InQueue).ToList();
        int index = queue.IndexOf(item);
        int target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= queue.Count)
            return;
        Items.Move(Items.IndexOf(item), Items.IndexOf(queue[target]));
        MarkDirty();
    }

    public void StartQueue()
    {
        if (!QueueRunning)
        {
            _queueRunCompleted = 0;
            _queueRunInterrupted = false;
        }
        _completionPending = false;
        foreach (var item in Items.Where(i => i.InQueue && i.Status is DownloadStatus.Paused or DownloadStatus.Error))
            item.Status = DownloadStatus.Queued;
        SetQueueRunning(true);
        Pump();
    }

    public void StopQueue()
    {
        _completionPending = false;
        SetQueueRunning(false);
        foreach (var item in Items.Where(i => i.InQueue))
            Pause(item);
    }

    private void SetQueueRunning(bool value)
    {
        if (QueueRunning == value)
            return;
        QueueRunning = value;
        QueueStateChanged?.Invoke();
    }

    /// <summary>Starts queued downloads up to <see cref="AppSettings.MaxConcurrentDownloads"/>.</summary>
    private void Pump()
    {
        if (!QueueRunning)
        {
            CheckCompletion();
            return;
        }
        int active = Items.Count(i => i.InQueue && _running.ContainsKey(i.Id));
        foreach (var next in Items.Where(i => i.InQueue && i.Status == DownloadStatus.Queued).ToList())
        {
            if (!QueueRunning || active >= Math.Max(1, Settings.MaxConcurrentDownloads))
                break;
            // A job that ends synchronously re-enters Pump, so re-check against live state.
            if (next.Status != DownloadStatus.Queued || _running.ContainsKey(next.Id))
                continue;
            Start(next);
            active = Items.Count(i => i.InQueue && _running.ContainsKey(i.Id));
        }
        if (active == 0 && QueueRunning)
        {
            SetQueueRunning(false);
            // Only a run that finished at least one download and was not interrupted counts;
            // a run where everything failed (e.g. no network at 2 am) does not power the PC off.
            _completionPending = _queueRunCompleted > 0 && !_queueRunInterrupted;
        }
        CheckCompletion();
    }

    /// <summary>Raises <see cref="QueueCompleted"/> once no download at all is running.</summary>
    private void CheckCompletion()
    {
        if (!_completionPending || QueueRunning || _running.Count > 0)
            return;
        _completionPending = false;
        QueueCompleted?.Invoke();
    }

    /// <summary>Refreshes progress and speed of running downloads; call it a few times per second.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var item in Items)
        {
            if (!_running.TryGetValue(item.Id, out var r))
                continue;
            item.RefreshProgress();
            if (item.Kind == DownloadKind.Torrent)
                continue; // the torrent engine measures its own speed; verified pieces arrive in bursts
            double dt = (now - r.LastSample).TotalSeconds;
            if (dt >= 0.25)
            {
                double instant = Math.Max(0, item.Downloaded - r.LastBytes) / dt;
                item.Speed = item.Speed <= 0 ? instant : item.Speed * 0.7 + instant * 0.3;
                r.LastBytes = item.Downloaded;
                r.LastSample = now;
            }
        }
        if (_running.Count > 0)
            _dirty = true;
        SaveIfDirty(force: false);
    }

    public void MarkDirty() => _dirty = true;

    public void SaveSettings() => _store.SaveSettings(Settings);

    public void SaveIfDirty(bool force)
    {
        if (!_dirty || (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(5)))
            return;
        _dirty = false;
        _lastSave = DateTime.UtcNow;
        _store.SaveDownloads(Items.ToList());
    }

    public async Task ShutdownAsync()
    {
        SetQueueRunning(false);
        var tasks = _running.Values.Select(r => { r.Cts.Cancel(); return r.Task; }).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(5)));
        _dirty = true;
        SaveIfDirty(force: true);
        SaveSettings();
    }

    private async Task StopAndWaitAsync(DownloadItem item)
    {
        if (_running.TryGetValue(item.Id, out var running))
        {
            running.Cts.Cancel();
            await running.Task;
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
        Engine.Dispose();
        Hls.Dispose();
        Tools.Dispose();
        Torrents.Dispose();
    }
}
