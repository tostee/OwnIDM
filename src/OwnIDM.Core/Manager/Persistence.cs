using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Manager;

/// <summary>Serializable snapshot of a <see cref="DownloadItem"/>.</summary>
public sealed record DownloadRecord(
    Guid Id,
    string Url,
    string? Referrer,
    string? Cookies,
    string? UserAgent,
    string FileName,
    string SaveDirectory,
    string Category,
    string? Description,
    long TotalSize,
    long Downloaded,
    bool? ResumeSupported,
    DownloadStatus Status,
    string? ErrorMessage,
    DateTime AddedAt,
    DateTime LastTryAt,
    bool InQueue,
    List<SegmentRecord> Segments,
    DownloadKind Kind = DownloadKind.Http,
    int HlsSegmentsDone = 0,
    long HlsBytesDone = 0,
    int HlsSegmentCount = 0,
    long HlsAudioBytesDone = 0,
    string? SourceUrl = null,
    string? YtDlpQuality = null,
    string? YtDlpFormat = null,
    string? InfoHash = null,
    List<TorrentFileRecord>? TorrentFiles = null)
{
    public static DownloadRecord From(DownloadItem i)
    {
        int segmentsDone;
        long bytesDone, audioBytesDone;
        lock (i.SyncRoot) // the HLS downloader updates these together
        {
            segmentsDone = i.HlsSegmentsDone;
            bytesDone = i.HlsBytesDone;
            audioBytesDone = i.HlsAudioBytesDone;
        }
        return new(
            i.Id, i.Url, i.Referrer, i.Cookies, i.UserAgent, i.FileName, i.SaveDirectory, i.Category, i.Description,
            i.TotalSize, i.Downloaded, i.ResumeSupported, i.Status, i.ErrorMessage, i.AddedAt, i.LastTryAt, i.InQueue,
            i.SnapshotSegments().Select(s => new SegmentRecord(s.Start, s.End, s.Downloaded)).ToList(),
            i.Kind, segmentsDone, bytesDone, i.HlsSegmentCount, audioBytesDone, i.SourceUrl, i.YtDlpQuality, i.YtDlpFormat,
            i.InfoHash,
            i.Kind == DownloadKind.Torrent
                ? i.SnapshotTorrentFiles().Select(f => new TorrentFileRecord(f.Path, f.Length, f.Selected, f.Downloaded, f.PreExisting, f.DiskPath)).ToList()
                : null);
    }

    public DownloadItem ToItem()
    {
        var item = new DownloadItem
        {
            Id = Id,
            Kind = Kind,
            HlsSegmentsDone = HlsSegmentsDone,
            HlsBytesDone = HlsBytesDone,
            HlsSegmentCount = HlsSegmentCount,
            HlsAudioBytesDone = HlsAudioBytesDone,
            SourceUrl = SourceUrl,
            YtDlpQuality = YtDlpQuality,
            YtDlpFormat = YtDlpFormat,
            InfoHash = InfoHash,
            Url = Url,
            Referrer = Referrer,
            Cookies = Cookies,
            UserAgent = UserAgent,
            FileName = FileName,
            SaveDirectory = SaveDirectory,
            Category = Category,
            Description = Description,
            TotalSize = TotalSize,
            Downloaded = Downloaded,
            ResumeSupported = ResumeSupported,
            // A download that was running when the app closed resumes as paused.
            Status = Status is DownloadStatus.Connecting or DownloadStatus.Downloading ? DownloadStatus.Paused : Status,
            ErrorMessage = ErrorMessage,
            AddedAt = AddedAt,
            LastTryAt = LastTryAt,
            InQueue = InQueue,
        };
        item.Segments.AddRange(Segments.Select(s => new Segment { Start = s.Start, End = s.End, Downloaded = s.Downloaded }));
        if (TorrentFiles is not null)
            item.TorrentFiles.AddRange(TorrentFiles.Select(f => new TorrentFileEntry
                { Path = f.Path, Length = f.Length, Selected = f.Selected, Downloaded = f.Downloaded, PreExisting = f.PreExisting, DiskPath = f.DiskPath }));
        return item;
    }
}

public sealed record SegmentRecord(long Start, long End, long Downloaded);

public sealed record TorrentFileRecord(string Path, long Length, bool Selected, long Downloaded, bool? PreExisting = null, string? DiskPath = null);

public sealed class Persistence
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _writeLock = new();

    public Persistence(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwnIDM");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    private string DownloadsFile => Path.Combine(Directory, "downloads.json");
    private string SettingsFile => Path.Combine(Directory, "settings.json");

    public AppSettings LoadSettings() => Read<AppSettings>(SettingsFile) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) => Write(SettingsFile, settings);

    public List<DownloadItem> LoadDownloads() =>
        (Read<List<DownloadRecord>>(DownloadsFile) ?? []).Select(r => r.ToItem()).ToList();

    public void SaveDownloads(IEnumerable<DownloadItem> items) =>
        Write(DownloadsFile, items.Select(DownloadRecord.From).ToList());

    private static T? Read<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
                return default;
            // Antivirus or sync tools may hold the file for a moment.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(200);
                }
            }
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".corrupt", overwrite: true);
            return default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private void Write<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        lock (_writeLock)
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var writer = new StreamWriter(fs))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}
