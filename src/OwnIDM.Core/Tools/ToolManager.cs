using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Tools;

/// <summary>External programs OwnIDM can download and keep up to date by itself.</summary>
public enum Tool
{
    /// <summary>YouTube and other video sites.</summary>
    YtDlp,
    /// <summary>Joins separate video and audio, remuxes MPEG-TS to MP4.</summary>
    Ffmpeg,
    /// <summary>JavaScript runtime yt-dlp needs to solve YouTube's challenges.</summary>
    Deno,
}

public sealed record ToolProgress(Tool Tool, string Message, long Downloaded = 0, long Total = 0);

/// <summary>A release asset ready to install: its version, address, size and expected SHA-256.</summary>
public sealed record ToolRelease(Tool Tool, string Version, string AssetName, string Url, long Size, string Sha256);

/// <summary>Where the managed copies live; also used by the tool lookups (<c>Ffmpeg.Find</c>…).</summary>
public static class ToolPaths
{
    public static string For(AppSettings settings, Tool tool) => tool switch
    {
        Tool.YtDlp => Path.Combine(settings.ToolsDirectory, "yt-dlp.exe"),
        Tool.Ffmpeg => Path.Combine(settings.ToolsDirectory, "ffmpeg", "ffmpeg.exe"),
        _ => Path.Combine(settings.ToolsDirectory, "deno.exe"),
    };

    public static string? Installed(AppSettings settings, Tool tool)
    {
        var path = For(settings, tool);
        return File.Exists(path) ? path : null;
    }
}

/// <summary>
/// Downloads yt-dlp, ffmpeg and Deno from their official GitHub releases into the per-user tools
/// folder (no administrator rights), checks each file against the SHA-256 published with it,
/// and keeps yt-dlp current: YouTube changes often and old versions stop working.
/// </summary>
public sealed class ToolManager : IDisposable
{
    private sealed record Spec(Tool Tool, string Repo, Func<IReadOnlyList<string>, string?> PickAsset, Func<string, string> ChecksumAsset);

    private static readonly Regex FfmpegRelease = new(@"^ffmpeg-n(?<v>\d+(\.\d+)*)-latest-win64-lgpl-shared-[\d.]+\.zip$");
    private const string FfmpegMaster = "ffmpeg-master-latest-win64-lgpl-shared.zip";

    private static readonly Spec[] Specs =
    [
        new(Tool.YtDlp, "yt-dlp/yt-dlp", names => names.FirstOrDefault(n => n == "yt-dlp.exe"), _ => "SHA2-256SUMS"),
        // LGPL "shared" builds: half the size of the static ones and all OwnIDM needs to copy streams.
        new(Tool.Ffmpeg, "BtbN/FFmpeg-Builds", names =>
                names.Select(n => (Name: n, Match: FfmpegRelease.Match(n))).Where(x => x.Match.Success)
                     .OrderByDescending(x => Version.Parse(x.Match.Groups["v"].Value + (x.Match.Groups["v"].Value.Contains('.') ? "" : ".0")))
                     .Select(x => x.Name).FirstOrDefault() ?? names.FirstOrDefault(n => n == FfmpegMaster),
            _ => "checksums.sha256"),
        new(Tool.Deno, "denoland/deno", names => names.FirstOrDefault(n => n == "deno-x86_64-pc-windows-msvc.zip"), n => n + ".sha256sum"),
    ];

    private readonly AppSettings _settings;
    private readonly DownloadEngine _engine;
    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="apiBase">GitHub API root; tests point it at a local server.</param>
    public ToolManager(AppSettings settings, DownloadEngine engine, HttpMessageHandler? handler = null, string apiBase = "https://api.github.com")
    {
        _settings = settings;
        _engine = engine;
        _apiBase = apiBase.TrimEnd('/');
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(15, settings.TimeoutSeconds)),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OwnIDM/1.0"); // the GitHub API requires one
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private string ManifestFile => Path.Combine(_settings.ToolsDirectory, "tools.json");

    /// <summary>Version of the managed copy, or null when OwnIDM has not installed it.</summary>
    public string? InstalledVersion(Tool tool) =>
        ToolPaths.Installed(_settings, tool) is null ? null : ReadManifest().GetValueOrDefault(tool)?.Version;

    /// <summary>Latest release of each tool (one GitHub API call each); sizes let the UI ask first.</summary>
    public async Task<List<ToolRelease>> ResolveAsync(IEnumerable<Tool> tools, CancellationToken ct)
    {
        var result = new List<ToolRelease>();
        foreach (var tool in tools.Distinct())
            result.Add(await ResolveAsync(tool, ct).ConfigureAwait(false));
        return result;
    }

