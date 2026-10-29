using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Fetches a link's preview card (title, site and image) the way the phone app does, since the phone sends none.
/// </summary>
/// <remarks>
/// <para>
/// Fetching a page the other side chose is a privacy and safety risk, so the rules are strict. Only HTTPS links to a
/// named public host are fetched (<see cref="IsAllowed"/>): no IP literals, <c>localhost</c> or <c>*.local</c>. The
/// handler from <see cref="CreateHandler"/> resolves the host itself and connects only to the address it vetted
/// (<see cref="IsPublic"/>), on every redirect hop too, so DNS rebinding can't reach the local network. No cookies,
/// credentials or proxy are used.
/// </para>
/// <para>
/// A page must be <c>text/html</c> within <see cref="MaxPageBytes"/> and an image <c>image/*</c> within
/// <see cref="MaxImageBytes"/>, each answered within <see cref="RequestTimeout"/>. Results, failures included, stay in
/// an in-memory list of the last 50 links and are never written to disk. A failure is logged by its error type only,
/// never the link, title or description.
/// </para>
/// </remarks>
public sealed partial class LinkPreviewer : IDisposable
{
    /// <summary>The largest page read, in bytes; a bigger one gives no preview.</summary>
    public const int MaxPageBytes = 262_144;

    /// <summary>The largest image read, in bytes; a bigger one gives a preview without an image.</summary>
    public const int MaxImageBytes = 1_048_576;

    /// <summary>How long each request (the page, then its image) may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    // How Many Links' Results Are Kept In Memory
    private const int CacheSize = 50;

    // Punctuation A Link Ends Before, Like A Browser's Or Windows' Link Detection
    private const string TrailingPunctuation = ".,;:!?)]}'\"";

    private readonly HttpClient _http;
    private readonly bool _allowLoopback;
    private readonly ILogger _log;

    // Canceled On Dispose, So Fetches Still Running Stop
    private readonly CancellationTokenSource _closing = new();

    // Least Recently Used First; Each Link's Fetch, Running Or Done (Null Results Included)
    private readonly Lock _cacheLock = new();
    private readonly OrderedDictionary<Uri, Task<LinkPreview?>> _cache = [];

    /// <summary>
    /// Creates the previewer.
    /// </summary>
    /// <param name="handler">Sends the requests; <see cref="CreateHandler"/> in the app, a fake in tests. Disposed with the previewer.</param>
    /// <param name="allowLoopback">True in UI test runs only, so <c>http://127.0.0.1</c> (the fake server) can be previewed.</param>
    /// <param name="log">Takes failures, by error type only.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public LinkPreviewer(HttpMessageHandler handler, bool allowLoopback, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);

