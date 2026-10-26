using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using OwnIDM.Core.Engine;

namespace OwnIDM.Core.Grabber;

public sealed class GrabberOptions
{
    public string StartUrl { get; set; } = "";

    /// <summary>Link levels to follow inside the start site (0 = only the start page).</summary>
    public int Depth { get; set; } = 1;

    /// <summary>Link levels to follow on other sites (0 = do not explore them).</summary>
    public int ExternalDepth { get; set; }

    /// <summary>Do not explore pages above the start page's folder.</summary>
    public bool StayBelowStartPath { get; set; } = true;

    public bool RespectRobots { get; set; } = true;
    public int MaxPages { get; set; } = 300;

    /// <summary>Pages explored at the same time (1-10).</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>Wildcards or bare extensions separated by spaces/commas, e.g. "*.zip pdf". "*" = any non-page file.</summary>
    public string FileMasks { get; set; } = "zip rar 7z";

    /// <summary>Ignore matching files hosted on other sites.</summary>
    public bool FilesFromStartSiteOnly { get; set; }

    /// <summary>Hide files with the same name and size found at different addresses.</summary>
    public bool HideDuplicates { get; set; } = true;

    public string? UserAgent { get; set; }
}

public sealed record GrabbedFile(string Url, string FileName, long Size, string? ContentType, string PageUrl);

public sealed record GrabberProgress(int PagesExplored, int Pending, int FilesFound, string? CurrentUrl);

public sealed record GrabberResult(int PagesExplored, IReadOnlyList<GrabbedFile> Files, bool HitPageLimit);

/// <summary>
/// Explores a web site breadth-first and collects links to files that match the masks,
/// like a download manager's site grabber. Pages are parsed as text; scripts are not run.
/// </summary>
public sealed class SiteGrabber
{
    private static readonly Regex LinkPattern = new(
        @"(?:href|src|data-src)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BasePattern = new(
        @"<base\s[^>]*href\s*=\s*[""']?([^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> PageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "", ".html", ".htm", ".xhtml", ".shtml", ".php", ".asp", ".aspx", ".jsp", ".cfm", ".cgi", ".pl" };

    /// <summary>Resources never worth fetching when they do not match the masks.</summary>
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".css", ".js", ".mjs", ".json", ".xml", ".ico", ".woff", ".woff2", ".ttf", ".eot", ".otf", ".map" };

    private const int MaxPageChars = 5 * 1024 * 1024;

    private readonly HttpMessageHandler? _handler;