    public async Task<ToolRelease> ResolveAsync(Tool tool, CancellationToken ct)
    {
        var spec = Specs.First(s => s.Tool == tool);
        using var doc = JsonDocument.Parse(await GetStringAsync($"{_apiBase}/repos/{spec.Repo}/releases/latest", ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var assets = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? "",
                          Size: a.TryGetProperty("size", out var s) ? s.GetInt64() : 0))
            .ToList();
        var name = spec.PickAsset(assets.Select(a => a.Name).ToList())
                   ?? throw new IOException($"La última versión de {Display(tool)} no incluye la descarga para Windows.");
        var asset = assets.First(a => a.Name == name);
        var sumsName = spec.ChecksumAsset(name);
        var sums = assets.FirstOrDefault(a => a.Name == sumsName);
        if (sums.Url is null)
            throw new IOException($"La versión de {Display(tool)} no publica sumas de comprobación; no se instala sin poder verificarla.");
        var sha = Checksums.Find(await GetStringAsync(sums.Url, ct).ConfigureAwait(false), name)
                  ?? throw new IOException($"No se encontró la suma SHA-256 de {name}.");

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        // BtbN publishes every build under the tag "latest": the version is in the file name.
        var version = tool == Tool.Ffmpeg && FfmpegRelease.Match(name) is { Success: true } m ? "n" + m.Groups["v"].Value
                    : tag.Length > 0 && tag != "latest" ? tag : name;
        return new ToolRelease(tool, version, name, asset.Url, asset.Size, sha);
    }