        _http          = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _allowLoopback = allowLoopback;
        _log           = log;
    }

    // =========================================================================
    // SAFETY RULES
    // =========================================================================

    /// <summary>
    /// Creates the handler the app sends previews through.
    /// </summary>
    /// <remarks>
    /// Cookies and the proxy are off, redirects are followed at most 3 times (never from HTTPS to HTTP), and the
    /// <c>ConnectCallback</c> resolves each host itself and connects only when every address it resolves to passes
    /// <see cref="IsPublic"/> (or is loopback when <paramref name="allowLoopback"/>). It connects to those vetted
    /// addresses, so a second lookup can't swap in a private one. The proxy is off because through a proxy the check
    /// would vet the proxy, not the link's host.
    /// </remarks>
    /// <param name="allowLoopback">True in UI test runs only.</param>
    /// <returns>The handler.</returns>
    public static SocketsHttpHandler CreateHandler(bool allowLoopback) => new()
    {
        UseCookies               = false,
        UseProxy                 = false,
        PreAuthenticate          = false,
        AllowAutoRedirect        = true,
        MaxAutomaticRedirections = 3,
        AutomaticDecompression   = DecompressionMethods.All,
        ConnectTimeout           = RequestTimeout,
        ConnectCallback          = (context, ct) => ConnectAsync(context.DnsEndPoint, allowLoopback, ct),
    };

    /// <summary>
    /// Finds the first web link in a message, without the punctuation that ends a sentence around it.
    /// </summary>
    /// <remarks>
    /// The first <c>http://</c> or <c>https://</c> run of non-spaces, with trailing <c>.,;:!?)]}'"</c> trimmed. A closing
    /// bracket the link opened itself stays, so <c>https://en.wikipedia.org/wiki/Foo_(bar)</c> keeps its <c>)</c>.
    /// </remarks>
    /// <param name="text">The message text.</param>
    /// <returns>The link, or null when there is none.</returns>
    public static Uri? FirstLink(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (Match match in LinkPattern().Matches(text))
        {
            string link = TrimLink(match.Value);
            if (Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>
    /// Says whether a link may be fetched for a preview.
    /// </summary>
    /// <remarks>
    /// HTTPS only, to a named host with a dot in it: no IP literal, <c>localhost</c>, <c>*.local</c>, single-label
    /// intranet name, or user name and password in the link. With <paramref name="allowLoopback"/> (UI test runs), an
    /// <c>http</c> or <c>https</c> link to <c>127.0.0.1</c> is allowed too. Where the host really points is checked when
    /// connecting (<see cref="CreateHandler"/>).
    /// </remarks>
    /// <param name="url">The link.</param>
    /// <param name="allowLoopback">True in UI test runs only.</param>
    /// <returns>True when the link may be fetched.</returns>
    public static bool IsAllowed(Uri url, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || url.UserInfo.Length > 0)
        {
            return false;
        }

        // UI Test Runs: The Fake Server On 127.0.0.1
        if (allowLoopback && url.HostNameType == UriHostNameType.IPv4 && url.IsLoopback && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        if (url.Scheme != Uri.UriSchemeHttps || url.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        string host = url.IdnHost.TrimEnd('.');
        return host.Contains('.', StringComparison.Ordinal)
            && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Says whether an address is on the public internet.
    /// </summary>
    /// <remarks>
    /// False for loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10), CGNAT
    /// (100.64/10), unspecified (0/8, ::), multicast and reserved (224/4, 240/4, ff00::/8) and the old IPv6 site-local
    /// range. An IPv4 address written as IPv6 (<c>::ffff:10.0.0.1</c>) is judged as IPv4.
    /// </remarks>
    /// <param name="address">The address.</param>
    /// <returns>True when public.</returns>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && (b[1] & 0xC0) == 64)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return !(address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC);
        }

        return false;
    }

    // =========================================================================
    // PARSING
    // =========================================================================

    /// <summary>
    /// Reads the preview's parts from a page's head, with regular expressions only.
    /// </summary>
    /// <remarks>
    /// The title is <c>og:title</c>, else <c>&lt;title&gt;</c>; the site is <c>og:site_name</c>, else the host without
    /// <c>www.</c>. Attributes may come in any order and any quotes. Text has tags removed, entities decoded and spaces
    /// collapsed. The image is <c>og:image</c> resolved against <paramref name="page"/>, kept only when
    /// <see cref="IsAllowed"/>.
    /// </remarks>
    /// <param name="html">The page.</param>
    /// <param name="page">The page's address, for relative image links and the host.</param>
    /// <param name="allowLoopback">True in UI test runs only.</param>
    /// <returns>The parts, or null when the page has no title.</returns>
    internal static (string Title, string Site, Uri? Image)? ParseHead(string html, Uri page, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(page);

        // Only The Head Holds What A Card Needs
        int end     = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        string head = end >= 0 ? html[..end] : html;

        // Open Graph Tags: The First Of Each Wins
        string? og_title = null;
        string? og_site  = null;
        string? og_image = null;
        foreach (Match tag in MetaTag().Matches(head))
        {
            string? key     = null;
            string? content = null;
            foreach (Match attribute in Attribute().Matches(tag.Value))
            {
                string name  = attribute.Groups["name"].Value;
                string value = attribute.Groups["value"].Value;
                if (name.Equals("property", StringComparison.OrdinalIgnoreCase) || name.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    key = value.Trim();
                }
                else if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                {
                    content = value;
                }
            }

            if (key is null || content is null)
            {
                continue;
            }

            if (key.Equals("og:title", StringComparison.OrdinalIgnoreCase))
            {
                og_title ??= content;
            }
            else if (key.Equals("og:site_name", StringComparison.OrdinalIgnoreCase))
            {
                og_site ??= content;
            }
            else if (key.Equals("og:image", StringComparison.OrdinalIgnoreCase))
            {
                og_image ??= content;
            }
        }

        // Title: og:title, Else <title>
        string title = PlainText(og_title);
        if (title.Length == 0 && TitleTag().Match(head) is { Success: true } title_tag)
        {
            title = PlainText(title_tag.Groups["text"].Value);
        }

        if (title.Length == 0)
        {
            return null;
        }

        // Site: og:site_name, Else The Host Without www.
        string site = PlainText(og_site);
        if (site.Length == 0)
        {
            site = page.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? page.Host[4..] : page.Host;
        }

        // Image: Resolved Against The Page, Then Held To The Same Rules As A Link
        Uri? image = og_image is not null
            && Uri.TryCreate(page, WebUtility.HtmlDecode(og_image).Trim(), out Uri? resolved)
            && IsAllowed(resolved, allowLoopback)
                ? resolved
                : null;

        return (title, site, image);
    }

    // =========================================================================
    // FETCHING
    // =========================================================================

    /// <summary>
    /// Gives a link's preview, fetching it the first time.
    /// </summary>
    /// <remarks>
    /// A link <see cref="IsAllowed"/> refuses is never requested. The last 50 links' results (null included) are kept in
    /// memory, and a second call for a link whose fetch is still running shares it. Never throws: any failure, or
    /// <paramref name="ct"/> being canceled, gives null.
    /// </remarks>
    /// <param name="url">The link.</param>
    /// <param name="ct">Stops waiting; the fetch itself carries on for the cache.</param>
    /// <returns>The preview, or null when there is none.</returns>
    public async Task<LinkPreview?> GetAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!IsAllowed(url, _allowLoopback))
        {
            return null;
        }

        // Most Recently Used Goes Last; The Oldest Drops Off The Front
        Task<LinkPreview?> fetch;
        lock (_cacheLock)
        {
            if (_cache.Remove(url, out Task<LinkPreview?>? cached))
            {
                fetch = cached;
            }
            else
            {
                fetch = FetchAsync(url);
                if (_cache.Count >= CacheSize)
                {
                    _cache.RemoveAt(0);
                }
            }

            _cache.Add(url, fetch);
        }

        try
        {
            return await fetch.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        _http.Dispose();
    }

    // The Page, Then Its Image; A Page Without A Title Gives Nothing, A Missing Image Only Drops The Image
    private async Task<LinkPreview?> FetchAsync(Uri url)
    {
        try
        {
            if (await GetBodyAsync(url, "text/html", MaxPageBytes).ConfigureAwait(false) is not { } page)
            {
                return null;
            }

            if (ParseHead(Decode(page.Body, page.CharSet), url, _allowLoopback) is not { } head)
            {
                LogSkipped("no title");
                return null;
            }

            byte[]? image = head.Image is { } image_url ? (await GetBodyAsync(image_url, "image/*", MaxImageBytes).ConfigureAwait(false))?.Body : null;
            return new LinkPreview(url, head.Title, head.Site, image);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError(ex);
            return null;
        }
    }

    // One GET Within The Time And Size Limits, Of The Wanted Type, Ending (After Redirects) Somewhere Still Allowed.
    // Null, Logged By Reason Or Error Type, Otherwise
    private async Task<(byte[] Body, string? CharSet)?> GetBodyAsync(Uri url, string accept, int maxBytes)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            timeout.CancelAfter(RequestTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", GmConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept);

            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            string type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            bool wanted = accept == "image/*"
                ? type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
                : type.Equals(accept, StringComparison.OrdinalIgnoreCase);

            string? refused = !response.IsSuccessStatusCode                                            ? "status " + (int)response.StatusCode
                : !IsAllowed(response.RequestMessage?.RequestUri ?? url, _allowLoopback)               ? "redirected away"
                : !wanted                                                                              ? "wrong type"
                : response.Content.Headers.ContentLength > maxBytes                                    ? "too large"
                : null;
            if (refused is not null)
            {
                LogSkipped(refused);
                return null;
            }

            // Read At Most maxBytes, Whatever The Length Header Said
            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var body      = new MemoryStream();
            byte[] chunk        = new byte[16_384];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > maxBytes)
                {
                    LogSkipped("too large");
                    return null;
                }

                body.Write(chunk, 0, read);
            }

            return (body.ToArray(), response.Content.Headers.ContentType?.CharSet);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError(ex);
            return null;
        }
    }

    // Connects Only When Every Address The Host Resolves To Is Allowed, And Only To Those Addresses
    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, bool allowLoopback, CancellationToken ct)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || !addresses.All(a => IsPublic(a) || (allowLoopback && IPAddress.IsLoopback(a))))
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "The host is not a public address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endpoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The Page's Charset, Else UTF-8
    private static string Decode(byte[] body, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                encoding = Encoding.GetEncoding(charset.Trim('"', ' '));
            }
        }
        catch (ArgumentException)
        {
            // An Unknown Charset Reads As UTF-8
        }

        return encoding.GetString(body);
    }

    // The Error Type Only: An Exception's Message Can Hold The Link
    private void LogError(Exception ex) => LogFailed(ex.GetType().Name);

    // Tags Removed, Entities Decoded, Spaces Collapsed
    private static string PlainText(string? html) =>
        html is null ? string.Empty : Spaces().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, " ")), " ").Trim();

    // Sentence Punctuation Comes Off The End; A Closing Bracket Stays When The Link Opened One Itself
    private static string TrimLink(string link)
    {
        while (link.Length > 0 && TrailingPunctuation.Contains(link[^1], StringComparison.Ordinal))
        {
            char last = link[^1];
            char open = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
            if (open != '\0' && link.Count(c => c == open) >= link.Count(c => c == last))
            {
                break;
            }

            link = link[..^1];
        }

        return link;
    }

    [GeneratedRegex(@"\bhttps?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""(?<name>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'>]+))""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title\b[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [LoggerMessage(Level = LogLevel.Information, Message = "No link preview ({Reason})")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link preview failed ({ErrorType})")]
    private partial void LogFailed(string errorType);
}
