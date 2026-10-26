using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Update;

/// <summary>A published OwnIDM installer: its version, absolute address, size, SHA-256 and release notes.</summary>
public sealed record AppRelease(Version Version, string Url, long Size, string Sha256, string Notes);

public sealed record UpdateProgress(string Message, long Downloaded = 0, long Total = 0);

/// <summary>
/// Finds and downloads new versions of OwnIDM. The feed is the latest.json that
/// installer\build-installer.ps1 writes next to the installer:
/// <code>{ "version": "1.1.0", "url": "OwnIDM-Setup-1.1.0.exe", "size": 2245614, "sha256": "…", "notes": "…" }</code>
/// A relative "url" is resolved against the feed address, so with GitHub Releases
/// (…/releases/latest/download/latest.json) it points at the installer of that same release.
/// </summary>
public sealed class AppUpdater : IDisposable
{
    private readonly DownloadEngine _engine;
    private readonly HttpClient _http;

    /// <param name="feedUrl">Address of latest.json; null or empty disables updates.</param>
    /// <param name="updateDirectory">Where installers are downloaded; tests use a temporary folder.</param>
    public AppUpdater(AppSettings settings, DownloadEngine engine, string? feedUrl, Version current,
                      HttpMessageHandler? handler = null, string? updateDirectory = null)
    {
        _engine = engine;
        UpdateDirectory = updateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnIDM", "Update");
        FeedUrl = string.IsNullOrWhiteSpace(feedUrl) ? null : feedUrl.Trim();
        CurrentVersion = Normalize(current);
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(15, settings.TimeoutSeconds)),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OwnIDM/{CurrentVersion}");
    }

    public string? FeedUrl { get; }
    public bool Enabled => FeedUrl is not null;
    public Version CurrentVersion { get; }

    /// <summary>Downloaded installers; cleared at the next start.</summary>
    public string UpdateDirectory { get; }

    /// <summary>The published release when it is newer than this copy, otherwise null.</summary>
    public async Task<AppRelease?> CheckAsync(CancellationToken ct)
    {
        if (FeedUrl is null)
            return null;
        // No cache: CDNs in front of the feed could otherwise hide a new release for a while.
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{FeedUrl}: el servidor respondió {(int)resp.StatusCode}", null, resp.StatusCode);
        // Resolve against the configured address, not the one redirects ended at: GitHub serves
        // latest.json from a signed CDN link where a sibling file name means nothing.
        var release = Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), new Uri(FeedUrl));
        return release.Version > CurrentVersion ? release : null;
    }

    public static AppRelease Parse(string json, Uri feedUri)
    {
        var feed = JsonSerializer.Deserialize<Feed>(json, FeedJson) ?? throw new JsonException("latest.json vacío.");
        if (!Version.TryParse(feed.Version?.TrimStart('v', 'V'), out var version))
            throw new JsonException($"Versión no válida en latest.json: {feed.Version}");
        if (string.IsNullOrWhiteSpace(feed.Url) || !Uri.TryCreate(feedUri, feed.Url, out var url) || url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback)
            throw new JsonException("latest.json no indica una dirección https para el instalador.");
        if (feed.Sha256 is not { Length: 64 } sha || !sha.All(Uri.IsHexDigit))
            throw new JsonException("latest.json no incluye la suma SHA-256 del instalador; no se instala sin poder verificarlo.");
        return new AppRelease(Normalize(version), url.AbsoluteUri, feed.Size, sha, feed.Notes ?? "");
    }

    /// <summary>Downloads the installer with OwnIDM's engine and checks its SHA-256; returns its path.</summary>
    public async Task<string> DownloadAsync(AppRelease release, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var dir = UpdateDirectory;
        TryDeleteDirectory(dir);
        Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(new Uri(release.Url).AbsolutePath);
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            fileName = $"OwnIDM-Setup-{release.Version}.exe";

        var message = $"Descargando OwnIDM {release.Version}…";
        var item = new DownloadItem { Url = release.Url, FileName = fileName, SaveDirectory = dir };
        var run = _engine.RunAsync(item, ct);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            item.RefreshProgress();
            progress?.Report(new UpdateProgress(message, item.Downloaded, item.TotalSize > 0 ? item.TotalSize : release.Size));
        }
        await run.ConfigureAwait(false);
        TryDelete(_engine.TempPathFor(item));
        ct.ThrowIfCancellationRequested();
        if (item.Status != DownloadStatus.Completed)
            throw new IOException($"No se pudo descargar la actualización: {item.ErrorMessage}");

        progress?.Report(new UpdateProgress("Comprobando la descarga…", item.Downloaded, item.Downloaded));
        string actual;
        await using (var stream = File.OpenRead(item.FullPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(item.FullPath);
            throw new IOException("La actualización descargada está dañada o no es la oficial (SHA-256 distinto); no se ha instalado.");
        }
        return item.FullPath;
    }

    /// <summary>Removes the installer of an update already applied; never throws.</summary>
    public void Cleanup() => TryDeleteDirectory(UpdateDirectory);

    /// <summary>1.2 and 1.2.0.0 are the same version: compare on major.minor.build.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private sealed class Feed
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
        public string? Notes { get; set; }
    }

    private static readonly JsonSerializerOptions FeedJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

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

    public void Dispose() => _http.Dispose();
}
