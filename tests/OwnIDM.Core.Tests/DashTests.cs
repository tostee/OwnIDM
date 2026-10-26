using OwnIDM.Core.Dash;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Manager;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;

namespace OwnIDM.Core.Tests;

public class DashParserTests
{
    private static readonly Uri Base = new("https://cdn.example.com/video/manifest.mpd");

    private static string Mpd(string periods, string attrs = "type=\"static\" mediaPresentationDuration=\"PT10S\"") =>
        $"<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" {attrs}>{periods}</MPD>";

    [Fact]
    public void Template_with_duration_numbers_segments_and_inherits_from_adaptation_set()
    {
        var m = DashParser.Parse(Mpd(
            "<BaseURL>https://media.example.com/v/</BaseURL><Period>" +
            "<AdaptationSet mimeType=\"video/mp4\">" +
            "<SegmentTemplate timescale=\"90000\" duration=\"360000\" startNumber=\"0\" initialization=\"$RepresentationID$_init.mp4\" media=\"$RepresentationID$_$Number%05d$.m4s?b=$Bandwidth$\"/>" +
            "<Representation id=\"low\" bandwidth=\"500000\" width=\"640\" height=\"360\"/>" +
            "<Representation id=\"high\" bandwidth=\"3000000\" width=\"1920\" height=\"1080\" codecs=\"avc1.640028\"/>" +
            "</AdaptationSet></Period>"), Base);

        Assert.Equal(["high", "low"], m.Video.Select(r => r.Id));
        Assert.Equal(10, m.TotalSeconds);
        var p = m.Video[0].Playlist!;
        Assert.Equal("https://media.example.com/v/high_init.mp4", p.Init!.Url.ToString());
        Assert.Equal(3, p.Segments.Count); // 4 s + 4 s + 2 s
        Assert.Equal("https://media.example.com/v/high_00002.m4s?b=3000000", p.Segments[2].Url.ToString());
        Assert.Equal(2, p.Segments[2].Duration, 3);
        Assert.Equal("1920x1080 · 3.0 Mbps", m.Video[0].ToVariant().Label.Replace(',', '.'));
        Assert.Equal("https://cdn.example.com/video/manifest.mpd#rep=high", m.Video[0].Key.ToString());
        Assert.Same(m.Video[1], m.VideoFor(m.Video[1].Key.ToString()));
        Assert.Same(m.Video[0], m.VideoFor(null));
    }

    [Fact]
    public void Timeline_expands_repeats_and_time_placeholders()
    {
        var m = DashParser.Parse(Mpd(
            "<Period><AdaptationSet contentType=\"video\"><Representation id=\"v\" bandwidth=\"1\">" +
            "<SegmentTemplate timescale=\"1000\" media=\"t/$Time$.m4s\" initialization=\"init.mp4\"><SegmentTimeline>" +
            "<S t=\"1000\" d=\"2000\" r=\"2\"/><S d=\"1000\"/><S d=\"500\" r=\"-1\"/>" +
            "</SegmentTimeline></SegmentTemplate></Representation></AdaptationSet></Period>"), Base);

        var urls = m.Video[0].Playlist!.Segments.Select(s => s.Url.AbsolutePath.Split('/').Last()).ToList();
        // 1000, 3000, 5000 (d=2000 x3), 7000 (d=1000), then 500 ms pieces up to 10 s + offset 0.
        Assert.Equal(["1000.m4s", "3000.m4s", "5000.m4s", "7000.m4s", "8000.m4s", "8500.m4s", "9000.m4s", "9500.m4s"], urls);
    }

    [Fact]
    public void Segment_list_with_byte_ranges()
    {
        var m = DashParser.Parse(Mpd(
            "<Period><AdaptationSet mimeType=\"audio/mp4\" lang=\"es\"><Representation id=\"a\" bandwidth=\"128000\">" +
            "<BaseURL>audio.mp4</BaseURL><SegmentList timescale=\"1000\" duration=\"5000\">" +
            "<Initialization range=\"0-99\"/><SegmentURL mediaRange=\"100-199\"/><SegmentURL mediaRange=\"200-349\"/>" +
            "</SegmentList></Representation></AdaptationSet></Period>"), Base);

        Assert.Empty(m.Video);
        var p = m.Audio[0].Playlist!;
        Assert.Equal((100L, 0L), (p.Init!.Length!.Value, p.Init.Offset!.Value));
        Assert.Equal((150L, 200L), (p.Segments[1].Length!.Value, p.Segments[1].Offset!.Value));
        Assert.Equal("https://cdn.example.com/video/audio.mp4", p.Segments[0].Url.ToString());
        Assert.Equal("es", m.Audio[0].Language);
        Assert.Same(m.Audio[0], m.VideoFor(null)); // audio-only manifests download the audio
    }

