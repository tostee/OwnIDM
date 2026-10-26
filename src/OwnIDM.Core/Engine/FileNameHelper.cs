using System.Net.Http.Headers;

namespace OwnIDM.Core.Engine;

public static class FileNameHelper
{
    public static string FromResponse(HttpResponseMessage response, string fallbackUrl)
    {
        var name = FromContentDisposition(response.Content.Headers.ContentDisposition);
        if (string.IsNullOrWhiteSpace(name))
            name = FromUrl(response.RequestMessage?.RequestUri?.ToString() ?? fallbackUrl);
        if (string.IsNullOrWhiteSpace(name))
            name = FromUrl(fallbackUrl);
        return Sanitize(string.IsNullOrWhiteSpace(name) ? "descarga" : name);
    }

    public static string? FromContentDisposition(ContentDispositionHeaderValue? cd)
    {
        var raw = cd?.FileNameStar ?? cd?.FileName;
        return raw?.Trim().Trim('"');
    }

    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";
        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return Uri.UnescapeDataString(last);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 200)
            clean = clean[..200];
        return clean.Length == 0 ? "descarga" : clean;
    }

    /// <summary>Returns <paramref name="path"/> or "name (n).ext" if it already exists.</summary>
    public static string MakeUnique(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
