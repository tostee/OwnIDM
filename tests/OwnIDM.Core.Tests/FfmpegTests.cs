using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using OwnIDM.Core.Hls;
using OwnIDM.Core.Models;
using OwnIDM.Core.Settings;
using OwnIDM.Core.YtDlp;

namespace OwnIDM.Core.Tests;

/// <summary>End-to-end HLS, DASH and yt-dlp tests with real media; they need ffmpeg (and yt-dlp) and do nothing without them.</summary>
public sealed class FfmpegTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "OwnIDM.Tests", Guid.NewGuid().ToString("N"));
    private readonly string? _ffmpeg = Ffmpeg.Find(new AppSettings());
    private WebApplication? _app;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        if (_ffmpeg is null)
            return;
        var site = Directory.CreateDirectory(Path.Combine(_root, "site")).FullName;
        // 4 s of video and a separate audio rendition, as many streaming sites publish.
        Run(_ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-g", "25", "-c:a", "aac",
            "-f", "hls", "-hls_time", "1", "-hls_playlist_type", "vod",
            "-var_stream_map", "v:0,agroup:aud a:0,agroup:aud,default:yes",
            "-master_pl_name", "master.m3u8", "-hls_segment_filename", Path.Combine(site, "s%v_%03d.ts"),
            Path.Combine(site, "p%v.m3u8"));
        MakeDash(site);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(site),
            ServeUnknownFileTypes = true,
        });
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>The same 4 s as DASH: SegmentTemplate + SegmentTimeline, SegmentList with byte ranges, SegmentBase (sidx).</summary>
    private void MakeDash(string site)
    {
        string[] input =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=4",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4",
            "-c:v", "libx264", "-g", "25", "-c:a", "aac",
        ];
        string[] dash = ["-map", "0:v", "-map", "1:a", "-f", "dash", "-seg_duration", "1", "-adaptation_sets", "id=0,streams=v id=1,streams=a"];
        // The DASH muxer only finds the manifest's folder (where segments go) with forward slashes.
        string Manifest(string folder) => Directory.CreateDirectory(Path.Combine(site, folder)).FullName.Replace('\\', '/') + "/manifest.mpd";
        Run(_ffmpeg!, [.. input, .. dash, "-use_template", "1", "-use_timeline", "1", Manifest("dash-template")]);
        Run(_ffmpeg!, [.. input, .. dash, "-single_file", "1", Manifest("dash-list")]);

        // ffmpeg's DASH muxer never writes SegmentBase: build it from fragmented MP4s with a global sidx.
        var indexed = Directory.CreateDirectory(Path.Combine(site, "dash-indexed")).FullName;
        string[] fragmented = ["-movflags", "+frag_keyframe+empty_moov+default_base_moof+global_sidx", "-f", "mp4"];
        Run(_ffmpeg!, [.. input, "-map", "0:v", .. fragmented, Path.Combine(indexed, "v.mp4")]);
        Run(_ffmpeg!, [.. input, "-map", "1:a", .. fragmented, Path.Combine(indexed, "a.mp4")]);
        string Rep(string file, string mime, string codecs) =>
            $"<AdaptationSet mimeType=\"{mime}\"><Representation id=\"{file}\" bandwidth=\"100000\" codecs=\"{codecs}\"><BaseURL>{file}</BaseURL>" +
            $"<SegmentBase indexRange=\"{SidxRange(Path.Combine(indexed, file))}\"/></Representation></AdaptationSet>";
        File.WriteAllText(Path.Combine(indexed, "manifest.mpd"),
            "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT4S\"><Period>" +
            Rep("v.mp4", "video/mp4", "avc1.64000c") + Rep("a.mp4", "audio/mp4", "mp4a.40.2") + "</Period></MPD>");
    }

    /// <summary>"first-last" byte range of the top-level sidx box.</summary>
    private static string SidxRange(string file)
    {
        var data = File.ReadAllBytes(file);
        for (int pos = 0; pos + 8 <= data.Length;)
        {
            int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (data.AsSpan(pos + 4, 4).SequenceEqual("sidx"u8))
                return $"{pos}-{pos + size - 1}";
            pos += size;
        }
        throw new InvalidOperationException("ffmpeg no escribió el índice sidx");
    }

    [Theory]
    [InlineData("/master.m3u8", DownloadKind.Hls)]
    [InlineData("/dash-template/manifest.mpd", DownloadKind.Dash)]
    [InlineData("/dash-list/manifest.mpd", DownloadKind.Dash)]
    [InlineData("/dash-indexed/manifest.mpd", DownloadKind.Dash)]
    public async Task Separate_audio_is_downloaded_and_muxed_into_one_mp4(string path, DownloadKind kind)
    {
        // Set OWNIDM_REQUIRE_FFMPEG=1 to make a missing ffmpeg a failure instead of a no-op.
        Assert.False(_ffmpeg is null && Environment.GetEnvironmentVariable("OWNIDM_REQUIRE_FFMPEG") == "1", "ffmpeg no encontrado");
        if (_ffmpeg is null)
            return; // ffmpeg not installed: nothing to verify here

        var settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp"),
            DefaultDownloadDirectory = Path.Combine(_root, "out"),
        };
        using var hls = new HlsDownloader(settings);
        var probe = await hls.ProbeAsync(_baseUrl + path, null, null, null, CancellationToken.None);
        Assert.True(probe.SeparateAudio);
        Assert.InRange(probe.TotalSeconds, 3.9, 4.1);

        var item = new DownloadItem
        {
            Kind = kind,
            Url = _baseUrl + path,
            FileName = "con audio.mp4",
            SaveDirectory = settings.DefaultDownloadDirectory,
        };
        await hls.RunAsync(item, CancellationToken.None);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("con audio.mp4", item.FileName);
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpeg)!, "ffprobe.exe");
        var streams = Run(ffprobe, "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", item.FullPath);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
        var duration = Run(ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", item.FullPath);
        Assert.InRange(double.Parse(duration, System.Globalization.CultureInfo.InvariantCulture), 3.9, 4.2);
        Assert.Empty(Directory.GetFiles(settings.TempDirectory));
    }

    [Fact]
    public async Task YtDlp_downloads_pauses_resumes_and_merges()
    {
        // yt-dlp from OWNIDM_YTDLP or PATH; OWNIDM_REQUIRE_YTDLP=1 makes its absence a failure.
        var ytdlp = Environment.GetEnvironmentVariable("OWNIDM_YTDLP") ?? YtDlpTool.Find(new AppSettings());
        Assert.False((_ffmpeg is null || ytdlp is null) && Environment.GetEnvironmentVariable("OWNIDM_REQUIRE_YTDLP") == "1",
            "yt-dlp o ffmpeg no encontrados");
        if (_ffmpeg is null || ytdlp is null)
            return;

        var settings = new AppSettings
        {
            TempDirectory = Path.Combine(_root, "tmp-ytdlp"),
            DefaultDownloadDirectory = Path.Combine(_root, "out-ytdlp"),
            YtDlpPath = ytdlp,
            SpeedLimiterEnabled = true,
            SpeedLimitKBps = 16,
        };
        var downloader = new YtDlpDownloader(settings);
        // yt-dlp's generic extractor reads the DASH manifest: separate video and audio, as on YouTube.
        var url = _baseUrl + "/dash-template/manifest.mpd";
        var item = new DownloadItem
        {
            Kind = DownloadKind.YtDlp,
            Url = url,
            FileName = "página.mp4",
            SaveDirectory = settings.DefaultDownloadDirectory,
            YtDlpQuality = "180",
        };
        // The page read when probing is kept for the download, which then skips reading it again.
        var probe = await downloader.ProbeAsync(url, null, null, CancellationToken.None, downloader.InfoPathFor(item));
        Assert.True(File.Exists(downloader.InfoPathFor(item)));
        Assert.Contains(probe.Qualities, q => q.Key == YtDlpTool.AudioKey);
        var quality = Assert.Single(probe.Qualities, q => q.Key == "180");
        Assert.Contains('+', quality.Formats!); // separate video and audio, merged by ffmpeg
        item.YtDlpFormat = quality.Formats;

        using (var cts = new CancellationTokenSource())
        {
            var run = downloader.RunAsync(item, cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            long shown = -1;
            while (item.Downloaded < 20_000 && !run.IsCompleted && DateTime.UtcNow < deadline)
            {
                // Once known, the size never goes back: the audio counts while the video still arrives.
                Assert.True(item.TotalSize >= shown, $"{item.TotalSize} < {shown}");
                shown = item.TotalSize;
                await Task.Delay(50);
            }
            cts.Cancel();
            await run;
        }
        Assert.True(item.Status == DownloadStatus.Paused, item.ErrorMessage);
        Assert.True(item.ResumeSupported);
        Assert.True(item.TotalSize > item.Downloaded);
        Assert.NotEmpty(Directory.GetFileSystemEntries(downloader.TempDirectoryFor(item)));

        // Resume as after a restart: the reloaded item keeps its quality and formats and finds yt-dlp's .part files.
        item = Manager.DownloadRecord.From(item).ToItem();
        Assert.Equal(("180", quality.Formats), (item.YtDlpQuality, item.YtDlpFormat));
        settings.SpeedLimiterEnabled = false;
        await downloader.RunAsync(item, CancellationToken.None);

        Assert.True(item.Status == DownloadStatus.Completed, item.ErrorMessage);
        Assert.Equal("página.mp4", item.FileName);
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpeg)!, "ffprobe.exe");
        var streams = Run(ffprobe, "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", item.FullPath);
        Assert.Contains("video", streams);
        Assert.Contains("audio", streams);
        Assert.False(Directory.Exists(downloader.TempDirectoryFor(item)));
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, error);
        return output;
    }
}