    [Fact]
    public void Separate_audio_prefers_main_role_then_bitrate()
    {
        var m = DashParser.Parse(Mpd(
            "<Period><AdaptationSet mimeType=\"video/mp4\"><SegmentTemplate duration=\"5\" media=\"$RepresentationID$-$Number$.m4s\"/>" +
            "<Representation id=\"v\" bandwidth=\"1000\"/></AdaptationSet>" +
            "<AdaptationSet mimeType=\"audio/mp4\"><Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"alternate\"/>" +
            "<SegmentTemplate duration=\"5\" media=\"$RepresentationID$-$Number$.m4s\"/><Representation id=\"alt\" bandwidth=\"256000\"/></AdaptationSet>" +
            "<AdaptationSet mimeType=\"audio/mp4\"><Role schemeIdUri=\"urn:mpeg:dash:role:2011\" value=\"main\"/>" +
            "<SegmentTemplate duration=\"5\" media=\"$RepresentationID$-$Number$.m4s\"/><Representation id=\"main\" bandwidth=\"96000\"/></AdaptationSet>" +
            "<AdaptationSet contentType=\"text\"><Representation id=\"subs\" bandwidth=\"1\"/></AdaptationSet>" +
            "</Period>"), Base);

        Assert.Single(m.Video);
        Assert.Equal(2, m.Audio.Count);
        Assert.Equal("main", m.AudioFor(m.Video[0])!.Id);
        Assert.Equal(2, m.Video[0].Playlist!.Segments.Count);
    }

    [Fact]
    public void Protection_live_and_multiple_periods_are_flagged()
    {
        const string set = "<AdaptationSet mimeType=\"video/mp4\"><ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/>" +
                           "<SegmentTemplate duration=\"5\" media=\"$Number$.m4s\"/><Representation id=\"v\" bandwidth=\"1\"/></AdaptationSet>";
        var drm = DashParser.Parse(Mpd($"<Period>{set}</Period>"), Base);
        Assert.True(drm.Video[0].Encrypted);
        Assert.False(drm.IsLive);

        var live = DashParser.Parse(Mpd($"<Period>{set}</Period>", "type=\"dynamic\""), Base);
        Assert.True(live.IsLive);

        var periods = DashParser.Parse(Mpd($"<Period duration=\"PT5S\">{set}</Period><Period duration=\"PT5S\">{set}</Period>"), Base);
        Assert.True(periods.MultiplePeriods);

        Assert.False(DashParser.IsManifest("#EXTM3U\n"));
        Assert.True(DashParser.IsManifest("﻿<?xml version=\"1.0\"?><MPD/>"));
        Assert.Throws<FormatException>(() => DashParser.Parse("<html><body>no</body></html>", Base));
        Assert.Throws<FormatException>(() => DashParser.Parse("<MPD", Base));
    }

    [Fact]
    public void Sidx_index_lists_byte_ranges_after_the_index()
    {
        var sidx = TestServer.Sidx([1000, 2000, 500]);
        var info = new DashIndex(Base, IndexOffset: 700, IndexLength: sidx.Length, InitOffset: null, InitLength: null);

        var p = DashParser.FromSidx(info, sidx);

        Assert.Equal((700L, 0L), (p.Init!.Length!.Value, p.Init.Offset!.Value));
        Assert.Equal([700L + sidx.Length, 1700L + sidx.Length, 3700L + sidx.Length], p.Segments.Select(s => s.Offset!.Value));
        Assert.Equal([1000L, 2000L, 500L], p.Segments.Select(s => s.Length!.Value));
        Assert.Equal(12, p.TotalSeconds, 3);
    }

