using System.Collections.Concurrent;
using OwnIDM.Core.Grabber;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Scheduling;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Tests;

public class QueueSchedulerTests
{
    // 2026-10-26 is a Monday.
    private static readonly DateTime Monday = new(2026, 10, 26);

    [Fact]
    public void Daily_start_fires_once_per_selected_day()
    {
        var s = new QueueSchedule { StartEnabled = true, StartTime = new TimeOnly(2, 0), Days = [DayOfWeek.Monday] };

        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddHours(1).AddMinutes(59)));
        Assert.Equal(ScheduledAction.StartQueue, QueueScheduler.Evaluate(s, Monday.AddHours(2).AddSeconds(1)));
        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddHours(2).AddSeconds(30)));
        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddDays(1).AddHours(2).AddSeconds(1)));
        Assert.Equal(ScheduledAction.StartQueue, QueueScheduler.Evaluate(s, Monday.AddDays(7).AddHours(2).AddSeconds(1)));
    }

    [Fact]
    public void Missed_start_does_not_fire_late()
    {
        var s = new QueueSchedule { StartEnabled = true, StartTime = new TimeOnly(2, 0) };
        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddHours(2).AddMinutes(10)));
    }

    [Fact]
    public void Once_mode_only_fires_on_its_date()
    {
        var s = new QueueSchedule
        {
            StartEnabled = true, Daily = false, StartTime = new TimeOnly(23, 30),
            OnceDate = DateOnly.FromDateTime(Monday.AddDays(2)),
        };
        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddHours(23.5)));
        Assert.Equal(ScheduledAction.StartQueue, QueueScheduler.Evaluate(s, Monday.AddDays(2).AddHours(23.5)));
        Assert.Equal(Monday.AddDays(2).AddHours(23.5), QueueScheduler.NextStart(s, Monday));
        Assert.Null(QueueScheduler.NextStart(s, Monday.AddDays(3)));
    }

    [Fact]
    public void Start_just_before_midnight_still_fires_after_it()
    {
        var s = new QueueSchedule { StartEnabled = true, StartTime = new TimeOnly(23, 59) };
        Assert.Equal(ScheduledAction.StartQueue, QueueScheduler.Evaluate(s, Monday.AddDays(1).AddSeconds(30)));
        Assert.Equal(ScheduledAction.None, QueueScheduler.Evaluate(s, Monday.AddDays(1).AddSeconds(40)));
    }

    [Fact]
    public void Stop_fires_every_day_and_takes_precedence()
    {
        var s = new QueueSchedule
        {
            StartEnabled = true, StartTime = new TimeOnly(7, 0),
            StopEnabled = true, StopTime = new TimeOnly(7, 0),
        };
        Assert.Equal(ScheduledAction.StopQueue, QueueScheduler.Evaluate(s, Monday.AddHours(7)));
        Assert.Equal(ScheduledAction.StartQueue, QueueScheduler.Evaluate(s, Monday.AddHours(7)));
        Assert.Equal(ScheduledAction.StopQueue, QueueScheduler.Evaluate(s, Monday.AddDays(1).AddHours(7)));
    }
}

