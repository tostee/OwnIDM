using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using OwnIDM.Core.Hls;

namespace OwnIDM.Core.Dash;

public enum DashContent { Video, Audio }

/// <summary>SegmentBase addressing: one file whose segments are listed by a 'sidx' box.</summary>
public sealed record DashIndex(Uri Url, long IndexOffset, long IndexLength, long? InitOffset, long? InitLength);

/// <summary>
/// One Representation. Segments are known from the manifest (<see cref="Playlist"/>) or, for
/// SegmentBase, after reading the file's index (<see cref="Index"/>).
/// </summary>
public sealed record DashRepresentation(
    string Id, DashContent Content, long Bandwidth, int? Width, int? Height, string? Codecs, string? MimeType,
    string? Language, bool Encrypted, bool IsMain, HlsMediaPlaylist? Playlist, DashIndex? Index)
{
    /// <summary>Identifies the representation inside its manifest: the manifest address plus #rep=id.</summary>
    public Uri Key { get; init; } = null!;

    public bool IsWebM => MimeType?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true;

    public HlsVariant ToVariant() =>
        new(Key, Bandwidth, Width is not null && Height is not null ? $"{Width}x{Height}" : null, Codecs);
}

public sealed class DashManifest
{
    /// <summary>Video representations (or muxed ones), best first.</summary>
    public List<DashRepresentation> Video { get; } = new();

    /// <summary>Audio-only representations, best first.</summary>
    public List<DashRepresentation> Audio { get; } = new();

    /// <summary>type="dynamic": a live stream.</summary>
    public bool IsLive { get; set; }

    /// <summary>Several Periods (ads, chapters with different encodings): not supported.</summary>
    public bool MultiplePeriods { get; set; }

    public double TotalSeconds { get; set; }

    /// <summary>The representation a download uses: the one chosen by <paramref name="key"/> or the best.</summary>
    public DashRepresentation? VideoFor(string? key) =>
        Video.FirstOrDefault(r => r.Key.ToString() == key) ?? Video.FirstOrDefault() ?? Audio.FirstOrDefault();

    /// <summary>The separate audio to join with <paramref name="video"/>, or null when it carries its own.</summary>
    public DashRepresentation? AudioFor(DashRepresentation video)
    {
        if (video.Content == DashContent.Audio || Audio.Count == 0)
            return null;
        // Prefer the main track and the video's language family; then the best bitrate.
        return Audio.OrderByDescending(a => a.IsMain)
                    .ThenByDescending(a => a.Language is not null && a.Language == video.Language)
                    .ThenByDescending(a => a.Bandwidth)
                    .First();
    }
}

/// <summary>Parser for ISO/IEC 23009-1 (MPEG-DASH) manifests, enough to download on-demand video.</summary>
public static partial class DashParser
{
    public static bool IsManifest(string text)
    {
        var start = text.AsSpan().TrimStart("﻿ \t\r\n");
        return start.StartsWith("<") && text.Contains("<MPD", StringComparison.Ordinal);
    }

