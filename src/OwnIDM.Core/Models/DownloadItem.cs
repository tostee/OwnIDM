using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OwnIDM.Core.Models;

public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Completed,
    Error,
}

public enum DownloadKind
{
    /// <summary>A single file fetched with (possibly several) ranged HTTP connections.</summary>
    Http,
    /// <summary>An HLS (.m3u8) stream: a playlist of media segments joined into one file.</summary>
    Hls,
    /// <summary>A DASH (.mpd) stream: like HLS, a manifest of media segments joined into one file.</summary>
    Dash,
    /// <summary>A video page (YouTube and other sites) downloaded by the external yt-dlp tool.</summary>
    YtDlp,
    /// <summary>A BitTorrent download from a .torrent file or a magnet link: one file or a folder.</summary>
    Torrent,
}

/// <summary>A file inside a torrent: whether to download it and how much of it has arrived.</summary>
public sealed class TorrentFileEntry
{
    /// <summary>Path relative to the torrent folder, as the torrent names it.</summary>
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public bool Selected { get; set; } = true;

    /// <summary>Bytes of verified pieces; refreshed while the torrent runs.</summary>
    public long Downloaded { get; set; }

    /// <summary>
    /// Whether the file was already on disk before OwnIDM first started the torrent (null: not started yet).
    /// Only files OwnIDM created are ever deleted when they were not wanted.
    /// </summary>
    public bool? PreExisting { get; set; }

    /// <summary>Where the engine writes the file, relative to the torrent's root (it escapes odd names); null until started.</summary>
    public string? DiskPath { get; set; }

    public TorrentFileEntry Clone() => new()
    {
        Path = Path, Length = Length, Selected = Selected, Downloaded = Downloaded, PreExisting = PreExisting, DiskPath = DiskPath,
    };
}

/// <summary>A byte range of the file served by one connection.</summary>
public sealed class Segment
{
    public long Start { get; set; }

    /// <summary>Inclusive end offset; -1 while the total size is unknown.</summary>
    public long End { get; set; }

    public long Downloaded { get; set; }

    /// <summary>True while a worker owns the segment.</summary>
    public bool Active { get; internal set; }

    public long Position => Start + Downloaded;
    public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    public bool IsDone => End >= 0 && Position > End;

    public Segment Clone() => new() { Start = Start, End = End, Downloaded = Downloaded, Active = Active };
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _fileName = "";
    private string _saveDirectory = "";
    private string _category = "General";
    private string? _description;
    private long _totalSize = -1;
    private long _downloaded;
    private bool? _resumeSupported;
    private DownloadStatus _status = DownloadStatus.Queued;
    private string? _errorMessage;
    private DateTime _lastTryAt;
    private double _speed;
    private int _activeConnections;
    private bool _inQueue;
    private int _hlsSegmentCount;

    public Guid Id { get; init; } = Guid.NewGuid();
    public DownloadKind Kind { get; init; } = DownloadKind.Http;

    /// <summary>HLS/DASH only: media segments already appended to the .part file and its length then.</summary>
    public int HlsSegmentsDone { get; set; }
    public long HlsBytesDone { get; set; }

    /// <summary>HLS/DASH with a separate audio track: bytes of the audio .part file.</summary>
    public long HlsAudioBytesDone { get; set; }

    /// <summary>yt-dlp only: quality chosen (a height such as "1080", "audio"); null = the best.</summary>
    public string? YtDlpQuality { get; set; }

    /// <summary>yt-dlp only: format ids picked for that quality when probing ("137+140"); null = let yt-dlp choose.</summary>
    public string? YtDlpFormat { get; set; }

    /// <summary>Torrent only: the info-hash (hex), which also names the saved .torrent metadata.</summary>
    public string? InfoHash { get; set; }

    /// <summary>Torrent only: its files in torrent order; always access under <see cref="SyncRoot"/>.</summary>
    public List<TorrentFileEntry> TorrentFiles { get; } = new();

    public int HlsSegmentCount
    {
        get => _hlsSegmentCount;
        set { if (Set(ref _hlsSegmentCount, value)) NotifyDerived(); }
    }
    public string Url { get; set; } = "";

    /// <summary>Address originally captured (an HLS master playlist, a DASH manifest) when <see cref="Url"/> was refined.</summary>
    public string? SourceUrl { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public DateTime AddedAt { get; init; } = DateTime.Now;

    /// <summary>A segmented video stream (HLS or DASH) rather than a single file.</summary>
    public bool IsStream => Kind is DownloadKind.Hls or DownloadKind.Dash;

    /// <summary>Segments owned by the engine; always access under <see cref="SyncRoot"/>.</summary>
    public List<Segment> Segments { get; } = new();

    public object SyncRoot { get; } = new();

    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    public string SaveDirectory { get => _saveDirectory; set => Set(ref _saveDirectory, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string? Description { get => _description; set => Set(ref _description, value); }
    public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }
    public DateTime LastTryAt { get => _lastTryAt; set => Set(ref _lastTryAt, value); }
    public bool InQueue { get => _inQueue; set => Set(ref _inQueue, value); }

    public bool? ResumeSupported { get => _resumeSupported; set => Set(ref _resumeSupported, value); }

    public long TotalSize
    {
        get => _totalSize;
        set { if (Set(ref _totalSize, value)) NotifyDerived(); }
    }

    public long Downloaded
    {
        get => _downloaded;
        set { if (Set(ref _downloaded, value)) NotifyDerived(); }
    }

    public DownloadStatus Status
    {
        get => _status;
        set { if (Set(ref _status, value)) NotifyDerived(); }
    }

    /// <summary>Bytes per second, refreshed by the manager tick.</summary>
    public double Speed
    {
        get => _speed;
        set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(TimeLeft)); }
    }

    public int ActiveConnections { get => _activeConnections; set => Set(ref _activeConnections, value); }

    public double Progress =>
        IsStream && HlsSegmentCount > 0 && Status != DownloadStatus.Completed
            ? Math.Min(100.0, HlsSegmentsDone * 100.0 / HlsSegmentCount)
            : TotalSize > 0 ? Math.Min(100.0, Downloaded * 100.0 / TotalSize) : 0;

    public TimeSpan? TimeLeft =>
        Status == DownloadStatus.Downloading && TotalSize > 0 && Speed > 1
            ? TimeSpan.FromSeconds((TotalSize - Downloaded) / Speed)
            : null;

    public string FullPath => Path.Combine(SaveDirectory, FileName);

    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    public List<Segment> SnapshotSegments()
    {
        lock (SyncRoot)
            return Segments.Select(s => s.Clone()).ToList();
    }

    public List<TorrentFileEntry> SnapshotTorrentFiles()
    {
        lock (SyncRoot)
            return TorrentFiles.Select(f => f.Clone()).ToList();
    }

    /// <summary>Recomputes <see cref="Downloaded"/> and <see cref="ActiveConnections"/> from the segments.</summary>
    public void RefreshProgress()
    {
        long sum;
        int active;
        lock (SyncRoot)
        {
            if (Segments.Count == 0)
                return;
            sum = Segments.Sum(s => s.Downloaded);
            active = Segments.Count(s => s.Active);
        }
        Downloaded = sum;
        ActiveConnections = active;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises change notifications after the stream (HLS/DASH) downloader advanced.</summary>
    public void NotifyProgressChanged() => NotifyDerived();

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(TimeLeft));
        OnPropertyChanged(nameof(IsActive));
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
