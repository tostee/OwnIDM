using OwnIDM.Core.YtDlp;

namespace OwnIDM.Core.Tests;

public class YtDlpToolTests
{
    // Trimmed from the real `yt-dlp -J` of a YouTube video.
    private const string Info = """
        {"_type": "video", "title": "Big Buck Bunny", "extractor_key": "Youtube", "duration": 635, "is_live": false,
         "live_status": "not_live", "formats": [
          {"format_id": "sb0", "ext": "mhtml", "vcodec": "none", "acodec": "none", "height": 180},
          {"format_id": "773", "ext": "m4a", "vcodec": null, "acodec": null, "filesize": 26688526},
          {"format_id": "140", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.2", "filesize": 10271496, "tbr": 129.4},
          {"format_id": "139", "ext": "m4a", "vcodec": "none", "acodec": "mp4a.40.5", "filesize": 3871021, "tbr": 48.7},
          {"format_id": "18", "ext": "mp4", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "height": 360, "filesize_approx": 28526904, "tbr": 359.6},
          {"format_id": "134", "ext": "mp4", "vcodec": "avc1.4D401E", "acodec": "none", "height": 360, "filesize": 18294110, "tbr": 230.6},
          {"format_id": "299", "ext": "mp4", "vcodec": "avc1.64002A", "acodec": "none", "height": 1080, "filesize": 257619653, "tbr": 3247.8},
          {"format_id": "303", "ext": "webm", "vcodec": "vp9", "acodec": "none", "height": 1080, "filesize": 168736189, "tbr": 2127.2},
          {"format_id": "999", "ext": "mp4", "vcodec": "avc1", "acodec": "none", "height": 2160, "has_drm": true}
        ]}
        """;

    [Fact]
    public void Qualities_list_heights_with_the_exact_size_of_the_files_picked_and_audio_only()
    {
        var probe = YtDlpTool.ParseInfo(Info, hasFfmpeg: true);

        Assert.Equal("Big Buck Bunny", probe.Title);
        Assert.Equal("Youtube", probe.Site);
        Assert.Equal(635, probe.Duration);
        Assert.False(probe.IsLive);
        Assert.Equal(["1080", "360", YtDlpTool.AudioKey], probe.Qualities.Select(q => q.Key)); // DRM 2160p left out

        // H.264 rather than the VP9 of the same height, with the AAC audio: what gets downloaded.
        var hd = probe.Qualities[0];
        Assert.Equal("299+140", hd.Formats);
        Assert.Equal(257619653 + 10271496, hd.Size);
        Assert.True(hd.SizeExact);
        Assert.Equal("1080p · " + Util.Format.Bytes(257619653 + 10271496), hd.Label);

        // The video-only 134 (exact size) beats the muxed 18 when ffmpeg can add the better audio.
        Assert.Equal("134+140", probe.Qualities[1].Formats);
        Assert.Equal(("140", 10271496L, true), (probe.Qualities[2].Formats, probe.Qualities[2].Size, probe.Qualities[2].SizeExact));
    }

    [Fact]
    public void Without_ffmpeg_only_formats_carrying_audio_are_offered()
    {
        var probe = YtDlpTool.ParseInfo(Info, hasFfmpeg: false);

        Assert.Equal(["360", YtDlpTool.AudioKey], probe.Qualities.Select(q => q.Key));
        Assert.Equal(["-f", "b", "-S", "res:360,vcodec:h264,acodec:aac"], YtDlpTool.FormatArgs("360", hasFfmpeg: false));

        // Format 18 only publishes an estimate, and says so.
        var sd = probe.Qualities[0];
        Assert.Equal(("18", 28526904L, false), (sd.Formats, sd.Size, sd.SizeExact));
        Assert.StartsWith("360p · ≈ ", sd.Label);
    }

    [Fact]
    public void Formats_picked_when_probing_are_asked_for_first()
    {
        Assert.Equal(["-f", "299+140/bv*+ba/b", "-S", "res:1080,vcodec:h264,acodec:aac", "--merge-output-format", "mp4"],
            YtDlpTool.FormatArgs("1080", hasFfmpeg: true, "299+140"));
        Assert.Equal(["-f", "140/ba[ext=m4a]/ba/b"], YtDlpTool.FormatArgs(YtDlpTool.AudioKey, hasFfmpeg: false, "140"));
        // A pair cannot be merged once ffmpeg is gone.
        Assert.Equal(["-f", "b", "-S", "res:1080,vcodec:h264,acodec:aac"], YtDlpTool.FormatArgs("1080", hasFfmpeg: false, "299+140"));
    }

    [Fact]
    public void Sizes_announced_before_downloading_are_parsed()
    {
        Assert.True(YtDlpTool.TryParseSizes("OWNIDM-SIZE 299+140 NA 268000000 299 257619653 NA 140 NA 10300000", out var parts));
        Assert.Equal([new YtDlpPart("299", 257619653, true), new YtDlpPart("140", 10300000, false)], parts);

        Assert.True(YtDlpTool.TryParseSizes("OWNIDM-SIZE 18 NA 28526904.0 NA NA NA NA NA NA", out parts));
        Assert.Equal([new YtDlpPart("18", 28526904, false)], parts);

        Assert.False(YtDlpTool.TryParseSizes("OWNIDM-SIZE NA NA NA NA NA NA NA NA NA", out _));
        Assert.False(YtDlpTool.TryParseSizes("OWNIDM-PROGRESS downloading 1 2 NA 1", out _));
    }

