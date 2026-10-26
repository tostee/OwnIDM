using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OwnIDM.Core.Engine;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Update;

namespace OwnIDM.Core.Tests;

/// <summary>Checks and downloads OwnIDM updates from a local imitation of a release folder.</summary>
public sealed class AppUpdaterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _base = "";
    private AppSettings _settings = null!;
    private DownloadEngine _engine = null!;

    private static readonly byte[] Installer = Encoding.ASCII.GetBytes("MZ OwnIDM-Setup-1.2.0");
    private string _publishedSha = Convert.ToHexString(SHA256.HashData(Installer));

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();
        // GitHub-like layout: latest.json and the installer side by side, url relative to the feed.
        // Like GitHub, the feed redirects to a CDN address where the installer does not exist.
        _app.MapGet("/releases/latest/download/latest.json", () => Results.Redirect("/cdn/asset-123?jwt=signed"));
        _app.MapGet("/cdn/asset-123", () => Results.Text(
            $$"""{"version": "1.2.0", "url": "OwnIDM-Setup-1.2.0.exe", "size": {{Installer.Length}}, "sha256": "{{_publishedSha}}", "notes": "Novedades"}""",
            "application/json"));
        _app.MapGet("/releases/latest/download/OwnIDM-Setup-1.2.0.exe", () =>
            Results.File(Installer, "application/octet-stream", enableRangeProcessing: true));
        await _app.StartAsync();
        _base = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        _settings = new AppSettings { TempDirectory = Path.Combine(_root, "tmp"), MaxRetries = 0 };
        _engine = new DownloadEngine(_settings);
    }

    public async Task DisposeAsync()
    {
        _engine.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private AppUpdater Updater(string current) =>
        new(_settings, _engine, $"{_base}/releases/latest/download/latest.json", Version.Parse(current),
            updateDirectory: Path.Combine(_root, "update"));

    [Fact]
    public async Task Offers_a_newer_version_with_the_installer_address_resolved_against_the_feed()
    {
        using var updater = Updater("1.1.9");
        var release = await updater.CheckAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release.Version);
        Assert.Equal($"{_base}/releases/latest/download/OwnIDM-Setup-1.2.0.exe", release.Url);
        Assert.Equal("Novedades", release.Notes);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.2")]
    [InlineData("1.2.0.0")]
    [InlineData("1.3.0")]
    public async Task Same_or_older_published_version_is_not_offered(string current)
    {
        using var updater = Updater(current);
        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_feed_updates_are_disabled()
    {
        using var updater = new AppUpdater(_settings, _engine, "", new Version(1, 0, 0));
        Assert.False(updater.Enabled);
        Assert.Null(await updater.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Downloads_and_verifies_the_installer()
    {
        using var updater = Updater("1.0.0");
        var release = (await updater.CheckAsync(CancellationToken.None))!;

        var path = await updater.DownloadAsync(release, null, CancellationToken.None);

        Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
        Assert.Equal("OwnIDM-Setup-1.2.0.exe", Path.GetFileName(path));
        updater.Cleanup();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Rejects_an_installer_whose_sha256_does_not_match()
    {
        _publishedSha = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        using var updater = Updater("1.0.0");
        var release = (await updater.CheckAsync(CancellationToken.None))!;

        var ex = await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(release, null, CancellationToken.None));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFiles(updater.UpdateDirectory));
    }

    [Theory]
    [InlineData("""{"version": "1.2.0", "url": "http://example.com/setup.exe", "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData("""{"version": "1.2.0", "url": "setup.exe"}""")]
    [InlineData("""{"version": "nueva", "url": "setup.exe", "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    public void Rejects_feeds_without_https_checksum_or_version(string json) =>
        Assert.Throws<JsonException>(() => AppUpdater.Parse(json, new Uri("https://example.com/latest.json")));

    [Fact]
    public void Reads_the_feed_written_by_the_build_script()
    {
        // PowerShell's ConvertTo-Json output, with a "v" tag-style version.
        var json = "{\r\n    \"version\":  \"v2.0.1\",\r\n    \"url\":  \"OwnIDM-Setup-2.0.1.exe\",\r\n    \"size\":  2245614,\r\n" +
                   "    \"sha256\":  \"" + new string('B', 64) + "\",\r\n    \"notes\":  \"\"\r\n}";
        var release = AppUpdater.Parse(json, new Uri("https://github.com/u/OwnIDM/releases/latest/download/latest.json"));

        Assert.Equal(new Version(2, 0, 1), release.Version);
        Assert.Equal("https://github.com/u/OwnIDM/releases/latest/download/OwnIDM-Setup-2.0.1.exe", release.Url);
        Assert.Equal(2245614, release.Size);
    }
}