    [Fact]
    public void Template_placeholders()
    {
        Assert.Equal("a/7/00042-1200-$.m4s", DashParser.Expand("a/$RepresentationID$/$Number%05d$-$Time$-$$.m4s", "7", 0, 42, 1200));
        Assert.Equal("b_3000.m4s", DashParser.Expand("b_$Bandwidth$.m4s", "x", 3000, 1, 0));
    }
}

public sealed class DashDownloaderTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private TestServer _server = null!;
    private AppSettings _settings = null!;
    private HlsDownloader _streams = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(2 * 1024 * 1024 + 7);
        _settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            DefaultDownloadDirectory = Path.Combine(_root, "out"),
            MaxRetries = 1,
        };
        _streams = new HlsDownloader(_settings);
    }

    public async Task DisposeAsync()
    {
        _streams.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DownloadItem NewItem(string path) => new()
    {
        Kind = DownloadKind.Dash,
        Url = _server.BaseUrl + path,
        FileName = "Mi vídeo.mp4",
        SaveDirectory = _settings.DefaultDownloadDirectory,
    };

    private byte[] Expected => TestServer.HlsInit.Concat(_server.Data).ToArray();

    [Fact]
    public async Task Probe_describes_representations()
    {
        var probe = await _streams.ProbeAsync(_server.BaseUrl + "/dash/template.mpd", null, null, null, CancellationToken.None);

        Assert.Equal(["1280x720", "640x360"], probe.Variants.Select(v => v.Resolution));
        Assert.Equal(TestServer.HlsSegments, probe.SegmentCount);
        Assert.Equal(40, probe.TotalSeconds);
        Assert.False(probe.SeparateAudio || probe.Encrypted || probe.IsLive || probe.Unsupported);
    }

    [Fact]
    public async Task Template_manifest_downloads_best_representation()
    {
        var item = NewItem("/dash/template.mpd");

        await _streams.RunAsync(item, CancellationToken.None);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("Mi vídeo.mp4", item.FileName);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(item.FullPath));
        Assert.Equal(_server.BaseUrl + "/dash/template.mpd", item.SourceUrl);
        Assert.EndsWith("#rep=hd", item.Url);
        Assert.Equal(100, item.Progress);
    }

    [Fact]
    public async Task Chosen_representation_is_used()
    {
        var probe = await _streams.ProbeAsync(_server.BaseUrl + "/dash/template.mpd", null, null, null, CancellationToken.None);
        var item = NewItem("/dash/template.mpd");
        item.SourceUrl = item.Url;
        item.Url = probe.Variants[1].Url.ToString(); // "sd": its segments do not exist on the server

        await _streams.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.EndsWith("#rep=sd", item.Url);
    }

    [Fact]
    public async Task Indexed_single_file_is_read_through_its_sidx()
    {
        var item = NewItem("/dash/indexed.mpd");

        await _streams.RunAsync(item, CancellationToken.None);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal(TestServer.HlsSegments, item.HlsSegmentCount);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(item.FullPath));
    }

    [Fact]
    public async Task Pause_and_resume_keeps_the_representation()
    {
        _settings.SpeedLimiterEnabled = true;
        _settings.SpeedLimitKBps = 512;
        var item = NewItem("/dash/template.mpd");
        using var cts = new CancellationTokenSource();

        var run = _streams.RunAsync(item, cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (item.HlsSegmentsDone < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        await run;

        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.InRange(item.HlsSegmentsDone, 2, TestServer.HlsSegments - 1);
        var restored = DownloadRecord.From(item).ToItem();
        Assert.Equal(DownloadKind.Dash, restored.Kind);
        Assert.Equal(item.Url, restored.Url);

        _settings.SpeedLimiterEnabled = false;
        await _streams.RunAsync(restored, CancellationToken.None);

        Assert.True(restored.Status == DownloadStatus.Completed, restored.ErrorMessage);
        Assert.Equal(Expected, await File.ReadAllBytesAsync(restored.FullPath));
    }

    [Fact]
    public async Task Protected_manifest_is_refused()
    {
        var probe = await _streams.ProbeAsync(_server.BaseUrl + "/dash/protected.mpd", null, null, null, CancellationToken.None);
        Assert.True(probe.Encrypted);

        var item = NewItem("/dash/protected.mpd");
        await _streams.RunAsync(item, CancellationToken.None);

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Contains("cifrado", item.ErrorMessage);
    }
}
