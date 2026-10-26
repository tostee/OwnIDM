using OwnIDM.Core.Grabber;
using OwnIDM.Core.Scheduling;

namespace OwnIDM.Core.Settings;

public sealed class Category
{
    public string Name { get; set; } = "";

    /// <summary>Space separated extensions without dot, e.g. "zip rar 7z".</summary>
    public string Extensions { get; set; } = "";

    /// <summary>Optional folder; empty means "&lt;default folder&gt;\&lt;Name&gt;".</summary>
    public string Directory { get; set; } = "";

    public bool Matches(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 &&
               Extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AppSettings
{
    public const string GeneralCategory = "General";

    public int MaxConnectionsPerDownload { get; set; } = 8;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int MaxRetries { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;

    public bool SpeedLimiterEnabled { get; set; }
    public int SpeedLimitKBps { get; set; } = 500;

    public string DefaultDownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public string TempDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnIDM", "Temp");

    public bool MonitorClipboard { get; set; } = true;
    public bool ShowProgressWindow { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;
    public bool BrowserIntegration { get; set; } = true;

    /// <summary>Extensions that trigger the clipboard monitor.</summary>
    public string MonitoredExtensions { get; set; } =
        "zip rar 7z gz tar iso exe msi apk dmg mp3 m4a flac wav aac mp4 mkv avi mov wmv webm flv pdf doc docx xls xlsx ppt pptx epub torrent bin img";

    public List<Category> Categories { get; set; } = DefaultCategories();

    public QueueSchedule Schedule { get; set; } = new();

    /// <summary>Last options used in the site grabber.</summary>
    public GrabberOptions Grabber { get; set; } = new();

    /// <summary>Remux finished HLS downloads (.ts) to .mp4 when ffmpeg is available.</summary>
    public bool ConvertHlsToMp4 { get; set; } = true;

    /// <summary>ffmpeg.exe location; empty = search PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>yt-dlp.exe location (YouTube and other video sites); empty = search PATH.</summary>
    public string YtDlpPath { get; set; } = "";

    /// <summary>Where OwnIDM installs yt-dlp, ffmpeg and Deno by itself (per user, no admin rights).</summary>
    public string ToolsDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnIDM", "tools");

    /// <summary>Saved .torrent metadata, fast-resume data and the DHT cache.</summary>
    public string TorrentDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnIDM", "Torrent");

    /// <summary>TCP/UDP port peers connect to; chosen at random once so that each install differs.</summary>
    public int TorrentPort { get; set; } = Random.Shared.Next(20000, 60000);

    /// <summary>Find peers without a tracker: DHT, peer exchange and local peer discovery.</summary>
    public bool TorrentDht { get; set; } = true;

    /// <summary>Open <see cref="TorrentPort"/> on the router with UPnP / NAT-PMP.</summary>
    public bool TorrentPortForwarding { get; set; } = true;

    /// <summary>Upload limit per torrent in KB/s, 0 = unlimited.</summary>
    public int TorrentUploadLimitKBps { get; set; }

    public int TorrentMaxConnections { get; set; } = 60;

    /// <summary>Look for a new OwnIDM version, and a new yt-dlp if OwnIDM installed it, once a day at startup.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime LastUpdateCheck { get; set; }

    /// <summary>Version the user chose to skip: not offered again at startup (a manual check still shows it).</summary>
    public string SkippedVersion { get; set; } = "";

    /// <summary>Bytes per second applied to each download, 0 = unlimited.</summary>
    public long EffectiveSpeedLimit => SpeedLimiterEnabled && SpeedLimitKBps > 0 ? SpeedLimitKBps * 1024L : 0;

    public string CategoryFor(string fileName) =>
        Categories.FirstOrDefault(c => c.Name != GeneralCategory && c.Matches(fileName))?.Name ?? GeneralCategory;

    public string DirectoryFor(string category)
    {
        var cat = Categories.FirstOrDefault(c => c.Name == category);
        if (cat is not null && !string.IsNullOrWhiteSpace(cat.Directory))
            return cat.Directory;
        return category == GeneralCategory
            ? DefaultDownloadDirectory
            : Path.Combine(DefaultDownloadDirectory, category);
    }

    public bool IsMonitoredUrl(string url)
    {
        if (Torrents.TorrentLink.IsMagnet(url))
            return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return false;
        var ext = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
        return ext.Length > 0 &&
               MonitoredExtensions.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static List<Category> DefaultCategories() =>
    [
        new() { Name = GeneralCategory },
        new() { Name = "Comprimidos", Extensions = "zip rar 7z gz bz2 xz tar tgz iso cab arj lzh ace z" },
        new() { Name = "Documentos", Extensions = "pdf doc docx xls xlsx ppt pptx odt ods txt rtf epub csv" },
        new() { Name = "Música", Extensions = "mp3 m4a flac wav aac ogg wma opus aif mid" },
        new() { Name = "Programas", Extensions = "exe msi msix apk dmg deb rpm appimage bat" },
        new() { Name = "Vídeo", Extensions = "mp4 mkv avi mov wmv webm flv m4v mpg mpeg 3gp ts" },
    ];
}
