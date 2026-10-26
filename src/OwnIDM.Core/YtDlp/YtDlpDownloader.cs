using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Tools;
using OwnIDM.Core.Util;

namespace OwnIDM.Core.YtDlp;

/// <summary>
/// One choice of the quality list: a height ("1080"), "audio", or "best". <paramref name="Formats"/> are
/// the yt-dlp format ids picked for it ("137+140") and <paramref name="Size"/> their total, exact when
/// <paramref name="SizeExact"/> (an estimate from the bitrate otherwise).
/// </summary>
public sealed record YtDlpQuality(string Key, string Label, string? Formats = null, long? Size = null, bool SizeExact = false);

public sealed record YtDlpProbe(string Title, string Site, double Duration, bool IsLive, List<YtDlpQuality> Qualities);

/// <summary>
/// yt-dlp progress for one of the files it downloads (video, audio): <paramref name="Total"/> is its
/// exact size, <paramref name="Estimate"/> the guess yt-dlp revises as fragments arrive.
/// <paramref name="Fragments"/> is how many pieces a fragmented file comes in (null for a plain file).
/// </summary>
public readonly record struct YtDlpProgress(string FormatId, bool Finished, long Downloaded, long? Total, long? Estimate = null,
    int? Fragments = null);

/// <summary>A file yt-dlp is about to download and its size, exact or estimated.</summary>
public readonly record struct YtDlpPart(string FormatId, long? Size, bool Exact);

/// <summary>Locating yt-dlp and talking its command line.</summary>
public static class YtDlpTool
{
    public const string BestKey = "best";
    public const string AudioKey = "audio";

