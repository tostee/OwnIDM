using OwnIDM.Core.Engine;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.Util;

namespace OwnIDM.Core.Tests;

public sealed class DownloadEngineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private TestServer _server = null!;
    private AppSettings _settings = null!;
    private DownloadEngine _engine = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(6 * 1024 * 1024 + 123);
        _settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            DefaultDownloadDirectory = Path.Combine(_root, "out"),
            MaxConnectionsPerDownload = 8,
            MaxRetries = 2,
            TimeoutSeconds = 10,
        };
        _engine = new DownloadEngine(_settings);
    }

    public async Task DisposeAsync()
    {
        _engine.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DownloadItem NewItem(string path) => new()
    {
        Url = _server.BaseUrl + path,
        SaveDirectory = _settings.DefaultDownloadDirectory,
    };

    [Fact]
    public async Task Segmented_download_produces_identical_file_using_several_connections()
    {
        var item = NewItem("/file.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("file.bin", item.FileName);
        Assert.Equal(_server.Data.Length, item.TotalSize);
        Assert.True(item.ResumeSupported);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.True(_server.RangeRequests > 2, $"expected several ranged connections, got {_server.RangeRequests}");
        Assert.False(File.Exists(_engine.TempPathFor(item)));
    }

    [Fact]
    public async Task Server_without_ranges_falls_back_to_one_connection()
    {
        var item = NewItem("/norange.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.False(item.ResumeSupported);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Unknown_length_download_completes_at_end_of_stream()
    {
        var item = NewItem("/chunked.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(_server.Data.Length, item.TotalSize);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Empty_file_completes()
    {
        var item = NewItem("/empty.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Empty(await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Http_error_sets_error_status()
    {
        var item = NewItem("/missing.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("404", item.ErrorMessage);
    }

    [Fact]
    public async Task Pause_persist_and_resume_produces_identical_file()
    {
        _settings.SpeedLimiterEnabled = true;
        _settings.SpeedLimitKBps = 1024;
        var item = NewItem("/file.bin");
        using var cts = new CancellationTokenSource();

        var run = _engine.RunAsync(item, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (item.Downloaded < 512 * 1024 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            item.RefreshProgress();
        }
        cts.Cancel();
        await run;

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.InRange(item.Downloaded, 1, _server.Data.Length - 1);
        Assert.NotEmpty(item.SnapshotSegments());

        // Simulate an app restart: the item comes back from its JSON record.
        var restored = DownloadRecord.From(item).ToItem();
        _settings.SpeedLimiterEnabled = false;
        int rangeRequestsBefore = _server.RangeRequests;

        await _engine.RunAsync(restored, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, restored.Status);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(restored.FullPath));
        Assert.True(_server.RangeRequests > rangeRequestsBefore);
    }

    [Fact]
    public async Task Speed_limiter_handles_segments_smaller_than_one_kilobyte()
    {
        _settings.SpeedLimiterEnabled = true;
        _settings.SpeedLimitKBps = 100;
        var item = NewItem("/tiny.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(_server.Data.AsSpan(0, 500).ToArray(), await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Empty_file_answered_with_416_completes()
    {
        var item = NewItem("/empty416.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Empty(await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task First_request_is_retried_after_server_errors()
    {
        var item = NewItem("/flaky.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Connection_drop_without_ranges_restarts_from_zero()
    {
        var item = NewItem("/drop.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Theory]
    [InlineData("/redirect-same", "s=1")]
    [InlineData("/redirect-other", "")]
    public async Task Cookies_follow_redirects_only_on_the_same_host(string path, string expectedCookie)
    {
        var item = NewItem(path);
        item.Cookies = "s=1";

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(expectedCookie, await File.ReadAllTextAsync(item.FullPath));
    }

    [Fact]
    public void Cookie_scope_rules()
    {
        var origin = new Uri("https://www.example.com/video");
        Assert.True(Redirects.CookiesAllowed(new Uri("https://www.example.com/a"), origin));
        Assert.True(Redirects.CookiesAllowed(new Uri("https://cdn.www.example.com/a"), origin));
        Assert.False(Redirects.CookiesAllowed(new Uri("https://evil.example/a"), origin));
        Assert.False(Redirects.CookiesAllowed(new Uri("https://www.example.com.evil.net/a"), origin));
        Assert.False(Redirects.CookiesAllowed(new Uri("http://www.example.com/a"), origin));
    }

    [Fact]
    public async Task Probe_reads_name_size_and_range_support()
    {
        var probe = await _engine.ProbeAsync(_server.BaseUrl + "/attachment", null, null, null, CancellationToken.None);

        Assert.Equal("nombre real.zip", probe.FileName);
        Assert.Equal(_server.Data.Length, probe.TotalSize);
        Assert.True(probe.ResumeSupported);
    }

    [Fact]
    public async Task Existing_file_gets_numbered_name()
    {
        Directory.CreateDirectory(_settings.DefaultDownloadDirectory);
        await File.WriteAllTextAsync(Path.Combine(_settings.DefaultDownloadDirectory, "file.bin"), "old");
        var item = NewItem("/file.bin");

        await _engine.RunAsync(item, CancellationToken.None);

        Assert.Equal("file (1).bin", item.FileName);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
    }
}

public class BatchPatternTests
{
    [Fact]
    public void Expands_numbers_with_padding()
    {
        var urls = BatchPattern.Expand("http://x/img*.jpg", letters: false, "8", "10", width: 3);
        Assert.Equal(["http://x/img008.jpg", "http://x/img009.jpg", "http://x/img010.jpg"], urls);
    }

    [Fact]
    public void Expands_letters()
    {
        var urls = BatchPattern.Expand("http://x/*.zip", letters: true, "a", "c", width: 1);
        Assert.Equal(["http://x/a.zip", "http://x/b.zip", "http://x/c.zip"], urls);
    }

    [Fact]
    public void Category_is_chosen_by_extension()
    {
        var s = new AppSettings();
        Assert.Equal("Vídeo", s.CategoryFor("peli.MKV"));
        Assert.Equal(AppSettings.GeneralCategory, s.CategoryFor("algo.xyz"));
    }
}