    public static DashManifest Parse(string text, Uri manifestUrl)
    {
        XElement mpd;
        try
        {
            mpd = XDocument.Parse(text).Root!;
        }
        catch (XmlException ex)
        {
            throw new FormatException("La dirección no es un manifiesto DASH (.mpd) válido.", ex);
        }
        if (mpd.Name.LocalName != "MPD")
            throw new FormatException("La dirección no es un manifiesto DASH (.mpd).");

        var manifest = new DashManifest
        {
            IsLive = string.Equals((string?)mpd.Attribute("type"), "dynamic", StringComparison.OrdinalIgnoreCase),
        };
        var periods = Children(mpd, "Period").ToList();
        manifest.MultiplePeriods = periods.Count(p => Children(p, "AdaptationSet").Any()) > 1;
        var period = periods.FirstOrDefault(p => Children(p, "AdaptationSet").Any());
        if (period is null)
            return manifest;

        double total = Duration(mpd.Attribute("mediaPresentationDuration"));
        double periodSeconds = Duration(period.Attribute("duration"));
        if (periodSeconds <= 0)
            periodSeconds = Math.Max(0, total - Duration(period.Attribute("start")));
        manifest.TotalSeconds = total > 0 ? total : periodSeconds;

        var periodBase = ResolveBase(ResolveBase(manifestUrl, mpd), period);
        foreach (var set in Children(period, "AdaptationSet"))
        {
            var setBase = ResolveBase(periodBase, set);
            bool setProtected = Children(set, "ContentProtection").Any();
            bool isMain = Children(set, "Role").Any(r => (string?)r.Attribute("value") == "main");
            foreach (var rep in Children(set, "Representation"))
            {
                var content = ContentOf(set, rep);
                if (content is null)
                    continue; // subtitles, thumbnails
                var id = (string?)rep.Attribute("id") ?? "";
                var repBase = ResolveBase(setBase, rep);
                long.TryParse((string?)rep.Attribute("bandwidth"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bandwidth);
                var (playlist, index) = Segments(repBase, id, bandwidth, periodSeconds, [rep, set, period]);
                var entry = new DashRepresentation(
                    id, content.Value, bandwidth, Int(rep, set, "width"), Int(rep, set, "height"),
                    Attr("codecs", rep, set), Attr("mimeType", rep, set), Attr("lang", rep, set),
                    setProtected || Children(rep, "ContentProtection").Any(), isMain, playlist, index)
                {
                    Key = new UriBuilder(manifestUrl) { Fragment = "rep=" + Uri.EscapeDataString(id) }.Uri,
                };
                if (playlist is not null)
                    playlist.IsLive = manifest.IsLive;
                (content == DashContent.Video ? manifest.Video : manifest.Audio).Add(entry);
            }
        }
        manifest.Video.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
        manifest.Audio.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
        return manifest;
    }

    private static DashContent? ContentOf(XElement set, XElement rep)
    {
        var type = (string?)set.Attribute("contentType") ?? Attr("mimeType", rep, set) ?? "";
        if (type.StartsWith("video", StringComparison.OrdinalIgnoreCase))
            return DashContent.Video;
        if (type.StartsWith("audio", StringComparison.OrdinalIgnoreCase))
            return DashContent.Audio;
        // No type given: guess from the codecs.
        var codecs = Attr("codecs", rep, set) ?? "";
        if (Regex.IsMatch(codecs, "^(avc|hvc|hev|vp0?[89]|av01|dvh)", RegexOptions.IgnoreCase))
            return DashContent.Video;
        if (Regex.IsMatch(codecs, "^(mp4a|opus|vorbis|ac-3|ec-3|flac)", RegexOptions.IgnoreCase))
            return DashContent.Audio;
        return null;
    }

    /// <summary>Builds the segment list from SegmentTemplate, SegmentList or SegmentBase (inherited from outer levels).</summary>
    private static (HlsMediaPlaylist?, DashIndex?) Segments(Uri baseUrl, string id, long bandwidth, double periodSeconds, XElement[] levels)
    {
        var templates = levels.Select(l => Children(l, "SegmentTemplate").FirstOrDefault()).Where(t => t is not null).Cast<XElement>().ToArray();
        if (templates.Length > 0)
            return (FromTemplate(baseUrl, id, bandwidth, periodSeconds, templates), null);

        var lists = levels.Select(l => Children(l, "SegmentList").FirstOrDefault()).Where(t => t is not null).Cast<XElement>().ToArray();
        if (lists.Length > 0)
            return (FromList(baseUrl, periodSeconds, lists), null);

        var segBase = levels.Select(l => Children(l, "SegmentBase").FirstOrDefault()).FirstOrDefault(t => t is not null);
        if (segBase?.Attribute("indexRange") is { } indexRange)
        {
            var (indexOffset, indexLength) = Range((string)indexRange);
            var init = Children(segBase, "Initialization").FirstOrDefault()?.Attribute("range");
            long? initOffset = null, initLength = null;
            if (init is not null)
                (initOffset, initLength) = Range((string)init);
            return (null, new DashIndex(baseUrl, indexOffset, indexLength, initOffset, initLength));
        }

        // A plain file with no segment information: download it whole.
        var whole = new HlsMediaPlaylist { IsLive = false };
        whole.Segments.Add(new HlsSegment(baseUrl, periodSeconds));
        return (whole, null);
    }

    private static HlsMediaPlaylist FromTemplate(Uri baseUrl, string id, long bandwidth, double periodSeconds, XElement[] templates)
    {
        string? A(string name) => templates.Select(t => (string?)t.Attribute(name)).FirstOrDefault(v => v is not null);
        long timescale = Math.Max(1, Long(A("timescale")) ?? 1);
        long startNumber = Long(A("startNumber")) ?? 1;
        long pto = Long(A("presentationTimeOffset")) ?? 0;
        var media = A("media") ?? throw new FormatException("SegmentTemplate sin atributo media.");
        Uri Url(string template, long number, long time) => new(baseUrl, Expand(template, id, bandwidth, number, time));

        var playlist = new HlsMediaPlaylist { IsLive = false };
        if (A("initialization") is { } init)
            playlist.Init = new HlsSegment(Url(init, startNumber, 0), 0);

        var timeline = templates.Select(t => Children(t, "SegmentTimeline").FirstOrDefault()).FirstOrDefault(t => t is not null);
        if (timeline is not null)
        {
            long end = pto + (long)Math.Round(periodSeconds * timescale);
            long time = 0, number = startNumber;
            foreach (var s in Children(timeline, "S"))
            {
                if (Long((string?)s.Attribute("t")) is { } t)
                    time = t;
                long d = Long((string?)s.Attribute("d")) ?? throw new FormatException("SegmentTimeline sin duración.");
                if (d <= 0)
                    throw new FormatException("SegmentTimeline con duración no válida.");
                long r = Long((string?)s.Attribute("r")) ?? 0;
                if (r < 0) // repeat until the next S or the end of the period
                {
                    long until = (long?)s.ElementsAfterSelf().FirstOrDefault(e => e.Name.LocalName == "S")?.Attribute("t") ?? end;
                    r = Math.Max(0, (until - time + d - 1) / d - 1);
                }
                for (long i = 0; i <= r; i++)
                {
                    playlist.Segments.Add(new HlsSegment(Url(media, number, time), d / (double)timescale));
                    time += d;
                    number++;
                }
            }
            return playlist;
        }

        long duration = Long(A("duration")) ?? throw new FormatException("SegmentTemplate sin duración ni SegmentTimeline.");
        if (duration <= 0 || periodSeconds <= 0)
            return playlist; // live, or no total duration: the segment count is unknown
        long count = (long)Math.Ceiling(periodSeconds * timescale / duration - 1e-9);
        for (long i = 0; i < count; i++)
        {
            double seconds = Math.Min(duration / (double)timescale, periodSeconds - i * duration / (double)timescale);
            playlist.Segments.Add(new HlsSegment(Url(media, startNumber + i, pto + i * duration), seconds));
        }
        return playlist;
    }

    private static HlsMediaPlaylist FromList(Uri baseUrl, double periodSeconds, XElement[] lists)
    {
        string? A(string name) => lists.Select(t => (string?)t.Attribute(name)).FirstOrDefault(v => v is not null);
        long timescale = Math.Max(1, Long(A("timescale")) ?? 1);
        double seconds = (Long(A("duration")) ?? 0) / (double)timescale;
        var playlist = new HlsMediaPlaylist { IsLive = false };

        var init = lists.Select(l => Children(l, "Initialization").FirstOrDefault()).FirstOrDefault(i => i is not null);
        if (init is not null)
            playlist.Init = Piece(baseUrl, (string?)init.Attribute("sourceURL"), (string?)init.Attribute("range"), 0);

        var urls = lists.Select(l => Children(l, "SegmentURL").ToList()).FirstOrDefault(u => u.Count > 0) ?? [];
        if (seconds <= 0 && urls.Count > 0)
            seconds = periodSeconds / urls.Count;
        foreach (var u in urls)
            playlist.Segments.Add(Piece(baseUrl, (string?)u.Attribute("media"), (string?)u.Attribute("mediaRange"), seconds));
        return playlist;
    }

    private static HlsSegment Piece(Uri baseUrl, string? url, string? range, double seconds)
    {
        var uri = string.IsNullOrEmpty(url) ? baseUrl : new Uri(baseUrl, url);
        if (range is null)
            return new HlsSegment(uri, seconds);
        var (offset, length) = Range(range);
        return new HlsSegment(uri, seconds, length, offset);
    }

    /// <summary>Expands $RepresentationID$, $Number$, $Time$, $Bandwidth$ (with optional %0Nd) and $$.</summary>
    public static string Expand(string template, string id, long bandwidth, long number, long time) =>
        TemplateId().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (name.Length == 0)
                return "$";
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            string Num(long v) => format is null ? v.ToString(CultureInfo.InvariantCulture)
                : v.ToString(CultureInfo.InvariantCulture).PadLeft(int.Parse(format, CultureInfo.InvariantCulture), '0');
            return name switch
            {
                "RepresentationID" => id,
                "Number" => Num(number),
                "Time" => Num(time),
                "Bandwidth" => Num(bandwidth),
                _ => m.Value,
            };
        });

    [GeneratedRegex(@"\$(RepresentationID|Number|Time|Bandwidth|)(?:%0?(\d+)d)?\$")]
    private static partial Regex TemplateId();

    /// <summary>
    /// Segments of a SegmentBase file from its 'sidx' box (<paramref name="index"/> holds the bytes of
    /// <see cref="DashIndex.IndexOffset"/>.. of the file). The init section precedes the index.
    /// </summary>
    public static HlsMediaPlaylist FromSidx(DashIndex info, ReadOnlySpan<byte> index)
    {
        int pos = 0;
        while (pos + 8 <= index.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(index[pos..]);
            var type = Encoding.ASCII.GetString(index.Slice(pos + 4, 4));
            int header = 8;
            if (size == 1 && pos + 16 <= index.Length)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(index[(pos + 8)..]);
                header = 16;
            }
            if (size < header || pos + size > index.Length)
                break;
            if (type == "sidx")
                return ParseSidx(info, index.Slice(pos + header, (int)size - header), info.IndexOffset + pos + size);
            pos += (int)size;
        }
        throw new FormatException("El vídeo DASH no tiene un índice de segmentos (sidx) legible.");
    }

    private static HlsMediaPlaylist ParseSidx(DashIndex info, ReadOnlySpan<byte> box, long boxEnd)
    {
        int version = box[0];
        uint timescale = BinaryPrimitives.ReadUInt32BigEndian(box[8..]);
        int p = 12;
        long firstOffset;
        if (version == 0)
        {
            firstOffset = BinaryPrimitives.ReadUInt32BigEndian(box[(p + 4)..]);
            p += 8;
        }
        else
        {
            firstOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(box[(p + 8)..]);
            p += 16;
        }
        int count = BinaryPrimitives.ReadUInt16BigEndian(box[(p + 2)..]);
        p += 4;

        var playlist = new HlsMediaPlaylist { IsLive = false };
        long initOffset = info.InitOffset ?? 0;
        long initLength = info.InitLength ?? info.IndexOffset - initOffset;
        if (initLength > 0)
            playlist.Init = new HlsSegment(info.Url, 0, initLength, initOffset);

        long offset = boxEnd + firstOffset;
        for (int i = 0; i < count; i++, p += 12)
        {
            uint reference = BinaryPrimitives.ReadUInt32BigEndian(box[p..]);
            if ((reference & 0x8000_0000) != 0)
                throw new NotSupportedException("El índice del vídeo DASH es jerárquico; no está soportado.");
            long length = reference & 0x7FFF_FFFF;
            uint duration = BinaryPrimitives.ReadUInt32BigEndian(box[(p + 4)..]);
            playlist.Segments.Add(new HlsSegment(info.Url, timescale == 0 ? 0 : duration / (double)timescale, length, offset));
            offset += length;
        }
        return playlist;
    }

    private static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(c => c.Name.LocalName == name);

    private static Uri ResolveBase(Uri parent, XElement e)
    {
        var baseUrl = Children(e, "BaseURL").Select(b => b.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        return baseUrl is null ? parent : new Uri(parent, baseUrl);
    }

    private static string? Attr(string name, params XElement[] levels) =>
        levels.Select(l => (string?)l.Attribute(name)).FirstOrDefault(v => v is not null);

    private static int? Int(XElement rep, XElement set, string name) =>
        int.TryParse(Attr(name, rep, set), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? Long(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>xs:duration (PT1H2M3.5S) in seconds; 0 when absent or malformed.</summary>
    private static double Duration(XAttribute? value)
    {
        if (value is null)
            return 0;
        try
        {
            return XmlConvert.ToTimeSpan(value.Value.Trim()).TotalSeconds;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    /// <summary>"first-last" byte range to (offset, length).</summary>
    private static (long Offset, long Length) Range(string value)
    {
        var parts = value.Split('-');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var first) ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var last) || last < first)
            throw new FormatException($"Rango de bytes no válido en el manifiesto DASH: {value}");
        return (first, last - first + 1);
    }
}