    /// <summary>The path set in Options, else the copy OwnIDM installed (kept up to date), else one on PATH.</summary>
    public static string? Find(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.YtDlpPath) && File.Exists(settings.YtDlpPath)
            ? settings.YtDlpPath
            : ToolPaths.Installed(settings, Tool.YtDlp) ?? FindOnPath("yt-dlp.exe") ??
              // winget installs portable tools here; the folder joins PATH only for processes started later.
              Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "yt-dlp.exe"));

    public static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), exe))
            .FirstOrDefault(File.Exists);

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    /// <summary>
    /// YouTube needs a JavaScript runtime. yt-dlp only enables Deno by default (looked up on PATH),
    /// so OwnIDM's own Deno is passed by path, and Node.js or Bun are enabled when there is no Deno.
    /// </summary>
    public static IEnumerable<string> JsRuntimeArgs(AppSettings settings)
    {
        if (ToolPaths.Installed(settings, Tool.Deno) is { } deno)
            return ["--js-runtimes", "deno:" + deno];
        if (FindOnPath("deno.exe") is not null)
            return [];
        if (FindOnPath("node.exe") is not null)
            return ["--js-runtimes", "node"];
        if (FindOnPath("bun.exe") is not null)
            return ["--js-runtimes", "bun"];
        return [];
    }

    /// <summary>The JavaScript runtime yt-dlp will use, or null when there is none.</summary>
    public static string? FindJsRuntime(AppSettings settings) =>
        ToolPaths.Installed(settings, Tool.Deno) ?? FindOnPath("deno.exe") ?? FindOnPath("node.exe") ?? FindOnPath("bun.exe");

    /// <summary>
    /// -f / -S arguments for a quality key. Within a resolution H.264 and AAC are preferred, as they
    /// play everywhere; without ffmpeg only files that already carry the audio can be used.
    /// </summary>
    /// <param name="formats">The format ids picked when probing: tried first, so the file is the one whose
    /// size was shown and a paused download resumes the same files.</param>
    public static string[] FormatArgs(string? key, bool hasFfmpeg, string? formats = null)
    {
        string[] args;
        if (key == AudioKey)
        {
            args = ["-f", "ba[ext=m4a]/ba/b"];
        }
        else
        {
            var res = int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ? $"res:{height}" : "res";
            args = hasFfmpeg
                ? ["-f", "bv*+ba/b", "-S", $"{res},vcodec:h264,acodec:aac", "--merge-output-format", "mp4"]
                : ["-f", "b", "-S", $"{res},vcodec:h264,acodec:aac"];
        }
        // Without ffmpeg a video+audio pair cannot be merged.
        if (!string.IsNullOrEmpty(formats) && (hasFfmpeg || !formats.Contains('+')))
            args[1] = formats + "/" + args[1];
        return args;
    }

    /// <summary>Reads the JSON of <c>yt-dlp -J</c>.</summary>
    public static YtDlpProbe ParseInfo(string json, bool hasFfmpeg)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Str(root, "_type") is "playlist")
            throw new NotSupportedException("La dirección es una lista de reproducción; abre un vídeo concreto.");
        var title = Str(root, "title") ?? "video";
        var site = Str(root, "extractor_key") ?? Str(root, "extractor") ?? "";
        double duration = Num(root, "duration") ?? 0;
        bool live = root.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True ||
                    Str(root, "live_status") is "is_live" or "is_upcoming";

        var formats = new List<Fmt>();
        if (root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                if (f.TryGetProperty("has_drm", out var drm) && drm.ValueKind == JsonValueKind.True)
                    continue;
                // "none" means absent; a missing codec (storyboards, unknown formats) says nothing.
                var v = Str(f, "vcodec");
                var a = Str(f, "acodec");
                var protocol = Str(f, "protocol");
                long? exact = (long?)Num(f, "filesize");
                bool video = v is not null && v != "none";
                bool audio = a is not null && a != "none";
                if ((!video && !audio) || Str(f, "format_id") is not { Length: > 0 } id)
                    continue;
                formats.Add(new Fmt(id, (int)(Num(f, "height") ?? 0), video, audio,
                    exact, (long?)Num(f, "filesize_approx"),
                    H264: v?.StartsWith("avc", StringComparison.Ordinal) == true || v?.StartsWith("h264", StringComparison.Ordinal) == true,
                    Aac: a?.StartsWith("mp4a", StringComparison.Ordinal) == true || a?.StartsWith("aac", StringComparison.Ordinal) == true,
                    // Plain files have an exact size and resume by byte range; HLS/DASH only estimate it.
                    // A plain file split in byte ranges (YouTube's "dashy") is DASH with an exact size.
                    Https: protocol is null or "https" or "http" || (protocol == "http_dash_segments" && exact > 0),
                    Preference: Num(f, "preference") ?? 0, Language: Num(f, "language_preference") ?? 0,
                    Quality: Num(f, "quality") ?? 0, Fps: Num(f, "fps") ?? 0, Tbr: Num(f, "tbr") ?? 0));
            }
        }

        // The original language first, then AAC (plays everywhere), then a known size.
        var audioTrack = formats.Where(f => f.Audio && !f.Video)
            .OrderByDescending(f => f.Preference).ThenByDescending(f => f.Language).ThenByDescending(f => f.Aac)
            .ThenByDescending(f => f.Https).ThenByDescending(f => f.Exact is not null)
            .ThenByDescending(f => f.Quality).ThenByDescending(f => f.Tbr)
            .FirstOrDefault();
        bool merge = hasFfmpeg && audioTrack is not null;
        var qualities = formats
            .Where(f => f.Video && f.Height > 0 && (merge || f.Audio))
            .GroupBy(f => f.Height)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                // Mirrors the -S of FormatArgs: H.264, then a plain file, then the best of what is left.
                var video = g.OrderByDescending(f => f.Preference).ThenByDescending(f => f.H264).ThenByDescending(f => f.Https)
                    .ThenByDescending(f => merge && !f.Audio).ThenByDescending(f => f.Exact is not null)
                    .ThenByDescending(f => f.Fps).ThenByDescending(f => f.Tbr)
                    .First();
                Fmt[] picked = video.Audio ? [video] : [video, audioTrack!];
                return Quality(g.Key.ToString(CultureInfo.InvariantCulture), $"{g.Key}p", picked);
            })
            .ToList();
        if (qualities.Count == 0)
            qualities.Add(new YtDlpQuality(BestKey, "Mejor calidad disponible"));
        if (audioTrack is not null)
            qualities.Add(Quality(AudioKey, "Solo audio", [audioTrack]));
        return new YtDlpProbe(title, site, duration, live, qualities);
    }

    private sealed record Fmt(string Id, int Height, bool Video, bool Audio, long? Exact, long? Approx, bool H264, bool Aac,
        bool Https, double Preference, double Language, double Quality, double Fps, double Tbr);

    private static YtDlpQuality Quality(string key, string label, Fmt[] picked)
    {
        bool exact = picked.All(f => f.Exact > 0);
        long? size = picked.All(f => (f.Exact ?? f.Approx) > 0) ? picked.Sum(f => f.Exact ?? f.Approx!.Value) : null;
        if (size > 0)
            label += " · " + (exact ? "" : "≈ ") + Format.Bytes(size.Value);
        return new YtDlpQuality(key, label, string.Join("+", picked.Select(f => f.Id)), size, exact && size > 0);
    }

    public const string ProgressPrefix = "OWNIDM-PROGRESS ";
    public const string SizePrefix = "OWNIDM-SIZE ";
    public const string FilePrefix = "OWNIDM-FILE ";

    /// <summary>The output lines OwnIDM asks for: sizes, progress and the final file.</summary>
    public static string[] ReportArgs() =>
    [
        "--newline", "--progress", "--no-simulate",
        "--progress-template", "download:" + ProgressPrefix +
            "%(progress.status)s %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s %(info.format_id)s " +
            "%(progress.fragment_count)s",
        // The chosen format, or each file of a video+audio pair (yt-dlp sums a pair's sizes as an estimate).
        "--print", "before_dl:" + SizePrefix +
            "%(format_id)s %(filesize)s %(filesize_approx)s " +
            "%(requested_formats.0.format_id)s %(requested_formats.0.filesize)s %(requested_formats.0.filesize_approx)s " +
            "%(requested_formats.1.format_id)s %(requested_formats.1.filesize)s %(requested_formats.1.filesize_approx)s",
        "--print", "after_move:" + FilePrefix + "%(filepath)s",
    ];

    public static bool TryParseProgress(string line, out YtDlpProgress progress)
    {
        progress = default;
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return false;
        var p = line[ProgressPrefix.Length..].Trim().Split(' ');
        if (p.Length < 5)
            return false;
        bool finished = p[0] == "finished";
        long? total = Long(p[2]);
        long? estimate = Long(p[3]);
        // A file already complete from an earlier run is reported as finished with no byte count.
        long? done = long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : finished ? total ?? estimate : null;
        if (done is null)
            return false;
        // Once finished, what arrived is the size.
        int? fragments = p.Length > 5 && Long(p[5]) is { } n ? (int)Math.Min(n, int.MaxValue) : null;
        progress = new YtDlpProgress(p[4], finished, done.Value, finished ? done : total, estimate, fragments);
        return true;
    }

    /// <summary>Reads the before_dl line of <see cref="ReportArgs"/>: the files about to be downloaded.</summary>
    public static bool TryParseSizes(string line, out List<YtDlpPart> parts)
    {
        parts = [];
        if (!line.StartsWith(SizePrefix, StringComparison.Ordinal))
            return false;
        var p = line[SizePrefix.Length..].Trim().Split(' ');
        if (p.Length < 9)
            return false;
        static YtDlpPart Part(string id, string exact, string approx) =>
            Long(exact) is { } size ? new(id, size, true) : new(id, Long(approx), false);
        if (p[3] != "NA")
        {
            parts.Add(Part(p[3], p[4], p[5]));
            if (p[6] != "NA")
                parts.Add(Part(p[6], p[7], p[8]));
        }
        else if (p[0] != "NA")
        {
            parts.Add(Part(p[0], p[1], p[2]));
        }
        return parts.Count > 0;
    }

    /// <summary>
    /// Total size of a download from the files announced before it started and the progress of each:
    /// a file's exact size wins, an estimate is kept steady (yt-dlp revises its own as fragments
    /// arrive) and only grows when more than that has arrived. Null while some file's size is unknown.
    /// </summary>
    public static long? TotalSize(IReadOnlyDictionary<string, YtDlpPart> planned, IReadOnlyDictionary<string, YtDlpProgress> progress)
    {
        long total = 0;
        foreach (var id in planned.Keys.Union(progress.Keys))
        {
            planned.TryGetValue(id, out var plan);
            bool started = progress.TryGetValue(id, out var p);
            long? size = (started ? p.Total : null) ?? plan.Size ?? (started ? p.Estimate : null);
            if (size is null)
                return null;
            total += Math.Max(size.Value, started ? p.Downloaded : 0);
        }
        return total > 0 ? total : null;
    }

    /// <summary>Cookies captured by the browser as a Netscape cookies.txt for the page's site.</summary>
    public static string CookiesFile(string cookieHeader, Uri page)
    {
        var host = page.Host;
        foreach (var prefix in new[] { "www.", "m." })
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                host = host[prefix.Length..];
        long expires = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var pair in cookieHeader.Split(';'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            if (name.Length == 0 || (name + value).IndexOfAny(['\t', '\r', '\n']) >= 0)
                continue;
            sb.Append(CultureInfo.InvariantCulture, $".{host}\tTRUE\t/\tFALSE\t{expires}\t{name}\t{value}\n");
        }
        return sb.ToString();
    }

    /// <summary>The message of yt-dlp's last "ERROR:" line, without its "[site] id:" prefix.</summary>
    public static string? ErrorFrom(IEnumerable<string> stderr)
    {
        var line = stderr.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line is null)
            return null;
        var message = line["ERROR:".Length..].Trim();
        if (message.StartsWith('[') && message.IndexOf("]", StringComparison.Ordinal) is int close and > 0)
        {
            message = message[(close + 1)..].TrimStart();
            int colon = message.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && !message[..colon].Contains(' '))
                message = message[(colon + 2)..];
        }
        return message;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>A positive byte count; estimates come with decimals ("4080.0").</summary>
    private static long? Long(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? (long)v : null;
}

