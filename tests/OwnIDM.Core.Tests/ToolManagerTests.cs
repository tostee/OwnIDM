using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Tools;
using OwnIDM.Core.YtDlp;

namespace OwnIDM.Core.Tests;

public class ChecksumsTests
{
    [Fact]
    public void Reads_the_formats_published_by_each_project()
    {
        const string a = "33121d192d706e62f684a06f80918e3f3a8ba52ac3a65213b6951c39c7d9f555";
        const string b = "492d42d469a4c2a32f9e185507390b18c4ff631203b51c39d5ddfe7ae1242e68";
        // yt-dlp's SHA2-256SUMS and BtbN's checksums.sha256: "hash  file" lines.
        var list = $"{b}  yt-dlp_linux\n{a}  yt-dlp.exe\n{b} *other.zip\n";
        Assert.Equal(a, Checksums.Find(list, "yt-dlp.exe"));
        Assert.Equal(b, Checksums.Find(list, "other.zip"));
        Assert.Null(Checksums.Find(list, "yt-dlp"));

        // Deno's .sha256sum: PowerShell Get-FileHash output, hash and file on separate lines.
        var deno = "\r\nAlgorithm : SHA256\r\nHash      : " + a.ToUpperInvariant() +
                   "\r\nPath      : C:\\a\\deno\\deno\\target\\release\\deno-x86_64-pc-windows-msvc.zip\r\n";
        Assert.Equal(a.ToUpperInvariant(), Checksums.Find(deno, "deno-x86_64-pc-windows-msvc.zip"));
        Assert.Null(Checksums.Find(deno, "otro.zip"));
    }
}

