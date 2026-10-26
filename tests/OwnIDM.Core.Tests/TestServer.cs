using System.Buffers.Binary;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OwnIDM.Core.Tests;

/// <summary>Local HTTP/1.1 server exposing the same random payload in several flavours.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _rangeRequests;
    private int _flakyRequests;
    private int _dropRequests;

    private TestServer(WebApplication app, byte[] data)
    {
        _app = app;
        Data = data;
    }

    public byte[] Data { get; }
    public string BaseUrl { get; private set; } = "";
    public int RangeRequests => _rangeRequests;

    public static async Task<TestServer> StartAsync(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var server = new TestServer(app, data);

        app.MapGet("/file.bin", (HttpContext ctx) =>
        {
            if (ctx.Request.Headers.Range.Count > 0)
                Interlocked.Increment(ref server._rangeRequests);
            return Results.File(data, "application/octet-stream", enableRangeProcessing: true);
        });
        app.MapGet("/attachment", () =>
            Results.File(data, "application/zip", fileDownloadName: "nombre real.zip", enableRangeProcessing: true));
        app.MapGet("/norange.bin", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = data.Length;
            await ctx.Response.Body.WriteAsync(data);
        });
        app.MapGet("/chunked.bin", async (HttpContext ctx) =>
        {
            for (int i = 0; i < data.Length; i += 8192)
            {
                await ctx.Response.Body.WriteAsync(data.AsMemory(i, Math.Min(8192, data.Length - i)));
                await ctx.Response.Body.FlushAsync();
            }
        });
        app.MapGet("/empty.bin", (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = 0;
            return Task.CompletedTask;
        });
        app.MapGet("/missing.bin", () => Results.NotFound());
        app.MapGet("/tiny.bin", () => Results.File(data.AsSpan(0, 500).ToArray(), "application/octet-stream", enableRangeProcessing: true));
        app.MapGet("/empty416.bin", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            ctx.Response.Headers.ContentRange = "bytes */0";
            return Task.CompletedTask;
        });
        // Two 503s, then the real file.
        app.MapGet("/flaky.bin", (HttpContext ctx) =>
            Interlocked.Increment(ref server._flakyRequests) <= 2
                ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                : Results.File(data, "application/octet-stream", enableRangeProcessing: true));
        // No ranges; the first response is cut halfway through.
        app.MapGet("/drop.bin", async (HttpContext ctx) =>
        {
            ctx.Response.ContentLength = data.Length;
            if (Interlocked.Increment(ref server._dropRequests) == 1)
            {
                await ctx.Response.Body.WriteAsync(data.AsMemory(0, data.Length / 2));
                await ctx.Response.Body.FlushAsync();
                ctx.Abort();
                return;
            }
            await ctx.Response.Body.WriteAsync(data);
        });

        MapHls(app, data);
        MapDash(app, data);
        MapCookies(app);
        MapSite(app, data);

        await app.StartAsync();
        server.BaseUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return server;
    }

    public const int HlsSegments = 10;

    public static readonly byte[] HlsInit = "ftyp-moov-init-section"u8.ToArray();

    /// <summary>The payload cut into <see cref="HlsSegments"/> MPEG-TS-like segments.</summary>
    private static void MapHls(WebApplication app, byte[] data)
    {
        int segSize = data.Length / HlsSegments;
        byte[] Segment(int i) => data.AsSpan(i * segSize, i == HlsSegments - 1 ? data.Length - i * segSize : segSize).ToArray();
        string Media(string extra) =>
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n" + extra +
            string.Concat(Enumerable.Range(0, HlsSegments).Select(i => $"#EXTINF:4.0,\nseg{i}.ts\n")) + "#EXT-X-ENDLIST\n";

        app.MapGet("/hls/master.m3u8", () => Results.Text(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\"\nlow.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720\nmedia.m3u8\n", "application/vnd.apple.mpegurl"));
        app.MapGet("/hls/media.m3u8", () => Results.Text(Media(""), "application/vnd.apple.mpegurl"));
        app.MapGet("/hls/low.m3u8", () => Results.Text("#EXTM3U\n#EXTINF:4,\nmissing.ts\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl"));
        app.MapGet("/hls/encrypted.m3u8", () => Results.Text(
            Media("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n"), "application/vnd.apple.mpegurl"));
        app.MapGet("/hls/seg{i:int}.ts", (int i) => i is >= 0 and < HlsSegments
            ? Results.File(Segment(i), "video/mp2t") : Results.NotFound());
        // fMP4 flavour: an init section (EXT-X-MAP) plus the same segments.
        app.MapGet("/hls/fmp4.m3u8", () => Results.Text(Media("#EXT-X-MAP:URI=\"init.mp4\"\n"), "application/vnd.apple.mpegurl"));
        app.MapGet("/hls/init.mp4", () => Results.File(HlsInit, "video/mp4"));
    }

    /// <summary>
    /// The payload as DASH: per-segment files from a SegmentTemplate (only the "hd" representation
    /// exists, so picking another quality fails) and one indexed file (SegmentBase + sidx).
    /// </summary>
    private static void MapDash(WebApplication app, byte[] data)
    {
        int segSize = data.Length / HlsSegments;
        int SegLength(int i) => i == HlsSegments - 1 ? data.Length - i * segSize : segSize;
        string Mpd(string protection) =>
            "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT40S\">\n" +
            "<Period><AdaptationSet contentType=\"video\" mimeType=\"video/mp4\">" + protection +
            "<SegmentTemplate timescale=\"1000\" duration=\"4000\" initialization=\"$RepresentationID$/init.mp4\" media=\"$RepresentationID$/seg-$Number%03d$.m4s\"/>" +
            "<Representation id=\"hd\" bandwidth=\"2500000\" width=\"1280\" height=\"720\"/>" +
            "<Representation id=\"sd\" bandwidth=\"800000\" width=\"640\" height=\"360\"/>" +
            "</AdaptationSet></Period></MPD>\n";

        app.MapGet("/dash/template.mpd", () => Results.Text(Mpd(""), "application/dash+xml"));
        app.MapGet("/dash/protected.mpd", () => Results.Text(
            Mpd("<ContentProtection schemeIdUri=\"urn:mpeg:dash:mp4protection:2011\" value=\"cenc\"/>"), "application/dash+xml"));
        app.MapGet("/dash/hd/init.mp4", () => Results.File(HlsInit, "video/mp4"));
        app.MapGet("/dash/hd/seg-{n:int}.m4s", (int n) => n is >= 1 and <= HlsSegments
            ? Results.File(data.AsSpan((n - 1) * segSize, SegLength(n - 1)).ToArray(), "video/iso.segment") : Results.NotFound());

        var sidx = Sidx(Enumerable.Range(0, HlsSegments).Select(SegLength).ToArray());
        var single = HlsInit.Concat(sidx).Concat(data).ToArray();
        app.MapGet("/dash/indexed.mpd", () => Results.Text(
            "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT40S\">" +
            "<Period><AdaptationSet mimeType=\"video/mp4\"><Representation id=\"1\" bandwidth=\"1000000\" width=\"1280\" height=\"720\">" +
            "<BaseURL>media/single.mp4</BaseURL>" +
            $"<SegmentBase indexRange=\"{HlsInit.Length}-{HlsInit.Length + sidx.Length - 1}\"><Initialization range=\"0-{HlsInit.Length - 1}\"/></SegmentBase>" +
            "</Representation></AdaptationSet></Period></MPD>", "application/dash+xml"));
        app.MapGet("/dash/media/single.mp4", () => Results.File(single, "video/mp4", enableRangeProcessing: true));
    }

    /// <summary>A version 0 'sidx' box listing media segments of the given sizes, 4 s each.</summary>
    public static byte[] Sidx(int[] sizes)
    {
        var box = new byte[32 + 12 * sizes.Length];
        var s = box.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)box.Length);
        "sidx"u8.CopyTo(s[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 1);    // reference_ID
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], 1000); // timescale
        BinaryPrimitives.WriteUInt16BigEndian(s[30..], (ushort)sizes.Length);
        for (int i = 0; i < sizes.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s[(32 + 12 * i)..], (uint)sizes[i]);
            BinaryPrimitives.WriteUInt32BigEndian(s[(36 + 12 * i)..], 4000);
        }
        return box;
    }

    /// <summary>Echoes the Cookie header, directly or after a redirect to another host name.</summary>
    private static void MapCookies(WebApplication app)
    {
        app.MapGet("/echo-cookie", (HttpContext ctx) => Results.Text(ctx.Request.Headers.Cookie.ToString()));
        app.MapGet("/redirect-same", () => Results.Redirect("/echo-cookie"));
        app.MapGet("/redirect-other", (HttpContext ctx) =>
            Results.Redirect($"http://localhost:{ctx.Connection.LocalPort}/echo-cookie"));
    }

    /// <summary>A small web site for the grabber.</summary>
    private static void MapSite(WebApplication app, byte[] data)
    {
        static IResult Html(string body) => Results.Content("<html><body>" + body + "</body></html>", "text/html");
        var small = data.AsSpan(0, 1000).ToArray();

        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /site/private/\n"));
        app.MapGet("/site/index.html", () => Html(
            "<a href='page2.html'>2</a> <a href=\"files/a.zip\">a</a> <a href=b.pdf>b</a>" +
            "<a href='private/secret.zip'>s</a> <a href='download.php?id=1'>dyn</a>" +
            "<a href='http://ext.invalid/ext.zip'>ext</a> <a href='#top'>top</a> <a href='mailto:x@y.z'>m</a>"));
        app.MapGet("/site/page2.html", () => Html(
            "<a href='../outside.html'>up</a> <a href='deep/page3.html'>deep</a> <a href='c.zip'>c</a>" +
            "<a href='files/a.zip'>again</a> <a href='mirror/a.zip'>mirror</a>"));
        app.MapGet("/site/deep/page3.html", () => Html("<a href='d.zip'>d</a>"));
        app.MapGet("/outside.html", () => Html("<a href='outside.zip'>o</a>"));
        app.MapGet("/site/download.php", () => Results.File(small, "application/zip", "dyn.zip"));
        foreach (var path in new[] { "/site/files/a.zip", "/site/mirror/a.zip", "/site/c.zip", "/site/deep/d.zip",
                                     "/site/private/secret.zip", "/outside.zip", "/site/b.pdf" })
            app.MapGet(path, () => Results.File(small, "application/octet-stream", enableRangeProcessing: true));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