    public SiteGrabber(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>Raised (from worker threads) for every matching file found.</summary>
    public event Action<GrabbedFile>? FileFound;

    public async Task<GrabberResult> RunAsync(GrabberOptions o, IProgress<GrabberProgress>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(o.StartUrl.Trim(), UriKind.Absolute, out var start) || (start.Scheme != "http" && start.Scheme != "https"))
            throw new FormatException("Introduce una dirección inicial http:// o https:// válida.");

        using var http = new HttpClient(_handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            MaxConnectionsPerServer = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }, disposeHandler: _handler is null) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            string.IsNullOrWhiteSpace(o.UserAgent) ? DownloadEngine.DefaultUserAgent : o.UserAgent);

        var masks = FileMask.Parse(o.FileMasks);
        var robots = new RobotsCache(http);
        var startDir = start.AbsolutePath[..(start.AbsolutePath.LastIndexOf('/') + 1)];
        var visited = new ConcurrentDictionary<string, byte>();
        var files = new ConcurrentDictionary<string, GrabbedFile>();
        var fileKeys = new ConcurrentDictionary<string, byte>();
        int pagesExplored = 0;
        bool hitLimit = false;

        var frontier = new List<Link> { new(start, 0, 0, start.ToString()) };
        visited.TryAdd(Normalize(start), 0);

        while (frontier.Count > 0 && !ct.IsCancellationRequested)
        {
            var next = new ConcurrentBag<Link>();
            int pending = frontier.Count;
            await Parallel.ForEachAsync(frontier,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(o.Parallel, 1, 10), CancellationToken = ct },
                async (link, token) =>
                {
                    try
                    {
                        await VisitAsync(link, next, token).ConfigureAwait(false);
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // Broken link, slow page or odd markup: skip it and keep exploring.
                    }
                    progress?.Report(new GrabberProgress(pagesExplored, Interlocked.Decrement(ref pending), files.Count, link.Url.ToString()));
                }).ConfigureAwait(false);
            frontier = next.ToList();
        }

        ct.ThrowIfCancellationRequested();
        progress?.Report(new GrabberProgress(pagesExplored, 0, files.Count, null));
        return new GrabberResult(pagesExplored, files.Values.OrderBy(f => f.Url, StringComparer.Ordinal).ToList(), hitLimit);

        async Task VisitAsync(Link link, ConcurrentBag<Link> next, CancellationToken token)
        {
            // Cheap checks first: excluded hosts are never contacted, not even for robots.txt.
            bool sameSite = SameSite(link.Url, start);
            bool filesAllowed = sameSite || !o.FilesFromStartSiteOnly;
            var name = FileNameHelper.FromUrl(link.Url.ToString());
            if (masks.Matches(name, IsPageLike(link.Url)))
            {
                if (filesAllowed && await RobotsAllowAsync(link.Url, token).ConfigureAwait(false))
                    await ProbeFileAsync(link, token, mustBeFile: false).ConfigureAwait(false);
                return;
            }
            if (SkipExtensions.Contains(Path.GetExtension(link.Url.AbsolutePath)))
                return;
            if (!MayExplore(link, sameSite))
            {
                // A dynamic link beyond the exploration limits may still be a file (download.php?id=3).
                if (filesAllowed && !string.IsNullOrEmpty(link.Url.Query) && IsPageLike(link.Url) &&
                    await RobotsAllowAsync(link.Url, token).ConfigureAwait(false))
                    await ProbeFileAsync(link, token, mustBeFile: true).ConfigureAwait(false);
                return;
            }
            if (!await RobotsAllowAsync(link.Url, token).ConfigureAwait(false))
                return;
            if (Interlocked.Increment(ref pagesExplored) > o.MaxPages)
            {
                Interlocked.Decrement(ref pagesExplored);
                hitLimit = true;
                return;
            }

            using var resp = await http.GetAsync(link.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                // A dynamic link (download.php?id=3) that turned out to be a file.
                var fileName = FileNameHelper.FromResponse(resp, link.Url.ToString());
                if (masks.Matches(fileName, isPageLike: false) && (sameSite || !o.FilesFromStartSiteOnly))
                    AddFile(new GrabbedFile(resp.RequestMessage?.RequestUri?.ToString() ?? link.Url.ToString(), fileName,
                        resp.Content.Headers.ContentLength ?? -1, mediaType, link.Referrer));
                return;
            }

            var html = await ReadLimitedAsync(resp, token).ConfigureAwait(false);
            if (html is null)
                return; // too slow
            var pageUri = resp.RequestMessage?.RequestUri ?? link.Url;
            if (BasePattern.Match(html) is { Success: true } b && Uri.TryCreate(pageUri, WebUtility.HtmlDecode(b.Groups[1].Value), out var baseUri))
                pageUri = baseUri;

            foreach (Match m in LinkPattern.Matches(html))
            {
                var raw = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
                if (raw.Length == 0 || raw.StartsWith('#') || !Uri.TryCreate(pageUri, raw, out var target) ||
                    (target.Scheme != "http" && target.Scheme != "https"))
                    continue;
                target = new UriBuilder(target) { Fragment = "" }.Uri;
                if (!visited.TryAdd(Normalize(target), 0))
                    continue;
                bool targetSameSite = SameSite(target, start);
                next.Add(new Link(target, targetSameSite ? link.Depth + 1 : link.Depth,
                    targetSameSite ? 0 : link.ExternalDepth + 1, pageUri.ToString()));
            }
        }

        Task<bool> RobotsAllowAsync(Uri url, CancellationToken token) =>
            o.RespectRobots ? robots.AllowedAsync(url, token) : Task.FromResult(true);

        bool MayExplore(Link link, bool sameSite)
        {
            if (link.Depth == 0 && link.ExternalDepth == 0)
                return true; // start page
            if (!sameSite)
                return link.ExternalDepth <= o.ExternalDepth;
            if (link.Depth > o.Depth)
                return false;
            return !o.StayBelowStartPath || link.Url.AbsolutePath.StartsWith(startDir, StringComparison.Ordinal);
        }

        // mustBeFile: only keep it if the server answers with a non-HTML body matching the masks.
        async Task ProbeFileAsync(Link link, CancellationToken token, bool mustBeFile)
        {
            long size = -1;
            string? type = null;
            string fileName = FileNameHelper.Sanitize(FileNameHelper.FromUrl(link.Url.ToString()));
            var finalUrl = link.Url.ToString();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, link.Url);
                req.Headers.Range = new RangeHeaderValue(0, 0);
                req.Headers.Referrer = Uri.TryCreate(link.Referrer, UriKind.Absolute, out var r) ? r : null;
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    size = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? -1;
                    type = resp.Content.Headers.ContentType?.MediaType;
                    fileName = FileNameHelper.FromResponse(resp, link.Url.ToString());
                    finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? finalUrl;
                    if (mustBeFile && ((type ?? "").Contains("html", StringComparison.OrdinalIgnoreCase) ||
                                       !masks.Matches(fileName, isPageLike: false)))
                        return;
                }
                else if (resp.StatusCode == HttpStatusCode.NotFound || mustBeFile)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!mustBeFile)
            {
                // Keep the link with unknown size; the download itself will report the problem.
            }
            AddFile(new GrabbedFile(finalUrl, fileName, size, type, link.Referrer));
        }

        void AddFile(GrabbedFile file)
        {
            if (o.HideDuplicates && file.Size >= 0 && !fileKeys.TryAdd($"{file.FileName.ToLowerInvariant()}|{file.Size}", 0))
                return;
            if (files.TryAdd(file.Url, file))
                FileFound?.Invoke(file);
        }
    }

    private sealed record Link(Uri Url, int Depth, int ExternalDepth, string Referrer);

    private static bool SameSite(Uri a, Uri b) =>
        string.Equals(StripWww(a.Host), StripWww(b.Host), StringComparison.OrdinalIgnoreCase);

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string Normalize(Uri u) => u.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    private static bool IsPageLike(Uri u) => PageExtensions.Contains(Path.GetExtension(u.AbsolutePath));

    /// <summary>Reads up to 5 M characters; null if the server stalls for 30 s between chunks.</summary>
    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            stall.CancelAfter(TimeSpan.FromSeconds(30));
            await using var stream = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var page = new System.Text.StringBuilder();
            var buffer = new char[16 * 1024];
            int n;
            while (page.Length < MaxPageChars &&
                   (n = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxPageChars - page.Length)), stall.Token)
                       .ConfigureAwait(false)) > 0)
            {
                page.Append(buffer, 0, n);
                stall.CancelAfter(TimeSpan.FromSeconds(30));
            }
            return page.ToString();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>File name masks: "*.zip", "img??.jpg", bare "pdf" (= *.pdf) or "*" (any non-page file).</summary>