public sealed class QueueManagerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync(1024 * 1024);

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Queue_runs_in_order_and_reports_completion_only_after_real_work()
    {
        SingleThreadContext.Run(async () =>
        {
            using var manager = new DownloadManager(new Persistence(Path.Combine(_root, "data")));
            manager.Settings.TempDirectory = Path.Combine(_root, "tmp");
            manager.Settings.MaxConcurrentDownloads = 1;
            int completions = 0;
            manager.QueueCompleted += () => completions++;

            manager.StartQueue(); // nothing queued: must not count as a finished run (could power off the PC)
            Assert.Equal(0, completions);
            Assert.False(manager.QueueRunning);

            var a = manager.CreateItem(_server.BaseUrl + "/file.bin");
            var b = manager.CreateItem(_server.BaseUrl + "/tiny.bin");
            a.SaveDirectory = b.SaveDirectory = Path.Combine(_root, "out");
            manager.Add(a, startNow: false);
            manager.Add(b, startNow: false);
            manager.MoveInQueue(b, -1);
            Assert.Same(b, manager.Items.First(i => i.InQueue));

            var done = new TaskCompletionSource();
            manager.QueueCompleted += () => done.TrySetResult();
            manager.StartQueue();
            Assert.True(manager.IsRunning(b));
            Assert.False(manager.IsRunning(a)); // one at a time, b first
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, completions);
            Assert.All(new[] { a, b }, i => Assert.Equal(DownloadStatus.Completed, i.Status));
            Assert.True(b.LastTryAt <= a.LastTryAt);
        });
    }

    private DownloadManager NewManager()
    {
        var manager = new DownloadManager(new Persistence(Path.Combine(_root, "data", Guid.NewGuid().ToString("N"))));
        manager.Settings.TempDirectory = Path.Combine(_root, "tmp");
        manager.Settings.MaxConcurrentDownloads = 1;
        manager.Settings.SpeedLimiterEnabled = true;
        manager.Settings.SpeedLimitKBps = 64; // slow enough to interrupt
        return manager;
    }

    private DownloadItem Queue(DownloadManager manager, string path)
    {
        var item = manager.CreateItem(_server.BaseUrl + path);
        item.SaveDirectory = Path.Combine(_root, "out");
        manager.Add(item, startNow: false);
        return item;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public void Pausing_or_unqueueing_the_last_download_never_counts_as_a_finished_queue()
    {
        SingleThreadContext.Run(async () =>
        {
            using var manager = NewManager();
            int completions = 0;
            manager.QueueCompleted += () => completions++;

            var a = Queue(manager, "/tiny.bin");
            var b = Queue(manager, "/file.bin");
            manager.StartQueue();
            await WaitUntil(() => a.Status == DownloadStatus.Completed && manager.IsRunning(b));
            manager.Pause(b); // the user pauses the last running item
            await WaitUntil(() => !manager.IsRunning(b));
            Assert.False(manager.QueueRunning);
            Assert.Equal(0, completions);

            manager.StartQueue();
            await WaitUntil(() => manager.IsRunning(b));
            manager.SetInQueue(b, false); // taken out of the queue while downloading
            Assert.True(manager.IsRunning(b));
            manager.Pause(b);
            await WaitUntil(() => !manager.IsRunning(b));
            Assert.Equal(0, completions);
        });
    }

    [Fact]
    public void A_queue_where_everything_failed_does_not_count_as_finished()
    {
        SingleThreadContext.Run(async () =>
        {
            using var manager = NewManager();
            int completions = 0;
            manager.QueueCompleted += () => completions++;
            var missing = Queue(manager, "/missing.bin");
            manager.StartQueue();
            await WaitUntil(() => missing.Status == DownloadStatus.Error && !manager.QueueRunning);
            await Task.Delay(100);
            Assert.Equal(0, completions);
        });
    }

    [Fact]
    public void Completion_waits_for_downloads_outside_the_queue()
    {
        SingleThreadContext.Run(async () =>
        {
            using var manager = NewManager();
            int completions = 0;
            manager.QueueCompleted += () => completions++;
            var manual = manager.CreateItem(_server.BaseUrl + "/file.bin");
            manual.SaveDirectory = Path.Combine(_root, "out");
            manager.Add(manual, startNow: true);
            var queued = Queue(manager, "/tiny.bin");
            manager.StartQueue();
            await WaitUntil(() => queued.Status == DownloadStatus.Completed && !manager.QueueRunning);
            Assert.Equal(0, completions); // the manual download is still running
            manager.Settings.SpeedLimiterEnabled = false;
            await WaitUntil(() => manual.Status == DownloadStatus.Completed);
            await WaitUntil(() => completions == 1);
        });
    }
}

public class HlsParserTests
{
    private static readonly Uri Base = new("https://cdn.example.com/video/master.m3u8");

    [Fact]
    public void Master_playlist_lists_variants_best_first()
    {
        var variants = HlsParser.ParseMaster(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS=\"avc1.4d401e,mp4a.40.2\",RESOLUTION=640x360\nlow/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhttps://other.example.com/hd.m3u8\n", Base);

        Assert.Equal(2, variants.Count);
        Assert.Equal("1920x1080", variants[0].Resolution);
        Assert.Equal("https://other.example.com/hd.m3u8", variants[0].Url.ToString());
        Assert.Equal("https://cdn.example.com/video/low/index.m3u8", variants[1].Url.ToString());
        Assert.Equal("avc1.4d401e,mp4a.40.2", variants[1].Codecs);
    }

