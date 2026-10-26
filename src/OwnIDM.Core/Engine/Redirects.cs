using System.Net;

namespace OwnIDM.Core.Engine;

/// <summary>
/// Follows HTTP redirects by hand so each hop gets a freshly built request. The browser's
/// cookies are only attached to the site they came from (never to another host, nor from
/// https to http), so a redirect or a playlist pointing elsewhere cannot leak a session.
/// </summary>
public static class Redirects
{
    private const int MaxHops = 10;

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri url, Func<Uri, HttpRequestMessage> build,
        HttpCompletionOption option, CancellationToken ct)
    {
        for (int hop = 0; ; hop++)
        {
            var resp = await http.SendAsync(build(url), option, ct).ConfigureAwait(false);
            if (!IsRedirect(resp.StatusCode) || resp.Headers.Location is not { } location)
                return resp;
            resp.Dispose();
            if (hop >= MaxHops)
                throw new HttpRequestException("Demasiadas redirecciones.");
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
    }

    /// <summary>True when cookies captured for <paramref name="cookieOrigin"/> may be sent to <paramref name="target"/>.</summary>
    public static bool CookiesAllowed(Uri target, Uri cookieOrigin)
    {
        if (cookieOrigin.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
            return false;
        return target.Host.Equals(cookieOrigin.Host, StringComparison.OrdinalIgnoreCase) ||
               target.Host.EndsWith("." + cookieOrigin.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode code) => (int)code is 301 or 302 or 303 or 307 or 308;
}
