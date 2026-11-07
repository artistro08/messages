using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using GMessages.Protocol.Http;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GMessages.Presentation.Messaging;

/// <summary>
/// Fetches the preview card of a link, with its title, site and image, as the phone app does. The phone sends no card.
/// </summary>
/// <remarks>
/// <para>
/// The other side chooses the link, so a fetch is a privacy and safety risk, and the rules are strict. The previewer
/// fetches only HTTPS links on port 443 to a named public host, as <see cref="IsAllowed"/> checks. It refuses IP
/// literals, <c>localhost</c> and <c>*.local</c>. The handler from <see cref="CreateHandler"/> resolves the host
/// itself. It connects only to the address that <see cref="IsPublic"/> vetted, on each redirect hop too. Thus DNS
/// rebinding cannot reach the local network. <see cref="IsAllowed"/> also checks each redirect hop. The previewer sends
/// no cookies or credentials. A link that the system sends through a proxy gets no preview. Thus the previewer never
/// goes around the proxy of the user. At most 4 previews fetch at the same time.
/// </para>
/// <para>
/// A page must be <c>text/html</c> within <see cref="MaxPageBytes"/>. An image must be <c>image/*</c> within
/// <see cref="MaxImageBytes"/>. Each must answer within <see cref="RequestTimeout"/>. <see cref="ShrinkAsync"/>
/// scales down an image wider than <see cref="MaxImageWidth"/>. The results, failures included, stay in memory for the
/// last 50 links. The list holds fewer links when their images pass 4 MB. The previewer never writes them to disk. The
/// log holds only the error type of a failure, never the link, title or description.
/// </para>
/// </remarks>
public sealed partial class LinkPreviewer : IDisposable
{
    /// <summary>The largest page that the previewer reads, in bytes. A bigger page gives no preview.</summary>
    public const int MaxPageBytes = 262_144;

    /// <summary>The largest image that the previewer reads, in bytes. A bigger one gives a card without it.</summary>
    public const int MaxImageBytes = 1_048_576;

    /// <summary>The time that each request can take: the page, then its image.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The time that a pasted picture from <see cref="FetchImageAsync"/> can take. It can be much bigger than a card
    /// image.
    /// </summary>
    public static readonly TimeSpan PictureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The widest card image that the previewer keeps, in pixels. The fetch shrinks a wider one.</summary>
    public const int MaxImageWidth = 480;

    // The largest card image that the previewer decodes: on each side, and in pixels in all. The shrink only scales
    // down, so the kept height also stays within the side limit.
    private const int MaxImageSide = 8192;
    private const long MaxImagePixels = 16_000_000;

    // The number of links whose results stay in memory, and the byte budget of their images.
    private const int CacheSize = 50;
    private const int CacheImageBytes = 4 * 1024 * 1024;

    private const int MaxRedirects = 3;

    private const int MaxFetches = 4;

    // The longest title or site that the previewer keeps, in characters. The card shows 2 lines at most.
    private const int MaxTextLength = 300;

    // The longest time that a regular expression can spend on a page, so a hostile page cannot stall the parse.
    private const int HtmlTimeoutMs = 500;

    // A link ends before this punctuation, as in the link detection of a browser or of Windows.
    private const string TrailingPunctuation = ".,;:!?)]}'\"";

    private readonly HttpClient _http;
    private readonly bool _allowLoopback;
    private readonly ILogger _log;
    private readonly IWebProxy _proxy;

    // Limits the number of previews that fetch at the same time, for any number of links in a chat.
    private readonly SemaphoreSlim _fetchSlots = new(MaxFetches);

    // Dispose cancels this source, so running fetches stop.
    private readonly CancellationTokenSource _closing = new();

    // The fetch of each link, running or done, null results included. The order is least recently used first.
    private readonly Lock _cacheLock = new();
    private readonly OrderedDictionary<Uri, Task<LinkPreview?>> _cache = [];