    /// <summary>Downloads, verifies and installs the given releases one after the other.</summary>
    public async Task InstallAsync(IEnumerable<ToolRelease> releases, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var release in releases)
                await InstallLockedAsync(release, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Installs a newer yt-dlp if the managed one has not been checked for <paramref name="minInterval"/>.
    /// True when it was replaced. A yt-dlp not installed by OwnIDM is left alone.
    /// </summary>
    public async Task<bool> UpdateYtDlpAsync(TimeSpan minInterval, CancellationToken ct)
    {
        if (ToolPaths.Installed(_settings, Tool.YtDlp) is null)
            return false;
        var info = ReadManifest().GetValueOrDefault(Tool.YtDlp);
        if (info is not null && DateTime.UtcNow - info.LastCheck < minInterval)
            return false;

        var latest = await ResolveAsync(Tool.YtDlp, ct).ConfigureAwait(false);
        if (info?.Version == latest.Version)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var manifest = ReadManifest();
                if (manifest.TryGetValue(Tool.YtDlp, out var current))
                    manifest[Tool.YtDlp] = current with { LastCheck = DateTime.UtcNow };
                WriteManifest(manifest);
            }
            finally
            {
                _lock.Release();
            }
            return false;
        }
        await InstallAsync([latest], null, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Cleanup of replaced files and, when <paramref name="checkUpdates"/>, the daily yt-dlp check; never throws.</summary>
    public async Task MaintainAsync(bool checkUpdates = true, CancellationToken ct = default)
    {
        if (Directory.Exists(_settings.ToolsDirectory))
            foreach (var old in Directory.GetFiles(_settings.ToolsDirectory, "*.old"))
                TryDelete(old);
        TryDeleteDirectory(Path.Combine(_settings.ToolsDirectory, "ffmpeg.old"));
        if (!checkUpdates)
            return;
        try
        {
            await UpdateYtDlpAsync(TimeSpan.FromDays(1), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            // Offline or GitHub unavailable: try again next time.
        }
    }

    private async Task InstallLockedAsync(ToolRelease release, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var name = Display(release.Tool);
        var staging = Path.Combine(_settings.ToolsDirectory, ".download");
        TryDeleteDirectory(staging);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new ToolProgress(release.Tool, $"Descargando {name} {release.Version}…", 0, release.Size));
            var file = await DownloadAsync(release, staging, progress, ct).ConfigureAwait(false);

            progress?.Report(new ToolProgress(release.Tool, $"Comprobando {name}…", release.Size, release.Size));
            var actual = await HashAsync(file, ct).ConfigureAwait(false);
            if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"La descarga de {name} está dañada o no es la oficial (SHA-256 distinto); no se ha instalado.");

            progress?.Report(new ToolProgress(release.Tool, $"Instalando {name}…", release.Size, release.Size));
            switch (release.Tool)
            {
                case Tool.YtDlp:
                    ReplaceFile(file, ToolPaths.For(_settings, Tool.YtDlp));
                    break;
                case Tool.Deno:
                    var exe = ExtractEntry(file, staging, e => e.FullName.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                    ReplaceFile(exe, ToolPaths.For(_settings, Tool.Deno));
                    break;
                case Tool.Ffmpeg:
                    InstallFfmpeg(file, staging);
                    break;
            }

            var manifest = ReadManifest();
            manifest[release.Tool] = new InstalledTool(release.Version, DateTime.UtcNow);
            WriteManifest(manifest);
            progress?.Report(new ToolProgress(release.Tool, $"{name} {release.Version} instalado.", release.Size, release.Size));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>Uses OwnIDM's own engine: several connections, retries, the speed limiter.</summary>
    private async Task<string> DownloadAsync(ToolRelease release, string staging, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var item = new DownloadItem { Url = release.Url, FileName = release.AssetName, SaveDirectory = staging };
        var run = _engine.RunAsync(item, ct);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            item.RefreshProgress();
            progress?.Report(new ToolProgress(release.Tool, $"Descargando {Display(release.Tool)} {release.Version}…",
                item.Downloaded, item.TotalSize > 0 ? item.TotalSize : release.Size));
        }
        await run.ConfigureAwait(false);
        TryDelete(_engine.TempPathFor(item));
        ct.ThrowIfCancellationRequested();
        if (item.Status != DownloadStatus.Completed)
            throw new IOException($"No se pudo descargar {Display(release.Tool)}: {item.ErrorMessage}");
        return item.FullPath;
    }

    private void InstallFfmpeg(string zip, string staging)
    {
        // The zip holds <build>/bin/{ffmpeg,ffprobe,ffplay}.exe + DLLs and the LICENSE.txt OwnIDM keeps with them.
        var target = Path.GetDirectoryName(ToolPaths.For(_settings, Tool.Ffmpeg))!;
        var fresh = Path.Combine(staging, "ffmpeg");
        Directory.CreateDirectory(fresh);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                var parts = entry.FullName.Split('/');
                bool inBin = parts.Length == 3 && parts[1] == "bin" &&
                             (entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                              entry.Name is "ffmpeg.exe" or "ffprobe.exe");
                bool license = parts.Length == 2 && entry.Name.Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase);
                if (inBin || license)
                    entry.ExtractToFile(Path.Combine(fresh, entry.Name), overwrite: true);
            }
        }
        if (!File.Exists(Path.Combine(fresh, "ffmpeg.exe")))
            throw new IOException("El paquete de ffmpeg no contiene ffmpeg.exe.");

        var old = target + ".old";
        TryDeleteDirectory(old);
        if (Directory.Exists(target))
        {
            try
            {
                Directory.Move(target, old);
            }
            catch (IOException)
            {
                throw new IOException("ffmpeg está en uso por una descarga; inténtalo cuando termine.");
            }
        }
        Directory.Move(fresh, target);
        TryDeleteDirectory(old);
    }

    private static string ExtractEntry(string zip, string staging, Func<ZipArchiveEntry, bool> match)
    {
        using var archive = ZipFile.OpenRead(zip);
        var entry = archive.Entries.FirstOrDefault(match) ?? throw new IOException("El paquete descargado no contiene el programa esperado.");
        var path = Path.Combine(staging, "extracted-" + entry.Name);
        entry.ExtractToFile(path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Puts <paramref name="source"/> in place of <paramref name="target"/>. A running .exe cannot be
    /// overwritten but can be renamed, so the old copy steps aside as .old and is deleted later.
    /// </summary>
    private static void ReplaceFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var old = target + ".old";
        TryDelete(old);
        if (File.Exists(target))
            File.Move(target, File.Exists(old) ? target + "." + Guid.NewGuid().ToString("N")[..8] + ".old" : old);
        File.Move(source, target);
        TryDelete(old);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Forbidden && resp.Headers.Contains("X-RateLimit-Remaining"))
            throw new IOException("GitHub limita las consultas por hora; inténtalo de nuevo más tarde.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{url}: el servidor respondió {(int)resp.StatusCode}", null, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static string Display(Tool tool) => tool switch
    {
        Tool.YtDlp => "yt-dlp",
        Tool.Ffmpeg => "ffmpeg",
        _ => "Deno",
    };

    private sealed record InstalledTool(string Version, DateTime LastCheck);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private Dictionary<Tool, InstalledTool> ReadManifest()
    {
        try
        {
            return File.Exists(ManifestFile)
                ? JsonSerializer.Deserialize<Dictionary<Tool, InstalledTool>>(File.ReadAllText(ManifestFile), Json) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void WriteManifest(Dictionary<Tool, InstalledTool> manifest)
    {
        Directory.CreateDirectory(_settings.ToolsDirectory);
        File.WriteAllText(ManifestFile, JsonSerializer.Serialize(manifest, Json));
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}

/// <summary>Reads the checksum files projects publish next to their downloads.</summary>
public static partial class Checksums
{
    [GeneratedRegex("[0-9A-Fa-f]{64}")]
    private static partial Regex Sha256();

    /// <summary>
    /// The SHA-256 of <paramref name="assetName"/>: from the line naming it ("hash  file" lists) or,
    /// in a file about a single download (PowerShell's Get-FileHash output), its only hash.
    /// </summary>
    public static string? Find(string text, string assetName)
    {
        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var hash = Sha256().Match(line);
            if (!hash.Success)
                continue;
            var rest = line.Replace(hash.Value, "").Trim().TrimStart('*').Trim();
            if ((rest == assetName || rest.EndsWith("/" + assetName, StringComparison.Ordinal)))
                return hash.Value;
        }
        var all = Sha256().Matches(text);
        return all.Count == 1 && text.Contains(assetName, StringComparison.Ordinal) ? all[0].Value : null;
    }
}
