using System.Globalization;

namespace OwnIDM.Core.Hls;

/// <summary>One quality level of a master playlist.</summary>
public sealed record HlsVariant(Uri Url, long Bandwidth, string? Resolution, string? Codecs, string? AudioGroup = null)
{
    public string Label
    {
        get
        {
            var mbps = (Bandwidth / 1_000_000.0).ToString("0.0", CultureInfo.CurrentCulture) + " Mbps";
            return Resolution is null ? mbps : $"{Resolution} · {mbps}";
        }
    }
}

public sealed record HlsSegment(Uri Url, double Duration, long? Length = null, long? Offset = null);

/// <summary>An #EXT-X-MEDIA audio rendition; a null Url means the audio is muxed into the video.</summary>
public sealed record HlsAudio(string GroupId, Uri? Url, bool IsDefault, string? Name);

public sealed class HlsMediaPlaylist
{
    public List<HlsSegment> Segments { get; } = new();

    /// <summary>fMP4 initialization section (EXT-X-MAP), written before the first segment.</summary>
    public HlsSegment? Init { get; set; }

    /// <summary>No #EXT-X-ENDLIST: a live stream that keeps growing.</summary>
    public bool IsLive { get; set; } = true;

    /// <summary>Segments use EXT-X-KEY encryption (AES-128, SAMPLE-AES, DRM).</summary>
    public bool Encrypted { get; set; }

    /// <summary>More than one EXT-X-MAP (init sections change mid-stream): not supported.</summary>
    public bool MultipleInits { get; set; }

    public double TotalSeconds => Segments.Sum(s => s.Duration);
}

/// <summary>Minimal parser for RFC 8216 playlists, enough to download VOD streams.</summary>
public static class HlsParser
{
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static List<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var variants = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = Attributes(line["#EXT-X-STREAM-INF:".Length..]);
            }
            else if (pending is not null && !line.StartsWith('#'))
            {
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bw);
                variants.Add(new HlsVariant(new Uri(baseUri, line), bw,
                    pending.GetValueOrDefault("RESOLUTION"), pending.GetValueOrDefault("CODECS"), pending.GetValueOrDefault("AUDIO")));
                pending = null;
            }
        }
        return variants.OrderByDescending(v => v.Bandwidth).ToList();
    }

    public static List<HlsAudio> ParseAudio(string text, Uri baseUri)
    {
        var result = new List<HlsAudio>();
        foreach (var line in Lines(text))
        {
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                continue;
            var a = Attributes(line["#EXT-X-MEDIA:".Length..]);
            if (!string.Equals(a.GetValueOrDefault("TYPE"), "AUDIO", StringComparison.OrdinalIgnoreCase) ||
                !a.TryGetValue("GROUP-ID", out var group))
                continue;
            result.Add(new HlsAudio(group, a.TryGetValue("URI", out var uri) ? new Uri(baseUri, uri) : null,
                string.Equals(a.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase), a.GetValueOrDefault("NAME")));
        }
        return result;
    }

    /// <summary>The separate audio playlist a variant needs, or null when its audio is muxed in.</summary>
    public static Uri? AudioFor(HlsVariant variant, IEnumerable<HlsAudio> audio)
    {
        if (variant.AudioGroup is null)
            return null;
        var group = audio.Where(a => a.GroupId == variant.AudioGroup).ToList();
        if (group.Count == 0)
            return null;
        // The default rendition without URI means its audio is already inside the video segments.
        return (group.FirstOrDefault(a => a.IsDefault) ?? group[0]).Url;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        var playlist = new HlsMediaPlaylist();
        double duration = 0;
        long? length = null, offset = null;
        var lastEnd = new Dictionary<Uri, long>(); // EXT-X-BYTERANGE without @offset continues the previous range

        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..].Split(',')[0];
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                (length, offset) = ByteRange(line["#EXT-X-BYTERANGE:".Length..]);
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var method = Attributes(line["#EXT-X-KEY:".Length..]).GetValueOrDefault("METHOD", "NONE");
                if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    playlist.Encrypted = true;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var attrs = Attributes(line["#EXT-X-MAP:".Length..]);
                if (attrs.TryGetValue("URI", out var uri))
                {
                    if (playlist.Init is not null)
                        playlist.MultipleInits = true;
                    var (len, off) = attrs.TryGetValue("BYTERANGE", out var br) ? ByteRange(br) : (null, null);
                    playlist.Init = new HlsSegment(new Uri(baseUri, uri), 0, len, len is null ? null : off ?? 0);
                }
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                playlist.IsLive = false;
            }
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:VOD", StringComparison.Ordinal))
            {
                playlist.IsLive = false;
            }
            else if (!line.StartsWith('#'))
            {
                var url = new Uri(baseUri, line);
                if (length is not null)
                {
                    offset ??= lastEnd.GetValueOrDefault(url);
                    lastEnd[url] = offset.Value + length.Value;
                }
                playlist.Segments.Add(new HlsSegment(url, duration, length, offset));
                duration = 0;
                length = offset = null;
            }
        }
        return playlist;
    }

    private static IEnumerable<string> Lines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new FormatException("La dirección no es una lista de reproducción HLS (.m3u8).");
        return lines.Skip(1);
    }

    private static (long? Length, long? Offset) ByteRange(string value)
    {
        var parts = value.Trim('"').Split('@');
        long length = long.Parse(parts[0], CultureInfo.InvariantCulture);
        long? offset = parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null;
        return (length, offset);
    }

    /// <summary>Parses KEY=value,KEY="quoted, value" attribute lists.</summary>
    public static Dictionary<string, string> Attributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < list.Length)
        {
            int eq = list.IndexOf('=', i);
            if (eq < 0)
                break;
            var key = list[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < list.Length && list[i] == '"')
            {
                int close = list.IndexOf('"', i + 1);
                if (close < 0)
                    close = list.Length;
                value = list[(i + 1)..close];
                i = Math.Min(list.Length, close + 1);
            }
            else
            {
                int comma = list.IndexOf(',', i);
                if (comma < 0)
                    comma = list.Length;
                value = list[i..comma].Trim();
                i = comma;
            }
            result[key] = value;
            if (i < list.Length && list[i] == ',')
                i++;
        }
        return result;
    }
}