/// <summary>Installs and updates the components against a local imitation of the GitHub API.</summary>
public sealed class ToolManagerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _base = "";
    private AppSettings _settings = null!;
    private DownloadEngine _engine = null!;
    private ToolManager _tools = null!;

    private string _ytDlpVersion = "2026.10.21";
    private bool _corruptYtDlp;

    private byte[] YtDlpExe => Encoding.ASCII.GetBytes("MZ yt-dlp " + _ytDlpVersion);
    private static readonly byte[] DenoZip = Zip(("deno.exe", "MZ deno"));
    private static readonly byte[] FfmpegZip = Zip(
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/LICENSE.txt", "GNU LESSER GENERAL PUBLIC LICENSE"),
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/ffmpeg.exe", "MZ ffmpeg"),
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/ffprobe.exe", "MZ ffprobe"),
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/ffplay.exe", "MZ ffplay"),
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/bin/avcodec-62.dll", "MZ dll"),
        ("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0/doc/ffmpeg.html", "<html>"));

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();

        string Asset(string name, long size) =>
            $$"""{"name": "{{name}}", "size": {{size}}, "browser_download_url": "{{_base}}/files/{{name}}"}""";
        IResult Release(string tag, params string[] assets) =>
            Results.Text($$"""{"tag_name": "{{tag}}", "assets": [{{string.Join(",", assets)}}]}""", "application/json");

        _app.MapGet("/repos/yt-dlp/yt-dlp/releases/latest", () =>
            Release(_ytDlpVersion, Asset("yt-dlp.exe", YtDlpExe.Length), Asset("SHA2-256SUMS", 100), Asset("yt-dlp_linux", 1)));
        _app.MapGet("/repos/denoland/deno/releases/latest", () =>
            Release("v2.9.8", Asset("deno-x86_64-pc-windows-msvc.zip", DenoZip.Length), Asset("deno-x86_64-pc-windows-msvc.zip.sha256sum", 100)));
        _app.MapGet("/repos/BtbN/FFmpeg-Builds/releases/latest", () =>
            Release("latest", Asset("ffmpeg-master-latest-win64-lgpl-shared.zip", 1), Asset("ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip", 1),
                Asset("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", FfmpegZip.Length), Asset("ffmpeg-n9.0-latest-win64-gpl-9.0.zip", 1),
                Asset("checksums.sha256", 100)));

        _app.MapGet("/files/yt-dlp.exe", () => Results.File(YtDlpExe, "application/octet-stream", enableRangeProcessing: true));
        _app.MapGet("/files/SHA2-256SUMS", () =>
            Results.Text($"{Sha(_corruptYtDlp ? [1, 2, 3] : YtDlpExe)}  yt-dlp.exe\n{Sha([9])}  yt-dlp_linux\n"));
        _app.MapGet("/files/deno-x86_64-pc-windows-msvc.zip", () => Results.File(DenoZip, "application/zip", enableRangeProcessing: true));
        _app.MapGet("/files/deno-x86_64-pc-windows-msvc.zip.sha256sum", () =>
            Results.Text($"\r\nAlgorithm : SHA256\r\nHash      : {Sha(DenoZip).ToUpperInvariant()}\r\nPath      : C:\\x\\deno-x86_64-pc-windows-msvc.zip\r\n"));
        _app.MapGet("/files/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", () => Results.File(FfmpegZip, "application/zip", enableRangeProcessing: true));
        _app.MapGet("/files/checksums.sha256", () =>
            Results.Text($"{Sha([7])}  ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip\n{Sha(FfmpegZip)}  ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip\n"));

        await _app.StartAsync();
        _base = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        _settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            ToolsDirectory = Path.Combine(_root, "tools"),
            MaxRetries = 0,
        };
        _engine = new DownloadEngine(_settings);
        _tools = new ToolManager(_settings, _engine, apiBase: _base);
    }

    public async Task DisposeAsync()
    {
        _tools.Dispose();
        _engine.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Resolves_the_windows_assets_and_newest_ffmpeg_release()
    {
        var releases = await _tools.ResolveAsync([Tool.YtDlp, Tool.Ffmpeg, Tool.Deno], CancellationToken.None);

        Assert.Equal(["2026.10.21", "n9.0", "v2.9.8"], releases.Select(r => r.Version));
        Assert.Equal("ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", releases[1].AssetName);
        Assert.Equal(Sha(FfmpegZip), releases[1].Sha256);
        Assert.Equal(DenoZip.Length, releases[2].Size);
    }

    [Fact]
    public async Task Installs_verified_components_and_lookups_prefer_them()
    {
        var steps = new List<string>();
        var releases = await _tools.ResolveAsync([Tool.YtDlp, Tool.Ffmpeg, Tool.Deno], CancellationToken.None);
        await _tools.InstallAsync(releases, new SyncProgress(p => steps.Add(p.Message)), CancellationToken.None);

        Assert.Equal(YtDlpExe, await File.ReadAllBytesAsync(ToolPaths.For(_settings, Tool.YtDlp)));
        Assert.Equal("MZ deno", await File.ReadAllTextAsync(ToolPaths.For(_settings, Tool.Deno)));
        var ffmpegDir = Path.Combine(_settings.ToolsDirectory, "ffmpeg");
        Assert.Equal(["LICENSE.txt", "avcodec-62.dll", "ffmpeg.exe", "ffprobe.exe"],
            Directory.GetFiles(ffmpegDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("2026.10.21", _tools.InstalledVersion(Tool.YtDlp));
        Assert.Equal("n9.0", _tools.InstalledVersion(Tool.Ffmpeg));
        Assert.Contains(steps, s => s.StartsWith("Comprobando yt-dlp", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_settings.ToolsDirectory, ".download")));

        Assert.Equal(ToolPaths.For(_settings, Tool.Ffmpeg), Ffmpeg.Find(_settings));
        Assert.Equal(ToolPaths.For(_settings, Tool.YtDlp), YtDlpTool.Find(_settings));
        Assert.Equal(["--js-runtimes", "deno:" + ToolPaths.For(_settings, Tool.Deno)], YtDlpTool.JsRuntimeArgs(_settings));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_not_installed()
    {
        _corruptYtDlp = true;
        var release = await _tools.ResolveAsync(Tool.YtDlp, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IOException>(() => _tools.InstallAsync([release], null, CancellationToken.None));

        Assert.Contains("SHA-256", ex.Message);
        Assert.Null(ToolPaths.Installed(_settings, Tool.YtDlp));
        Assert.Null(_tools.InstalledVersion(Tool.YtDlp));
    }

    [Fact]
    public async Task YtDlp_is_replaced_when_a_new_version_is_published()
    {
        await _tools.InstallAsync([await _tools.ResolveAsync(Tool.YtDlp, CancellationToken.None)], null, CancellationToken.None);
        Assert.False(await _tools.UpdateYtDlpAsync(TimeSpan.Zero, CancellationToken.None)); // already the latest

        _ytDlpVersion = "2026.11.30";
        Assert.False(await _tools.UpdateYtDlpAsync(TimeSpan.FromDays(1), CancellationToken.None)); // checked moments ago
        Assert.True(await _tools.UpdateYtDlpAsync(TimeSpan.Zero, CancellationToken.None));

        Assert.Equal("2026.11.30", _tools.InstalledVersion(Tool.YtDlp));
        Assert.Equal(YtDlpExe, await File.ReadAllBytesAsync(ToolPaths.For(_settings, Tool.YtDlp)));
        Assert.Empty(Directory.GetFiles(_settings.ToolsDirectory, "*.old"));
    }

    [Fact]
    public async Task A_yt_dlp_not_installed_by_OwnIDM_is_never_updated()
    {
        Assert.False(await _tools.UpdateYtDlpAsync(TimeSpan.Zero, CancellationToken.None));
        await _tools.MaintainAsync(); // and the startup check stays quiet
        Assert.Null(ToolPaths.Installed(_settings, Tool.YtDlp));
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Progress&lt;T&gt; posts to the thread pool; tests want the reports in order.</summary>
    private sealed class SyncProgress(Action<ToolProgress> report) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) => report(value);
    }
}