    [Fact]
    public void Total_size_is_known_from_the_start_and_estimates_do_not_move_it()
    {
        var planned = new Dictionary<string, YtDlpPart>
        {
            ["299"] = new("299", 1000, true),
            ["140"] = new("140", 100, false),
        };
        var progress = new Dictionary<string, YtDlpProgress>();
        Assert.Equal(1100, YtDlpTool.TotalSize(planned, progress));

        // While the video arrives, the audio still counts.
        progress["299"] = new("299", false, 400, 1000, null);
        Assert.Equal(1100, YtDlpTool.TotalSize(planned, progress));

        // yt-dlp's own estimate for the audio wanders; the announced figure stays.
        progress["299"] = new("299", true, 1000, 1000, null);
        progress["140"] = new("140", false, 10, null, 160);
        Assert.Equal(1100, YtDlpTool.TotalSize(planned, progress));

        // More than estimated arrived; once finished, what arrived is the size.
        progress["140"] = new("140", false, 120, null, 130);
        Assert.Equal(1120, YtDlpTool.TotalSize(planned, progress));
        progress["140"] = new("140", true, 125, 125, null);
        Assert.Equal(1125, YtDlpTool.TotalSize(planned, progress));

        // Nothing announced and no size reported: unknown.
        Assert.Null(YtDlpTool.TotalSize(new Dictionary<string, YtDlpPart>(),
            new Dictionary<string, YtDlpProgress> { ["1"] = new("1", false, 5, null, null) }));
    }

    [Fact]
    public void Pages_without_format_list_get_a_single_best_choice_and_playlists_are_refused()
    {
        var probe = YtDlpTool.ParseInfo("""{"title": "clip", "is_live": true}""", hasFfmpeg: true);
        Assert.True(probe.IsLive);
        Assert.Equal([YtDlpTool.BestKey], probe.Qualities.Select(q => q.Key));

        Assert.Throws<NotSupportedException>(() => YtDlpTool.ParseInfo("""{"_type": "playlist", "entries": []}""", true));
    }

    [Fact]
    public void Format_arguments()
    {
        Assert.Equal(["-f", "bv*+ba/b", "-S", "res:720,vcodec:h264,acodec:aac", "--merge-output-format", "mp4"],
            YtDlpTool.FormatArgs("720", hasFfmpeg: true));
        Assert.Equal(["-f", "bv*+ba/b", "-S", "res,vcodec:h264,acodec:aac", "--merge-output-format", "mp4"],
            YtDlpTool.FormatArgs(null, hasFfmpeg: true));
        Assert.Equal(["-f", "ba[ext=m4a]/ba/b"], YtDlpTool.FormatArgs(YtDlpTool.AudioKey, hasFfmpeg: true));
    }

    [Fact]
    public void Progress_lines_are_parsed()
    {
        Assert.True(YtDlpTool.TryParseProgress("OWNIDM-PROGRESS downloading 3072 4323893 NA 160", out var p));
        Assert.Equal(new YtDlpProgress("160", false, 3072, 4323893), p);

        // Fragmented downloads only estimate their size: kept apart from an exact one.
        Assert.True(YtDlpTool.TryParseProgress("OWNIDM-PROGRESS downloading 1024 NA 102801.66666666666 hls-720", out p));
        Assert.Equal((null, 102801L), (p.Total, p.Estimate));

        // A file finished in an earlier run reports no byte count.
        Assert.True(YtDlpTool.TryParseProgress("OWNIDM-PROGRESS finished NA 30767611 NA 258", out p));
        Assert.Equal(new YtDlpProgress("258", true, 30767611, 30767611), p);

        // Once finished, what arrived is the size.
        Assert.True(YtDlpTool.TryParseProgress("OWNIDM-PROGRESS finished 99000 NA 100000.5 hls-720", out p));
        Assert.Equal(99000, p.Total);

        Assert.False(YtDlpTool.TryParseProgress("[download] Destination: video.f160.mp4", out _));
        Assert.False(YtDlpTool.TryParseProgress("OWNIDM-PROGRESS downloading NA NA NA 1", out _));
    }

    [Fact]
    public void Browser_cookies_become_a_netscape_cookie_file_for_the_site()
    {
        var file = YtDlpTool.CookiesFile("SID=abc; PREF=f6=40000000&tz=Europe.Madrid; broken; =x",
            new Uri("https://www.youtube.com/watch?v=1"));

        var lines = file.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
        Assert.Equal(3, lines.Length);
        var fields = lines[2].Split('\t');
        Assert.Equal(".youtube.com", fields[0]);
        Assert.Equal(("PREF", "f6=40000000&tz=Europe.Madrid"), (fields[5], fields[6]));
    }

    [Fact]
    public void Error_message_drops_the_site_and_video_prefix()
    {
        string[] stderr =
        [
            "WARNING: [youtube] something minor",
            "ERROR: [youtube] aqz-KE-bpKQ: Sign in to confirm you're not a bot",
        ];
        Assert.Equal("Sign in to confirm you're not a bot", YtDlpTool.ErrorFrom(stderr));
        Assert.Equal("Unsupported URL: https://example.com/", YtDlpTool.ErrorFrom(["ERROR: Unsupported URL: https://example.com/"]));
        Assert.Null(YtDlpTool.ErrorFrom(["WARNING: x"]));
    }
}