    [Fact]
    public void Media_playlist_reads_segments_byteranges_and_init()
    {
        var p = HlsParser.ParseMedia(
            "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\",BYTERANGE=\"720@0\"\n#EXTINF:4.5,\n#EXT-X-BYTERANGE:1000@720\nmain.mp4\n" +
            "#EXTINF:4.5,\n#EXT-X-BYTERANGE:500\nmain.mp4\n#EXT-X-ENDLIST\n", Base);

        Assert.False(p.IsLive);
        Assert.False(p.Encrypted);
        Assert.Equal(9.0, p.TotalSeconds);
        Assert.Equal((720L, 0L), (p.Init!.Length!.Value, p.Init.Offset!.Value));
        Assert.Equal((1000L, 720L), (p.Segments[0].Length!.Value, p.Segments[0].Offset!.Value));
        Assert.Equal((500L, 1720L), (p.Segments[1].Length!.Value, p.Segments[1].Offset!.Value));
    }

    [Fact]
    public void Separate_audio_rendition_is_found_for_its_variant()
    {
        const string master = "#EXTM3U\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Español\",DEFAULT=YES,URI=\"audio/es.m3u8\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"muxed\",NAME=\"main\",DEFAULT=YES\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,AUDIO=\"aud\"\nv1.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=500000,AUDIO=\"muxed\"\nv2.m3u8\n";
        var variants = HlsParser.ParseMaster(master, Base);
        var audio = HlsParser.ParseAudio(master, Base);

        Assert.Equal("https://cdn.example.com/video/audio/es.m3u8", HlsParser.AudioFor(variants[0], audio)?.ToString());
        Assert.Null(HlsParser.AudioFor(variants[1], audio));
    }