/// <summary>
/// Downloads videos of sites that publish no plain stream (YouTube and the others yt-dlp knows)
/// by running yt-dlp. Pausing stops it; it resumes its own .part files on the next run.
/// </summary>
public sealed class YtDlpDownloader
{
    private readonly AppSettings _settings;

    public YtDlpDownloader(AppSettings settings) => _settings = settings;

    public string TempDirectoryFor(DownloadItem item) =>
        Path.Combine(_settings.TempDirectory, item.Id.ToString("N") + ".ytdlp");

    /// <summary>
    /// The page as read when probing (yt-dlp -J). The download loads it instead of reading the page
    /// again, which on YouTube takes as long as the probe itself.
    /// </summary>
    public string InfoPathFor(DownloadItem item) => Path.Combine(TempDirectoryFor(item), "info.json");

    // The format addresses in it expire: YouTube's after about six hours.
    private static readonly TimeSpan InfoLifetime = TimeSpan.FromHours(4);

    /// <param name="saveInfoTo">Where to keep the page as read, see <see cref="InfoPathFor"/>.</param>
    public async Task<YtDlpProbe> ProbeAsync(string url, string? cookies, string? userAgent, CancellationToken ct,
        string? saveInfoTo = null)
    {
        var exe = YtDlpTool.Find(_settings) ?? throw new FileNotFoundException(NotFound);
        Directory.CreateDirectory(_settings.TempDirectory);
        var cookieFile = WriteCookies(Path.Combine(_settings.TempDirectory, Guid.NewGuid().ToString("N") + ".cookies.txt"), url, cookies);
        try
        {
            var stdout = new StringBuilder();
            var stderr = new List<string>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _settings.TimeoutSeconds * 2)));
            int exit;
            try
            {
                exit = await RunAsync(exe, [.. CommonArgs(userAgent, cookieFile), "-J", url],
                    line => stdout.AppendLine(line), stderr.Add, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("yt-dlp tardó demasiado en leer la página.");
            }
            if (exit != 0 || stdout.Length == 0)
                throw new IOException(YtDlpTool.ErrorFrom(stderr) ?? $"yt-dlp terminó con el código {exit}.");
            var json = stdout.ToString();
            var probe = YtDlpTool.ParseInfo(json, Ffmpeg.Find(_settings) is not null);
            if (saveInfoTo is not null && !probe.IsLive)
                SaveInfo(saveInfoTo, json);
            return probe;
        }
        finally
        {
            if (cookieFile is not null)
                TryDelete(cookieFile);
        }
    }

    /// <summary>Never throws; the outcome is reflected in <see cref="DownloadItem.Status"/>.</summary>
    public async Task RunAsync(DownloadItem item, CancellationToken ct)
    {
        item.LastTryAt = DateTime.Now;
        item.ErrorMessage = null;
        item.Status = DownloadStatus.Connecting;
        // yt-dlp continues its .part files (plain files by byte range, HLS/DASH by fragment).
        item.ResumeSupported = true;
        var dir = TempDirectoryFor(item);
        var info = InfoPathFor(item);
        string? cookieFile = null;
        try
        {
            var exe = YtDlpTool.Find(_settings) ?? throw new FileNotFoundException(NotFound);
            var ffmpeg = Ffmpeg.Find(_settings);
            Directory.CreateDirectory(dir);
            cookieFile = WriteCookies(Path.Combine(dir, "cookies.txt"), item.Url, item.Cookies);
            // yt-dlp fetches that many fragments at once; a plain file always comes over one connection.
            int connections = Math.Clamp(_settings.MaxConnectionsPerDownload, 1, 8);

            List<string> Args(bool fromInfo)
            {
                var args = new List<string>(CommonArgs(item.UserAgent, cookieFile));
                args.AddRange(YtDlpTool.FormatArgs(item.YtDlpQuality, ffmpeg is not null, item.YtDlpFormat));
                if (ffmpeg is not null)
                    args.AddRange(["--ffmpeg-location", ffmpeg]);
                args.AddRange(["-N", connections.ToString(CultureInfo.InvariantCulture)]);
                if (_settings.EffectiveSpeedLimit > 0)
                    args.AddRange(["--limit-rate", _settings.EffectiveSpeedLimit.ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["--retries", _settings.MaxRetries.ToString(CultureInfo.InvariantCulture)]);
                args.AddRange(["-o", Path.Combine(dir, "video.%(ext)s")]);
                args.AddRange(YtDlpTool.ReportArgs());
                if (fromInfo)
                    args.AddRange(["--load-info-json", info]);
                else
                    args.Add(item.Url);
                return args;
            }

            // Video and audio arrive one after the other, each with its own progress; the sizes of
            // both are announced before the first starts.
            var planned = new Dictionary<string, YtDlpPart>();
            var parts = new Dictionary<string, YtDlpProgress>();
            string? output = null;
            var stderr = new List<string>();
            void Refresh()
            {
                // Until yt-dlp announces the files, the size shown when probing stands.
                if (YtDlpTool.TotalSize(planned, parts) is { } total && (planned.Count > 0 || total > item.TotalSize))
                    item.TotalSize = total;
                item.Downloaded = parts.Values.Sum(x => x.Downloaded);
            }
            void OnLine(string line)
            {
                if (YtDlpTool.TryParseProgress(line, out var p))
                {
                    item.Status = DownloadStatus.Downloading;
                    item.ActiveConnections = p.Fragments is { } fragments ? Math.Clamp(fragments, 1, connections) : 1;
                    parts[p.FormatId] = p;
                    Refresh();
                }
                else if (YtDlpTool.TryParseSizes(line, out var announced))
                {
                    planned.Clear();
                    foreach (var part in announced)
                        planned[part.FormatId] = part;
                    Refresh();
                }
                else if (line.StartsWith(YtDlpTool.FilePrefix, StringComparison.Ordinal))
                {
                    output = line[YtDlpTool.FilePrefix.Length..].Trim();
                }
            }

            bool fromInfo = File.Exists(info) && DateTime.UtcNow - File.GetLastWriteTimeUtc(info) < InfoLifetime;
            int exit = await RunAsync(exe, Args(fromInfo), OnLine, stderr.Add, ct).ConfigureAwait(false);
            if (exit != 0 && fromInfo)
            {
                // The saved addresses may have expired or been refused: read the page again.
                TryDelete(info);
                stderr.Clear();
                exit = await RunAsync(exe, Args(fromInfo: false), OnLine, stderr.Add, ct).ConfigureAwait(false);
            }
            else if (!fromInfo)
            {
                TryDelete(info); // stale
            }
            if (exit != 0)
                throw new IOException(YtDlpTool.ErrorFrom(stderr) ?? $"yt-dlp terminó con el código {exit}.");
            // yt-dlp skips the print when the file was already complete from an earlier run.
            output ??= Directory.GetFiles(dir, "video.*").FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") &&
                                                                                 !Path.GetFileName(f).Contains(".f"));
            if (output is null || !File.Exists(output))
                throw new IOException("yt-dlp no generó ningún archivo.");
            Finish(item, output);
            TryDeleteDirectory(dir);
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
            if (cookieFile is not null)
                TryDelete(cookieFile);
            item.ActiveConnections = 0;
            item.Speed = 0;
            item.NotifyProgressChanged();
        }
    }

    private const string NotFound = "Falta yt-dlp: instálalo desde Opciones → Vídeo → «Instalar componentes».";

    private void Finish(DownloadItem item, string output)
    {
        if (string.IsNullOrWhiteSpace(item.SaveDirectory))
            item.SaveDirectory = _settings.DirectoryFor(item.Category);
        Directory.CreateDirectory(item.SaveDirectory);
        var stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(item.FileName) ? "video" : item.FileName);
        var target = FileNameHelper.MakeUnique(Path.Combine(item.SaveDirectory, stem + Path.GetExtension(output)));
        File.Move(output, target);
        item.FileName = Path.GetFileName(target);
        item.TotalSize = new FileInfo(target).Length;
        item.Downloaded = item.TotalSize;
        item.Status = DownloadStatus.Completed;
    }

    private IEnumerable<string> CommonArgs(string? userAgent, string? cookieFile)
    {
        // The user's own yt-dlp configuration could change the output names OwnIDM relies on.
        yield return "--ignore-config";
        yield return "--no-playlist";
        yield return "--encoding";
        yield return "utf-8";
        yield return "--socket-timeout";
        yield return Math.Max(5, _settings.TimeoutSeconds).ToString(CultureInfo.InvariantCulture);
        foreach (var arg in YtDlpTool.JsRuntimeArgs(_settings))
            yield return arg;
        // YouTube's files would come over a single connection; split in 10 MiB byte ranges, -N fetches
        // several at once.
        yield return "--extractor-args";
        yield return "youtube:formats=dashy";
        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            yield return "--user-agent";
            yield return userAgent;
        }
        if (cookieFile is not null)
        {
            yield return "--cookies";
            yield return cookieFile;
        }
    }

    /// <summary>Written whole or not at all: a download may start while it is being saved.</summary>
    private static void SaveInfo(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? WriteCookies(string path, string url, string? cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies) || !Uri.TryCreate(url, UriKind.Absolute, out var page))
            return null;
        File.WriteAllText(path, YtDlpTool.CookiesFile(cookies, page));
        return path;
    }

    /// <summary>
    /// Runs yt-dlp and feeds its output lines. Cancellation kills it (and the ffmpeg it may have
    /// started) and rethrows.
    /// </summary>
    private static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string> onStdout, Action<string> onStderr,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new IOException("No se pudo iniciar yt-dlp.");
        var stdout = Pump(process.StandardOutput, onStdout);
        var stderr = Pump(process.StandardError, onStderr);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        return process.ExitCode;
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            onLine(line);
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

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