    /// <summary>
    /// Creates the previewer.
    /// </summary>
    /// <param name="handler">
    /// Sends the requests: <see cref="CreateHandler"/> in the app, a fake in tests. The previewer disposes it.
    /// </param>
    /// <param name="allowLoopback">
    /// True only in UI test runs, so the fake server at <c>http://127.0.0.1</c> gets previews.
    /// </param>
    /// <param name="log">Takes the failures, by error type only.</param>
    /// <param name="proxy">
    /// The proxy that Windows sends the link through, or null for the proxy of the system. The previews connect
    /// directly, so a link that this proxy carries gets no preview.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="proxy"/> is null.</exception>
    public LinkPreviewer(HttpMessageHandler handler, bool allowLoopback, ILogger log, IWebProxy? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);

        _http          = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _allowLoopback = allowLoopback;
        _log           = log;
        _proxy         = proxy ?? HttpClient.DefaultProxy;
    }

    /// <summary>
    /// Creates the previewer with the vetted handler of the app from <see cref="CreateHandler"/>. The caller gives the
    /// loopback flag only once.
    /// </summary>
    /// <param name="allowLoopback">
    /// True only in UI test runs, so the fake server at <c>http://127.0.0.1</c> gets previews.
    /// </param>
    /// <param name="log">Takes the failures, by error type only.</param>
    /// <param name="proxy">The proxy that Windows sends the link through, or null for the proxy of the system.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public LinkPreviewer(bool allowLoopback, ILogger log, IWebProxy? proxy = null)
        : this(CreateHandler(allowLoopback), allowLoopback, log, proxy)
    {
    }

    // =========================================================================
    // SAFETY RULES
    // =========================================================================

    /// <summary>
    /// Creates the handler that the app sends previews through.
    /// </summary>
    /// <remarks>
    /// Cookies are off. The previewer follows the redirects itself and holds each hop to <see cref="IsAllowed"/>, 3
    /// hops at most. The <c>ConnectCallback</c> resolves each host itself. It connects only when each resolved address
    /// passes <see cref="IsPublic"/>, or is loopback with <paramref name="allowLoopback"/>. It connects to those vetted
    /// addresses, so a second lookup cannot give a private address instead. The proxy is off, because through a proxy
    /// the check vets the proxy and not the host of the link. Instead, a link that the system sends through a proxy
    /// gets no preview. Thus the previewer never goes around the proxy of a user.
    /// </remarks>
    /// <param name="allowLoopback">True only in UI test runs.</param>
    /// <returns>The handler.</returns>
    public static SocketsHttpHandler CreateHandler(bool allowLoopback) => new()
    {
        UseCookies             = false,
        UseProxy               = false,
        PreAuthenticate        = false,
        AllowAutoRedirect      = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout         = RequestTimeout,
        ConnectCallback        = (context, ct) => ConnectAsync(context.DnsEndPoint, allowLoopback, ct),
    };

    /// <summary>
    /// Finds the first web link in a message, without the punctuation of a sentence around it.
    /// </summary>
    /// <remarks>
    /// The link is the first run of non-spaces that starts with <c>http://</c> or <c>https://</c>. The method removes
    /// <c>.,;:!?)]}'"</c> from its end. A closing bracket stays when the link opened it. Thus
    /// <c>https://en.wikipedia.org/wiki/Foo_(bar)</c> keeps its <c>)</c>.
    /// </remarks>
    /// <param name="text">The message text.</param>
    /// <returns>The link, or null when there is none.</returns>
    public static Uri? FirstLink(string text) => FirstTextLink(text)?.Url;

    /// <summary>
    /// Finds the first web link in a message as <see cref="FirstLink"/> does, with its place in the text.
    /// </summary>
    /// <remarks>
    /// The text of the link, at <see cref="TextLink.Start"/> for <see cref="TextLink.Length"/>, is the address exactly
    /// as sent.
    /// </remarks>
    /// <param name="text">The message text.</param>
    /// <returns>The link and its place, or null when there is none.</returns>
    public static TextLink? FirstTextLink(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (Match match in LinkPattern().Matches(text))
        {
            string link = TrimLink(match.Value);
            if (Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return new TextLink(match.Index, link.Length, uri);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds every web link in the text of a message, so the bubble can show them as links.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A link starts with <c>http://</c>, <c>https://</c> or <c>www.</c> at the start of a word. It runs to the next
    /// space or emoji, VS16 and zero-width joiners included. The method removes the same punctuation from its end as
    /// <see cref="FirstLink"/>. A bare <c>www.</c> host opens over <c>https</c>.
    /// </para>
    /// <para>
    /// The method gives only <c>http</c> and <c>https</c> addresses with no user name. Thus a <c>file:</c>,
    /// <c>javascript:</c> or <c>ms-</c> link stays plain text. So does a link that hides its real host behind a user
    /// name, such as <c>www.bank.com@evil.example</c>. The method works on the shown text from
    /// <see cref="ChatFormat.WithColorEmoji"/> and on the original text.
    /// </para>
    /// </remarks>
    /// <param name="text">The message text.</param>
    /// <returns>The links in order, or empty when there are none.</returns>
    public static IReadOnlyList<TextLink> FindLinks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<TextLink> links = [];
        foreach (Match match in TextLinkPattern().Matches(text))
        {
            string shown   = TrimLink(match.Value);
            string address = shown.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + shown : shown;
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.Host.Length > 0 && uri.UserInfo.Length == 0)
            {
                links.Add(new TextLink(match.Index, shown.Length, uri));
            }
        }

        return links;
    }

    /// <summary>
    /// Tells whether the previewer can fetch a link for a preview.
    /// </summary>
    /// <remarks>
    /// The link must be HTTPS on port 443, to a named host with a dot in it. The method refuses an IP literal,
    /// <c>localhost</c>, <c>*.local</c> and a single-label intranet name. It also refuses a user name and password in
    /// the link. With
    /// <paramref name="allowLoopback"/> in UI test runs, it also allows an <c>http</c> or <c>https</c> link to
    /// <c>127.0.0.1</c>. The handler from <see cref="CreateHandler"/> checks where the host really points when it
    /// connects.
    /// </remarks>
    /// <param name="url">The link.</param>
    /// <param name="allowLoopback">True only in UI test runs.</param>
    /// <returns>True when the previewer can fetch the link.</returns>
    public static bool IsAllowed(Uri url, bool allowLoopback)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || url.UserInfo.Length > 0)
        {
            return false;
        }

        // UI test runs use the fake server on 127.0.0.1.
        if (allowLoopback && url.HostNameType == UriHostNameType.IPv4 && url.IsLoopback && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        if (url.Scheme != Uri.UriSchemeHttps || url.Port != 443 || url.HostNameType != UriHostNameType.Dns)
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
    /// Tells whether an address is on the public internet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The method refuses these IPv4 ranges: loopback, private 10/8, 172.16/12 and 192.168/16, link-local 169.254/16,
    /// and CGNAT 100.64/10. It also refuses the IETF protocol and documentation ranges 192.0.0/24, 192.0.2/24,
    /// 198.51.100/24 and 203.0.113/24, and the benchmark range 198.18/15. It refuses the unspecified range 0/8, and
    /// multicast and reserved 224/4 and 240/4.
    /// </para>
    /// <para>
    /// The method refuses these IPv6 ranges: loopback, unspecified ::, private fc00::/7, link-local fe80::/10,
    /// multicast ff00::/8, and the old site-local range. It judges an IPv6 address that carries an IPv4 address by that
    /// IPv4 address. Such addresses are IPv4-mapped, as <c>::ffff:10.0.0.1</c>, NAT64 64:ff9b::/96, and 6to4 2002::/16.
    /// The method always refuses local-use NAT64 64:ff9b:1::/48, because the network picks its layout. It always
    /// refuses Teredo 2001::/32, SIIT ::ffff:0:0/96 and IPv4-compatible ::/96 too.
    /// </para>
    /// </remarks>
    /// <param name="address">The address.</param>
    /// <returns>True when the address is public.</returns>
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
                || (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2))
                || (b[0] == 198 && (b[1] & 0xFE) == 18)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();

            // 6to4 and well-known NAT64 carry an IPv4 address. The method judges them by that address.
            if (b is [0x20, 0x02, ..])
            {
                return IsPublic(new IPAddress(b.AsSpan(2, 4)));
            }

            if (b is [0x00, 0x64, 0xFF, 0x9B, 0, 0, 0, 0, 0, 0, 0, 0, ..])
            {
                return IsPublic(new IPAddress(b.AsSpan(12, 4)));
            }

            // The method always refuses local-use NAT64 /48, because the network chooses its layout, as RFC 8215 says.
            // It also refuses Teredo, SIIT and IPv4-compatible ::/96, which also holds :: and ::1.
            if (b is [0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01, ..]
                || b is [0x20, 0x01, 0x00, 0x00, ..]
                || b is [0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, ..]
                || b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0)
            {
                return false;
            }

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
    /// Reads the parts of the preview from the head of a page, with regular expressions only.
    /// </summary>
    /// <remarks>
    /// The title is <c>og:title</c>, else <c>&lt;title&gt;</c>. The site is always the real host of the page without
    /// <c>www.</c>, never the claim of the page. Thus a card cannot pose as another site. The attributes can come in
    /// any order and with any quotes. The method removes tags, decodes entities, removes direction-control characters and
    /// collapses spaces in the title. It then cuts the title to 300 characters. The image is <c>og:image</c> resolved
    /// against <paramref name="page"/>, and the method keeps it only when <see cref="IsAllowed"/> allows it.
    /// </remarks>
    /// <param name="html">The page.</param>
    /// <param name="page">The address of the page after redirects, for relative image links and the host.</param>
    /// <param name="allowLoopback">True only in UI test runs.</param>
    /// <returns>The parts, or null when the page has no title.</returns>
    internal static (string Title, string Site, Uri? Image)? ParseHead(string html, Uri page, bool allowLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(page);

        // Only the head holds what a card needs.
        int end     = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        string head = end >= 0 ? html[..end] : html;

        // The first Open Graph tag of each kind wins.
        string? ogTitle = null;
        string? ogImage = null;
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
                ogTitle ??= content;
            }
            else if (key.Equals("og:image", StringComparison.OrdinalIgnoreCase))
            {
                ogImage ??= content;
            }
        }

        string title = PlainText(ogTitle);
        if (title.Length == 0 && TitleTag().Match(head) is { Success: true } titleTag)
        {
            title = PlainText(titleTag.Groups["text"].Value);
        }

        if (title.Length == 0)
        {
            return null;
        }

        // The site is the real host without www. A non-ASCII host shows as punycode, so a look-alike cannot pass as the
        // real host.
        string host = page.IdnHost;
        string site = Cap(host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host);

        // The image link resolves against the page, and the same rules as a link apply to it.
        Uri? image = ogImage is not null
            && Uri.TryCreate(page, WebUtility.HtmlDecode(ogImage).Trim(), out Uri? resolved)
            && IsAllowed(resolved, allowLoopback)
                ? resolved
                : null;

        return (title, site, image);
    }

    // =========================================================================
    // FETCHING
    // =========================================================================

    /// <summary>
    /// Gives the preview of a link, and fetches it the first time.
    /// </summary>
    /// <remarks>
    /// The previewer never requests a link that <see cref="IsAllowed"/> refuses. The results of the last 50 links, null
    /// included, stay in memory. A second call for a link whose fetch still runs shares that fetch. The method never
    /// throws: any failure, or a canceled <paramref name="ct"/>, gives null.
    /// </remarks>
    /// <param name="url">The link.</param>
    /// <param name="ct">Stops the wait. The fetch itself continues for the cache.</param>
    /// <returns>The preview, or null when there is none.</returns>
    public async Task<LinkPreview?> GetAsync(Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!IsAllowed(url, _allowLoopback))
        {
            return null;
        }

        // The most recently used link goes last. The oldest links drop off the front past 50 links or 4 MB of images,
        // but never the newest link.
        Task<LinkPreview?> fetch;

        // The fetch starts on the pool, because the proxy lookup of Windows can block for seconds.
        lock (_cacheLock)
        {
            fetch = _cache.Remove(url, out Task<LinkPreview?>? cached) ? cached : Task.Run(() => FetchAsync(url));
            _cache.Add(url, fetch);
            while (_cache.Count > 1 && (_cache.Count > CacheSize || CachedImageBytes() > CacheImageBytes))
            {
                _cache.RemoveAt(0);
            }
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

    /// <summary>
    /// Fetches a picture that a message pastes, such as a GIF from the emoji panel. The fetch uses the same vetted
    /// handler as the cards.
    /// </summary>
    /// <remarks>
    /// The rules of a card page apply. <see cref="IsAllowed"/> checks each hop, with 3 redirects at most, no cookies
    /// and no proxy. The previewer does not shrink or cache the picture, because the app sends it as an attachment at
    /// full size. The method never throws. Any failure, a different type, a body over <paramref name="maxBytes"/>, or a
    /// canceled <paramref name="ct"/> gives null.
    /// </remarks>
    /// <param name="uri">The address of the picture.</param>
    /// <param name="mime">The type that the response must declare, such as <c>image/gif</c>.</param>
    /// <param name="maxBytes">The most bytes that the method reads.</param>
    /// <param name="ct">Stops the fetch.</param>
    /// <returns>The bytes of the picture, or null when there are none.</returns>
    public async Task<byte[]?> FetchImageAsync(Uri uri, string mime, long maxBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrEmpty(mime);

        if (!IsAllowed(uri, _allowLoopback))
        {
            return null;
        }

        return (await GetBodyAsync(uri, mime, maxBytes, PictureTimeout, ct).ConfigureAwait(false))?.Body;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
        _http.Dispose();
    }

    // Fetches the page, then its image. A page without a title gives nothing. A missing image only drops the image.
    private async Task<LinkPreview?> FetchAsync(Uri url)
    {
        bool slot = false;
        try
        {
            await _fetchSlots.WaitAsync(_closing.Token).ConfigureAwait(false);
            slot = true;

            if (await GetBodyAsync(url, "text/html", MaxPageBytes, RequestTimeout).ConfigureAwait(false) is not { } page)
            {
                return null;
            }

            if (ParseHead(Decode(page.Body, page.CharSet), page.Final, _allowLoopback) is not { } head)
            {
                LogSkipped("no title");
                return null;
            }

            byte[]? image = head.Image is { } imageUrl ? (await GetBodyAsync(imageUrl, "image/*", MaxImageBytes, RequestTimeout).ConfigureAwait(false))?.Body : null;
            return new LinkPreview(url, head.Title, head.Site, image is null ? null : await ShrinkAsync(image).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError(ex);
            return null;
        }
        finally
        {
            if (slot)
            {
                _fetchSlots.Release();
            }
        }
    }

    /// <summary>
    /// Makes a card image small. The method scales an image wider than <see cref="MaxImageWidth"/> down to that width,
    /// with its EXIF orientation applied. It saves the result as JPEG, or as PNG when any pixel is transparent.
    /// </summary>
    /// <remarks>
    /// An image that is narrow enough keeps its own bytes, so a small GIF still moves. Bytes that Windows cannot read
    /// give no image, because the card cannot show them. An image over 8192 pixels on a side or 16 million pixels in
    /// all also gives no image. A few bytes can claim a huge size, and a remote sender picks the link.
    /// </remarks>
    /// <param name="bytes">The image as fetched.</param>
    /// <returns>The image to keep, or null for none.</returns>
    internal static async Task<byte[]?> ShrinkAsync(byte[] bytes)
    {
        try
        {
            using IRandomAccessStream input = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            BitmapDecoder decoder           = await BitmapDecoder.CreateAsync(input);

            // The bytes do not limit the pixels, so too many pixels are not safe to decode. The bubble does not get
            // the image later either.
            if (decoder.PixelWidth > MaxImageSide || decoder.PixelHeight > MaxImageSide
                || (long)decoder.PixelWidth * decoder.PixelHeight > MaxImagePixels)
            {
                return null;
            }

            if (decoder.OrientedPixelWidth <= MaxImageWidth)
            {
                return bytes;
            }

            // Windows scales first, and then flips and rotates. Thus the transform takes the stored size. The pixels
            // come out upright, and turn a quarter when the orientation swaps the sides.
            double scale  = (double)MaxImageWidth / decoder.OrientedPixelWidth;
            var transform = new BitmapTransform
            {
                ScaledWidth       = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight      = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            PixelDataProvider data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            byte[] pixels          = data.DetachPixelData();
            (uint width, uint height) = decoder.OrientedPixelWidth == decoder.PixelWidth
                ? (transform.ScaledWidth, transform.ScaledHeight)
                : (transform.ScaledHeight, transform.ScaledWidth);

            // Any pixel that is not fully opaque keeps PNG. Else the image becomes JPEG.
            bool transparent = false;
            for (int i = 3; i < pixels.Length && !transparent; i += 4)
            {
                transparent = pixels[i] != 255;
            }

            using var output      = new MemoryStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(transparent ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId, output.AsRandomAccessStream());
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, transparent ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
            await encoder.FlushAsync();
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // The bytes of the images of the cached previews. A fetch that still runs counts as none. The caller holds the
    // cache lock.
    private long CachedImageBytes()
    {
        long total = 0;
        foreach (Task<LinkPreview?> fetch in _cache.Values)
        {
            if (fetch.IsCompletedSuccessfully && fetch.Result?.Image is { } image)
            {
                total += image.Length;
            }
        }

        return total;
    }

    // Sends 1 GET within the time and size limits, for the wanted type. Each hop, redirects included, must be allowed
    // and must not go through the proxy of the system. Else the method logs the reason or error type and gives null.
    private async Task<(byte[] Body, string? CharSet, Uri Final)?> GetBodyAsync(Uri url, string accept, long maxBytes, TimeSpan limit, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, ct);
            timeout.CancelAfter(limit);

            for (int hop = 0; ; hop++)
            {
                if (await GetHopAsync(url, accept, maxBytes, timeout.Token).ConfigureAwait(false) is not { } answer)
                {
                    return null;
                }

                if (answer.RedirectTo is null)
                {
                    return (answer.Body, answer.CharSet, url);
                }

                if (hop == MaxRedirects)
                {
                    LogSkipped("too many redirects");
                    return null;
                }

                url = answer.RedirectTo;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogError(ex);
            return null;
        }
    }

    // Sends 1 hop and gives the target of a redirect, or the body. A refused hop logs its reason and gives null.
    private async Task<(byte[] Body, string? CharSet, Uri? RedirectTo)?> GetHopAsync(Uri url, string accept, long maxBytes, CancellationToken ct)
    {
        string? before = !IsAllowed(url, _allowLoopback) ? "link not allowed"
            : IsProxied(url)                             ? "system proxy"
            : null;
        if (before is not null)
        {
            LogSkipped(before);
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", GmConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", accept);

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // The next hop of a redirect gets the same checks as the first hop.
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            return ([], null, new Uri(url, location));
        }

        string type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        bool wanted = accept == "image/*"
            ? type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase)
            : type.Equals(accept, StringComparison.OrdinalIgnoreCase);

        string? refused = !response.IsSuccessStatusCode                ? "status " + (int)response.StatusCode
            : !wanted                                                  ? "wrong type"
            : response.Content.Headers.ContentLength > maxBytes        ? "too large"
            : null;
        if (refused is not null)
        {
            LogSkipped(refused);
            return null;
        }

        // The loop reads at most maxBytes, whatever the length header said.
        using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var body      = new MemoryStream();
        byte[] chunk        = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                LogSkipped("too large");
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return (body.ToArray(), response.Content.Headers.ContentType?.CharSet, null);
    }

    // Tells whether the system sends the link through a proxy. The fake server of UI test runs never goes through one.
    private bool IsProxied(Uri url)
    {
        if (_allowLoopback && url.IsLoopback)
        {
            return false;
        }

        Uri? via = _proxy.GetProxy(url);
        return via is not null && via != url && !_proxy.IsBypassed(url);
    }

    // Connects only when each address of the host is allowed, and only to those addresses.
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

    // Decodes the page with its charset, else with UTF-8.
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
            // The method reads an unknown charset as UTF-8.
        }

        return encoding.GetString(body);
    }

    // The log holds the error type only, because the message of an exception can hold the link.
    private void LogError(Exception ex) => LogFailed(ex.GetType().Name);

    // Removes tags, decodes entities and removes direction controls, which can make text read backward. It then
    // collapses spaces and cuts the text to length.
    private static string PlainText(string? html) =>
        html is null ? string.Empty : Cap(Spaces().Replace(BidiControls().Replace(WebUtility.HtmlDecode(AnyTag().Replace(html, " ")), string.Empty), " ").Trim());

    // Cuts the text to MaxTextLength characters, and never splits the surrogate pair of an emoji.
    private static string Cap(string text)
    {
        if (text.Length <= MaxTextLength)
        {
            return text;
        }

        int cut = char.IsHighSurrogate(text[MaxTextLength - 1]) ? MaxTextLength - 1 : MaxTextLength;
        return text[..cut].TrimEnd() + "\u2026";
    }

    // Removes the punctuation of a sentence from the end. A closing bracket stays when the link opened 1 itself.
    private static string TrimLink(string link)
    {
        // The method counts the brackets once, then updates the counts as characters come off. Thus a long run of ")"
        // stays linear.
        int roundOpen   = link.AsSpan().Count('(');
        int roundClose  = link.AsSpan().Count(')');
        int squareOpen  = link.AsSpan().Count('[');
        int squareClose = link.AsSpan().Count(']');
        int curlyOpen   = link.AsSpan().Count('{');
        int curlyClose  = link.AsSpan().Count('}');

        int end = link.Length;
        while (end > 0 && TrailingPunctuation.Contains(link[end - 1], StringComparison.Ordinal))
        {
            switch (link[end - 1])
            {
                case ')' when roundOpen >= roundClose:
                case ']' when squareOpen >= squareClose:
                case '}' when curlyOpen >= curlyClose:
                    return link[..end];

                case ')':
                    roundClose--;
                    break;

                case ']':
                    squareClose--;
                    break;

                case '}':
                    curlyClose--;
                    break;
            }

            end--;
        }

        return link[..end];
    }

    [GeneratedRegex(@"\bhttps?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    // A link in text ends at a space or an emoji: symbols, surrogate pairs, VS16 and zero-width joiners.
    [GeneratedRegex(@"\b(?:https?://|www\.)[^\s\p{So}\p{Cs}\uFE0F\u200D]+", RegexOptions.IgnoreCase)]
    private static partial Regex TextLinkPattern();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase, HtmlTimeoutMs)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""(?<name>[\w:-]+)\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s"'>]+))""", RegexOptions.None, HtmlTimeoutMs)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"<title\b[^>]*>(?<text>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, HtmlTimeoutMs)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.None, HtmlTimeoutMs)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex("[\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]")]
    private static partial Regex BidiControls();

    [LoggerMessage(Level = LogLevel.Information, Message = "No link preview ({Reason})")]
    private partial void LogSkipped(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Link preview failed ({ErrorType})")]
    private partial void LogFailed(string errorType);
}