public sealed class FileMask
{
    private readonly List<Regex> _patterns = new();
    private bool _any;

    public static FileMask Parse(string masks)
    {
        var result = new FileMask();
        foreach (var token in masks.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token is "*" or "*.*")
            {
                result._any = true;
                continue;
            }
            var mask = token.Contains('*') || token.Contains('?') ? token : "*." + token.TrimStart('.');
            result._patterns.Add(new Regex("^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase));
        }
        return result;
    }

    public bool Matches(string fileName, bool isPageLike) =>
        fileName.Length > 0 && ((_any && !isPageLike) || _patterns.Any(p => p.IsMatch(fileName)));
}

/// <summary>robots.txt rules for user-agent "*" (or OwnIDM), cached per host.</summary>
internal sealed class RobotsCache(HttpClient http)
{
    private readonly ConcurrentDictionary<string, Task<List<(bool Allow, Regex Pattern, int Length)>>> _rules = new();

    public async Task<bool> AllowedAsync(Uri url, CancellationToken ct)
    {
        var rules = await _rules.GetOrAdd(url.GetLeftPart(UriPartial.Authority), key => LoadAsync(key, ct)).ConfigureAwait(false);
        var path = url.PathAndQuery;
        var best = rules.Where(r => r.Pattern.IsMatch(path)).OrderByDescending(r => r.Length).ThenByDescending(r => r.Allow).FirstOrDefault();
        return best.Pattern is null || best.Allow;
    }

    private async Task<List<(bool, Regex, int)>> LoadAsync(string authority, CancellationToken ct)
    {
        var rules = new List<(bool, Regex, int)>();
        try
        {
            using var resp = await http.GetAsync(authority + "/robots.txt", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return rules;
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            bool applies = false, inAgents = false;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Split('#')[0].Trim();
                int colon = line.IndexOf(':');
                if (colon < 0)
                    continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (key == "user-agent")
                {
                    if (!inAgents)
                        applies = false; // a new group starts
                    inAgents = true;
                    applies |= value == "*" || value.Contains("OwnIDM", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                inAgents = false;
                if (!applies || (key != "allow" && key != "disallow") || value.Length == 0)
                    continue;
                var regex = "^" + Regex.Escape(value).Replace(@"\*", ".*");
                regex = regex.EndsWith(@"\$", StringComparison.Ordinal) ? regex[..^2] + "$" : regex;
                rules.Add((key == "allow", new Regex(regex), value.Length));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // No robots.txt reachable: everything allowed.
        }
        return rules;
    }
}