    [Fact]
    public void Encryption_and_live_streams_are_detected()
    {
        var p = HlsParser.ParseMedia("#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"\n#EXTINF:4,\na.ts\n", Base);
        Assert.True(p.Encrypted);
        Assert.True(p.IsLive);
        Assert.Throws<FormatException>(() => HlsParser.ParseMedia("<html>", Base));
    }
}

public sealed class HlsDownloaderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private TestServer _server = null!;
    private AppSettings _settings = null!;
    private HlsDownloader _hls = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(2 * 1024 * 1024 + 7);
        _settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            DefaultDownloadDirectory = Path.Combine(_root, "out"),
            ConvertHlsToMp4 = false,
            MaxRetries = 1,
        };
        _hls = new HlsDownloader(_settings);
    }

    public async Task DisposeAsync()
    {
        _hls.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DownloadItem NewItem(string path) => new()
    {
        Kind = DownloadKind.Hls,
        Url = _server.BaseUrl + path,
        FileName = "Mi vídeo.mp4",
        SaveDirectory = _settings.DefaultDownloadDirectory,
    };

    [Fact]
    public async Task Probe_reports_variants_and_best_stream()
    {
        var probe = await _hls.ProbeAsync(_server.BaseUrl + "/hls/master.m3u8", null, null, null, CancellationToken.None);

        Assert.Equal(["1280x720", "640x360"], probe.Variants.Select(v => v.Resolution));
        Assert.Equal(TestServer.HlsSegments, probe.SegmentCount);
        Assert.Equal(40, probe.TotalSeconds);
        Assert.False(probe.Encrypted);
    }

    [Fact]
    public async Task Master_playlist_downloads_best_variant_into_one_file()
    {
        var item = NewItem("/hls/master.m3u8");

        await _hls.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("Mi vídeo.ts", item.FileName);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(item.FullPath));
        Assert.Equal(100, item.Progress);
    }

    [Fact]
    public async Task Pause_and_resume_continues_after_last_segment()
    {
        _settings.SpeedLimiterEnabled = true;
        _settings.SpeedLimitKBps = 512;
        var item = NewItem("/hls/media.m3u8");
        using var cts = new CancellationTokenSource();

        var run = _hls.RunAsync(item, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (item.HlsSegmentsDone < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        await run;

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.InRange(item.HlsSegmentsDone, 2, TestServer.HlsSegments - 1);
        var restored = DownloadRecord.From(item).ToItem();
        Assert.Equal(item.HlsBytesDone, restored.HlsBytesDone);

        _settings.SpeedLimiterEnabled = false;
        await _hls.RunAsync(restored, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, restored.Status);
        Assert.Equal(_server.Data, await File.ReadAllBytesAsync(restored.FullPath));
    }

    [Fact]
    public async Task Fragmented_mp4_init_section_is_written_once_across_resume()
    {
        _settings.SpeedLimiterEnabled = true;
        _settings.SpeedLimitKBps = 64;
        var item = NewItem("/hls/fmp4.m3u8");
        using var cts = new CancellationTokenSource();

        var run = _hls.RunAsync(item, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (item.Downloaded < 4096 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        cts.Cancel(); // stop while the first media segment is still downloading
        await run;
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(0, item.HlsSegmentsDone);

        _settings.SpeedLimiterEnabled = false;
        await _hls.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("Mi vídeo.mp4", item.FileName);
        Assert.Equal(TestServer.HlsInit.Concat(_server.Data).ToArray(), await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Encrypted_stream_is_refused()
    {
        var item = NewItem("/hls/encrypted.m3u8");

        await _hls.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("cifrado", item.ErrorMessage);
    }
}

public sealed class SiteGrabberTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync(4096);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<List<string>> GrabAsync(Action<GrabberOptions> configure)
    {
        var options = new GrabberOptions
        {
            StartUrl = _server.BaseUrl + "/site/index.html",
            FileMasks = "zip",
            FilesFromStartSiteOnly = true,
        };
        configure(options);
        var found = new ConcurrentBag<string>();
        var grabber = new SiteGrabber();
        grabber.FileFound += f => found.Add(f.FileName);
        var result = await grabber.RunAsync(options, null, CancellationToken.None);
        Assert.Equal(result.Files.Count, found.Count);
        return result.Files.Select(f => new Uri(f.Url).AbsolutePath).Order().ToList();
    }

    [Fact]
    public async Task Finds_matching_files_within_levels_robots_and_duplicates_rules()
    {
        var files = await GrabAsync(_ => { });

        // mirror/a.zip: same name and size as files/a.zip (duplicate); d.zip: level 2;
        // secret.zip: robots.txt; outside.zip: parent folder; ext.zip: other site.
        Assert.Equal(["/site/c.zip", "/site/download.php", "/site/files/a.zip"], files);
    }

    [Fact]
    public async Task Dynamic_links_on_the_last_level_are_still_checked()
    {
        var files = await GrabAsync(o => o.Depth = 0);
        Assert.Equal(["/site/download.php", "/site/files/a.zip"], files);
    }

    [Fact]
    public async Task Deeper_levels_and_disabled_filters_find_more()
    {
        var files = await GrabAsync(o =>
        {
            o.Depth = 2;
            o.RespectRobots = false;
            o.HideDuplicates = false;
        });

        Assert.Equal(["/site/c.zip", "/site/deep/d.zip", "/site/download.php", "/site/files/a.zip",
                      "/site/mirror/a.zip", "/site/private/secret.zip"], files);
    }

    [Fact]
    public void Masks_accept_wildcards_and_bare_extensions()
    {
        var mask = FileMask.Parse("*.zip, img??.jpg pdf");
        Assert.True(mask.Matches("A.ZIP", false));
        Assert.True(mask.Matches("img01.jpg", false));
        Assert.False(mask.Matches("img001.jpg", false));
        Assert.True(mask.Matches("doc.pdf", false));
        Assert.True(FileMask.Parse("*").Matches("song.mp3", false));
        Assert.False(FileMask.Parse("*").Matches("index.html", true));
    }
}

/// <summary>Runs async code on one thread, like the WPF dispatcher the manager expects.</summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* finished; late continuations are dropped */ }
    }

    public static void Run(Func<Task> body)
    {
        var previous = Current;
        var context = new SingleThreadContext();
        SetSynchronizationContext(context);
        try
        {
            var task = body();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                callback(state);
            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
