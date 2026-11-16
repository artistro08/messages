using System.Net;
using System.Text;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Http;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Tests.Fakes;
using Windows.Graphics.Imaging;

namespace GMessages.Presentation.Tests.Messaging;

[TestClass]
public sealed class LinkPreviewerTests
{
    private static readonly Uri Page = new("https://www.example.com/a");

    private const string Html =
        """
        <html><head>
        <meta property="og:title" content="Example Title">
        <meta property="og:site_name" content="Example">
        </head><body>hi</body></html>
        """;

    private static HttpResponseMessage Redirect(string location)
    {
        HttpResponseMessage response = FakeHttpHandler.Response(HttpStatusCode.Found);
        response.Headers.Location    = new Uri(location);
        return response;
    }

    private static HttpResponseMessage HtmlResponse(string html) =>
        FakeHttpHandler.Response(HttpStatusCode.OK, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

    // =========================================================================
    // LINKS AND ADDRESSES
    // =========================================================================

    [TestMethod]
    [DataRow("see https://x.com.", "https://x.com/")]
    [DataRow("(see https://x.com/a)", "https://x.com/a")]
    [DataRow("\"https://x.com/a?b=1\",", "https://x.com/a?b=1")]
    [DataRow("wow https://x.com/a!?", "https://x.com/a")]
    [DataRow("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [DataRow("go to http://b.example/c; then", "http://b.example/c")]
    [DataRow("a https://x.com/a)))", "https://x.com/a")]
    [DataRow("a https://x.com/a_(b)).", "https://x.com/a_(b)")]
    [DataRow("a https://x.com/a[1]],", "https://x.com/a[1]")]
    public void FirstLink_TrimsPunctuation(string text, string expected) =>
        Assert.AreEqual(new Uri(expected), LinkPreviewer.FirstTextLink(text)?.Url);

    [TestMethod]
    public void FirstLink_NoLink_Null() => Assert.IsNull(LinkPreviewer.FirstTextLink("no link here, ftp://x.com"));

    // =========================================================================
    // LINKS IN TEXT
    // =========================================================================

    // Each link has the form "Start:Shown Text>Url". A space joins the links.
    private static string Links(string text) =>
        string.Join(" ", LinkPreviewer.FindLinks(text).Select(l => $"{l.Start}:{text.Substring(l.Start, l.Length)}>{l.Url.AbsoluteUri}"));

    [TestMethod]
    [DataRow("see https://x.com.", "4:https://x.com>https://x.com/")]
    [DataRow("(see https://x.com/a)", "5:https://x.com/a>https://x.com/a")]
    [DataRow("https://en.wikipedia.org/wiki/Foo_(bar), ok", "0:https://en.wikipedia.org/wiki/Foo_(bar)>https://en.wikipedia.org/wiki/Foo_(bar)")]
    [DataRow("wow http://b.example/c?d=1!?", "4:http://b.example/c?d=1>http://b.example/c?d=1")]
    [DataRow("\"https://x.com/a\",", "1:https://x.com/a>https://x.com/a")]
    public void FindLinks_TrimsPunctuationAndParentheses(string text, string expected) => Assert.AreEqual(expected, Links(text));

    [TestMethod]
    public void FindLinks_Several() =>
        Assert.AreEqual("0:https://a.com>https://a.com/ 18:http://b.com/x>http://b.com/x", Links("https://a.com and http://b.com/x"));

    [TestMethod]
    public void FindLinks_Www_OpensOverHttps() =>
        Assert.AreEqual("8:www.example.com/a>https://www.example.com/a", Links("Try it: www.example.com/a."));

    [TestMethod]
    [DataRow("https://x.com😀 nice", "0:https://x.com>https://x.com/")]
    [DataRow("😀https://x.com/a", "2:https://x.com/a>https://x.com/a")]
    [DataRow("https://x.com/a❤️", "0:https://x.com/a>https://x.com/a")]
    public void FindLinks_StopsAtEmoji(string text, string expected) => Assert.AreEqual(expected, Links(text));

    [TestMethod]
    [DataRow("no links here")]
    [DataRow("")]
    [DataRow("ftp://x.com file:///c:/x javascript:alert(1) ms-settings:display")]
    [DataRow("awww.example.com and https:// alone")]
    [DataRow("www.bank.com@evil.example/login and https://user:pw@x.com/a")]
    public void FindLinks_None(string text) => Assert.AreEqual(string.Empty, Links(text));

    /// <summary>
    /// The previewer fetches only HTTPS to a named public host. It must never fetch a local or private host.
    /// </summary>
    [TestMethod]
    [DataRow("http://example.com/")]
    [DataRow("https://192.168.1.1/")]
    [DataRow("https://8.8.8.8/")]
    [DataRow("https://[::1]/")]
    [DataRow("https://localhost/")]
    [DataRow("https://localhost./")]
    [DataRow("https://printer.local/")]
    [DataRow("https://router/")]
    [DataRow("https://user:pass@example.com/")]
    [DataRow("https://example.com:8443/")]
    [DataRow("file:///C:/Windows/win.ini")]
    public void IsAllowed_RefusesUnsafeUrls(string url)
    {
        Assert.IsFalse(LinkPreviewer.IsAllowed(new Uri(url), allowLoopback: false));
    }

    [TestMethod]
    public void IsAllowed_PublicHttps_And_TestLoopback()
    {
        Assert.IsTrue(LinkPreviewer.IsAllowed(new Uri("https://www.example.com/a?b=1"), allowLoopback: false));
        Assert.IsTrue(LinkPreviewer.IsAllowed(new Uri("http://127.0.0.1:5000/preview-page"), allowLoopback: true));
        Assert.IsFalse(LinkPreviewer.IsAllowed(new Uri("http://127.0.0.1:5000/preview-page"), allowLoopback: false));
        Assert.IsFalse(LinkPreviewer.IsAllowed(new Uri("http://192.168.1.1/"), allowLoopback: true));
    }

    /// <summary>
    /// The previewer refuses each of these addresses, one for each refused range.
    /// It must never fetch a local or private host.
    /// </summary>
    [TestMethod]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    [DataRow("10.1.2.3")]
    [DataRow("172.16.0.1")]
    [DataRow("172.31.255.255")]
    [DataRow("192.168.1.1")]
    [DataRow("fc00::1")]
    [DataRow("fd12:3456::1")]
    [DataRow("169.254.169.254")]
    [DataRow("fe80::1")]
    [DataRow("100.64.0.1")]
    [DataRow("100.127.255.255")]
    [DataRow("0.0.0.0")]
    [DataRow("::")]
    [DataRow("224.0.0.1")]
    [DataRow("ff02::1")]
    [DataRow("255.255.255.255")]
    [DataRow("192.0.0.8")]
    [DataRow("192.0.2.1")]
    [DataRow("198.18.0.1")]
    [DataRow("198.19.255.255")]
    [DataRow("198.51.100.7")]
    [DataRow("203.0.113.9")]
    [DataRow("::ffff:10.0.0.1")]
    [DataRow("64:ff9b::a00:1")]
    [DataRow("64:ff9b::7f00:1")]
    [DataRow("64:ff9b:1:a00:0:100::")]
    [DataRow("64:ff9b:1:808:8:800::")]
    [DataRow("64:ff9b:1::808:808")]
    [DataRow("2002:a00:1::1")]
    [DataRow("2002:c0a8:101::1")]
    [DataRow("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [DataRow("::ffff:0:a00:1")]
    [DataRow("::ffff:0:808:808")]
    [DataRow("::a00:1")]
    [DataRow("::808:808")]
    public void IsPublic_RefusesPrivateAddresses(string address)
    {
        Assert.IsFalse(LinkPreviewer.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("172.32.0.1")]
    [DataRow("100.128.0.1")]
    [DataRow("192.0.1.1")]
    [DataRow("198.17.255.255")]
    [DataRow("198.20.0.1")]
    [DataRow("198.51.101.1")]
    [DataRow("203.0.112.1")]
    [DataRow("2606:4700::1111")]
    [DataRow("64:ff9b::808:808")]
    [DataRow("2002:808:808::1")]
    public void IsPublic_AllowsPublicAddresses(string address)
    {
        Assert.IsTrue(LinkPreviewer.IsPublic(IPAddress.Parse(address)));
    }

    // =========================================================================
    // PARSING
    // =========================================================================

    [TestMethod]
    public void ParseHead_OgTags_EitherAttributeOrder()
    {
        const string Html =
            """
            <head>
            <title>Fallback</title>
            <meta content="Og Title" property="og:title" />
            <meta property='og:site_name' content='Site Name'>
            <META CONTENT="/img/card.png" PROPERTY="og:image">
            </head>
            """;

        (string title, string site, Uri? image, _) = LinkPreviewer.ParseHead(Html, Page)!.Value;

        Assert.AreEqual("Og Title", title);
        Assert.AreEqual("example.com", site, "the site is the real host, never og:site_name");
        Assert.AreEqual(new Uri("https://www.example.com/img/card.png"), image);
    }

    [TestMethod]
    public void ParseOEmbed_ReadsYouTubeTitleAndThumbnail()
    {
        byte[] json = Encoding.UTF8.GetBytes("""{"title":"Big Buck Bunny","provider_name":"YouTube","thumbnail_url":"https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg"}""");

        (string title, string site, Uri? image, _) = LinkPreviewer.ParseOEmbed(json)!.Value;

        Assert.AreEqual("Big Buck Bunny", title);
        Assert.AreEqual("youtube.com", site);
        Assert.AreEqual(new Uri("https://i.ytimg.com/vi/aqz-KE-bpKQ/hqdefault.jpg"), image);
    }

    [TestMethod]
    [DataRow("""{"thumbnail_url":"https://i.ytimg.com/a.jpg"}""")]
    [DataRow("""{"title":"","thumbnail_url":"https://i.ytimg.com/a.jpg"}""")]
    [DataRow("not json")]
    [DataRow("[]")]
    public void ParseOEmbed_NoTitle_GivesNull(string json) =>
        Assert.IsNull(LinkPreviewer.ParseOEmbed(Encoding.UTF8.GetBytes(json)));

    [TestMethod]
    public void ParseOEmbed_KeepsTitleAsWritten()
    {
        (string title, _, _, _) = LinkPreviewer.ParseOEmbed(Encoding.UTF8.GetBytes("""{"title":"Learn <div> &amp; <span>  fast"}"""))!.Value;

        Assert.AreEqual("Learn <div> &amp; <span> fast", title);
    }

    [TestMethod]
    public void ParseOEmbed_ThumbnailNotAllowed_DropsImage()
    {
        (_, _, Uri? image, _) = LinkPreviewer.ParseOEmbed(Encoding.UTF8.GetBytes("""{"title":"A","thumbnail_url":"http://127.0.0.1/a.jpg"}"""))!.Value;

        Assert.IsNull(image);
    }

    [TestMethod]
    public void ParseHead_FallsBackToTitleAndHost()
    {
        (string title, string site, Uri? image, _) = LinkPreviewer.ParseHead("<html><head><title>\n  Plain   Title </title></head></html>", Page)!.Value;

        Assert.AreEqual("Plain Title", title);
        Assert.AreEqual("example.com", site);
        Assert.IsNull(image);
    }

    /// <summary>
    /// A host with non-ASCII letters shows in punycode form, so that it cannot pass as the real site.
    /// The test host is a Cyrillic look-alike of google.com.
    /// </summary>
    [TestMethod]
    public void ParseHead_NonAsciiHost_ShowsPunycode()
    {
        (_, string site, _, _) = LinkPreviewer.ParseHead("<title>Sign in</title>", new Uri("https://www.gооgle.com/"))!.Value;

        Assert.AreEqual("xn--ggle-55da.com", site);
    }

    [TestMethod]
    public void ParseHead_DecodesEntities_NoMarkup()
    {
        (string title, _, _, _) = LinkPreviewer.ParseHead("<title>Tom &amp; <b>Jerry</b> &#8211; &quot;Hi&quot;</title>", Page)!.Value;

        Assert.AreEqual("Tom & Jerry \u2013 \"Hi\"", title);
    }

    [TestMethod]
    public void ParseHead_StripsBidiControls_CapsLength()
    {
        string longTitle     = new('a', 500);
        (string title, _, _, _) = LinkPreviewer.ParseHead($"<title>Pay\u202Egnp.exe {longTitle}</title>", Page)!.Value;

        Assert.IsFalse(title.Contains('\u202E', StringComparison.Ordinal), title);
        StringAssert.StartsWith(title, "Paygnp.exe aaa");
        Assert.AreEqual(301, title.Length);
        Assert.IsTrue(title.EndsWith('\u2026'));
    }

    [TestMethod]
    public void ParseHead_UnsafeImage_Dropped()
    {
        (_, _, Uri? image, _) = LinkPreviewer.ParseHead("<title>T</title><meta property=\"og:image\" content=\"http://10.0.0.1/x.png\">", Page)!.Value;

        Assert.IsNull(image);
    }

    [TestMethod]
    [DataRow("twitter:image")]
    [DataRow("twitter:image:src")]
    [DataRow("og:image:secure_url")]
    public void ParseHead_NoOgImage_UsesOtherImageTag(string key)
    {
        (_, _, Uri? image, _) = LinkPreviewer.ParseHead($"<title>T</title><meta name=\"{key}\" content=\"/card.png\">", Page)!.Value;

        Assert.AreEqual(new Uri(Page, "/card.png"), image);
    }

    [TestMethod]
    public void ParseHead_OgImage_WinsOverOtherImageTag()
    {
        (_, _, Uri? image, _) = LinkPreviewer.ParseHead("<title>T</title><meta name=\"twitter:image\" content=\"/t.png\"><meta property=\"og:image\" content=\"/og.png\">", Page)!.Value;

        Assert.AreEqual(new Uri(Page, "/og.png"), image);
    }

    [TestMethod]
    [DataRow("<title>T</title><meta name=\"description\" content=\"Plain\"><meta property=\"og:description\" content=\"Og &amp; more\">", "Og & more")]
    [DataRow("<title>T</title><meta name=\"twitter:description\" content=\"From  twitter\">", "From twitter")]
    [DataRow("<title>T</title>", "")]
    public void ParseHead_Description_OgFirstThenOthers(string html, string expected)
    {
        (_, _, _, string description) = LinkPreviewer.ParseHead(html, Page)!.Value;

        Assert.AreEqual(expected, description);
    }

    [TestMethod]
    public void ParseHead_NoTitle_Null() => Assert.IsNull(LinkPreviewer.ParseHead("<html><body>nothing</body></html>", Page));

    // =========================================================================
    // FETCHING
    // =========================================================================

    // This page has the given bytes as its card image.
    private static FakeHttpHandler ImagePage(byte[] png) => new()
    {
        Respond = (_, r) => r.Url.EndsWith(".png", StringComparison.Ordinal)
            ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/png", png)
            : HtmlResponse(Html.Replace("</head>", "<meta property=\"og:image\" content=\"https://cdn.example.com/c.png\"></head>", StringComparison.Ordinal)),
    };

    // An alpha value of 255 makes the PNG opaque. An alpha value of 128 makes it half see-through.
    private static async Task<byte[]> MakePngAsync(uint width, uint height, byte alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(i % 251), 80, 160, alpha);
        }

        using var output      = new MemoryStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        return output.ToArray();
    }

    // The decoder gives the size and the format of the image.
    private static async Task<BitmapDecoder> DecodeAsync(byte[] bytes) =>
        await BitmapDecoder.CreateAsync(new MemoryStream(bytes, writable: false).AsRandomAccessStream());

    /// <summary>
    /// The card of the phone gives the text, and only its image and the site icon are fetched, never the page. The site
    /// is still the real host of the link.
    /// </summary>
    [TestMethod]
    public async Task Get_PhoneCard_UsesItsText_FetchesOnlyImage()
    {
        byte[] png              = await MakePngAsync(40, 20, 255);
        FakeHttpHandler handler = ImagePage(png);
        using var previewer     = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);
        var phone               = new LinkPreviewData { Title = "Neil  Ashton", Description = "#badumtss", ImageURL = "https://cdn.example.com/p.png", Domain = "evil.example" };

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None, phone);

        Assert.IsNotNull(preview);
        Assert.AreEqual("Neil Ashton", preview.Title);
        Assert.AreEqual("#badumtss", preview.Description);
        Assert.AreEqual("example.com", preview.Site, "the site is the real host, never the domain of the card");
        CollectionAssert.AreEqual(png, preview.Image);
        Assert.HasCount(3, handler.Requests, "the image, then the icon of the site and of its parent domain");
        Assert.EndsWith(".png", handler.Requests[0].Url);
        Assert.EndsWith("/favicon.ico", handler.Requests[1].Url);
        Assert.EndsWith("/favicon.ico", handler.Requests[2].Url);
    }

    /// <summary>
    /// The card of the phone replaces the card of the page when it comes later, even when the page gave a card, such as
    /// the login wall of Instagram. A later plain request then gets the card of the phone too.
    /// </summary>
    [TestMethod]
    [DataRow("<html><head></head></html>")]
    [DataRow("<html><head><title>Instagram</title></head></html>")]
    public async Task Get_PhoneCardLater_ReplacesPageCard(string page)
    {
        var handler         = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(page) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        await previewer.GetAsync(Page, CancellationToken.None);
        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None, new LinkPreviewData { Title = "Later" });
        LinkPreview? again   = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.AreEqual("Later", preview?.Title);
        Assert.AreEqual("Later", again?.Title);
    }

    /// <summary>
    /// The card of the phone belongs to a link with the same host and path. The scheme, www and the query do not count.
    /// </summary>
    [TestMethod]
    [DataRow("https://www.instagram.com/p/Dfb9/?igsh=x", "https://www.instagram.com/p/Dfb9/?igsh=x", true)]
    [DataRow("https://www.instagram.com/p/Dfb9/?igsh=x", "instagram.com/p/Dfb9", true)]
    [DataRow("https://www.instagram.com/p/Dfb9/", "//www.instagram.com/p/Dfb9/", true)]
    [DataRow("https://www.instagram.com/p/Dfb9/", "https://www.instagram.com/p/Other/", false)]
    [DataRow("https://www.instagram.com/p/Dfb9/", "https://evil.example/p/Dfb9/", false)]
    [DataRow("https://www.instagram.com/p/Dfb9/", "", false)]
    [DataRow("https://www.instagram.com/p/Dfb9?u=https://x.example", "instagram.com/p/Dfb9?u=https://x.example", true)]
    [DataRow("https://www.youtube.com/watch?v=AAA", "https://www.youtube.com/watch?v=BBB", false)]
    [DataRow("https://www.youtube.com/watch?v=AAA", "https://www.youtube.com/watch?v=AAA", true)]
    public void SameLink_MatchesHostAndPath(string link, string phoneUrl, bool same) =>
        Assert.AreEqual(same, LinkPreviewer.SameLink(new Uri(link), phoneUrl));

    [TestMethod]
    public async Task Get_Html_GivesPreview_WithImage()
    {
        byte[] png              = await MakePngAsync(40, 20, 255);
        FakeHttpHandler handler = ImagePage(png);
        using var previewer     = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual(Page, preview.Url);
        Assert.AreEqual("Example Title", preview.Title);
        Assert.AreEqual("example.com", preview.Site);
        CollectionAssert.AreEqual(png, preview.Image);
        Assert.AreEqual(GmConstants.UserAgent, handler.Requests[0].Headers["User-Agent"]);
        Assert.IsFalse(handler.Requests[0].Headers.ContainsKey("Cookie"));
    }

    /// <summary>
    /// A wide opaque card image is stored as JPEG, at most 480 pixels wide, with the same aspect ratio.
    /// </summary>
    [TestMethod]
    public async Task Preview_LargeImage_StoredAtMost480Wide()
    {
        using var previewer = new LinkPreviewer(ImagePage(await MakePngAsync(1600, 800, 255)), false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview?.Image);
        BitmapDecoder decoder = await DecodeAsync(preview.Image);
        Assert.AreEqual((uint)LinkPreviewer.MaxImageWidth, decoder.PixelWidth);
        Assert.AreEqual(240u, decoder.PixelHeight);
        Assert.AreEqual(BitmapDecoder.JpegDecoderId, decoder.DecoderInformation.CodecId);
    }

    /// <summary>
    /// A card image over 16 million pixels is dropped, although each side is within 8192. The card keeps its text.
    /// </summary>
    [TestMethod]
    public async Task Preview_HugePixelCount_Rejected()
    {
        byte[] png = await MakePngAsync(4001, 4000, 255);
        Assert.IsLessThan(LinkPreviewer.MaxImageBytes, png.Length, "Must be refused for its pixels, not its bytes");
        using var previewer = new LinkPreviewer(ImagePage(png), false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual("Example Title", preview.Title);
        Assert.IsNull(preview.Image);
    }

    /// <summary>
    /// A narrow card image taller than 8192 pixels is dropped, although the width alone would allow it.
    /// </summary>
    [TestMethod]
    public async Task Preview_TallNarrow_Rejected()
    {
        byte[] png = await MakePngAsync(100, 8193, 255);
        Assert.IsLessThan(LinkPreviewer.MaxImageBytes, png.Length, "Must be refused for its pixels, not its bytes");
        using var previewer = new LinkPreviewer(ImagePage(png), false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.IsNull(preview.Image);
    }

    /// <summary>
    /// A wide card image with transparency is scaled down and stays PNG.
    /// </summary>
    [TestMethod]
    public async Task Preview_TransparentPng_StaysPng()
    {
        using var previewer = new LinkPreviewer(ImagePage(await MakePngAsync(1600, 800, 128)), false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview?.Image);
        BitmapDecoder decoder = await DecodeAsync(preview.Image);
        Assert.AreEqual((uint)LinkPreviewer.MaxImageWidth, decoder.PixelWidth);
        Assert.AreEqual(BitmapDecoder.PngDecoderId, decoder.DecoderInformation.CodecId);
    }

    [TestMethod]
    public async Task Get_NotHtml_Null()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json", "{}"u8.ToArray()) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
    }

    [TestMethod]
    public async Task Get_TooLarge_Null()
    {
        string big  = Html + new string('x', LinkPreviewer.MaxPageBytes);
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(big) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
    }

    [TestMethod]
    public async Task Get_TooLargeImage_PreviewWithoutImage()
    {
        var handler = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url.EndsWith(".png", StringComparison.Ordinal)
                ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/png", new byte[LinkPreviewer.MaxImageBytes + 1])
                : HtmlResponse(Html.Replace("</head>", "<meta property=\"og:image\" content=\"/c.png\"></head>", StringComparison.Ordinal)),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.IsNull(preview.Image);
    }

    [TestMethod]
    public async Task FetchImageAsync_Gif_FullBytesNotShrunk()
    {
        byte[] gif  = [.. "GIF89a"u8.ToArray(), .. new byte[5000]];
        var handler = new FakeHttpHandler { Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "image/gif", gif) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        byte[]? bytes = await previewer.FetchImageAsync(new Uri("https://media.example.com/a.gif"), "image/gif", 10_000, CancellationToken.None);

        CollectionAssert.AreEqual(gif, bytes);
    }

    [TestMethod]
    public async Task FetchImageAsync_OverMaxBytes_Null()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "image/gif", new byte[5000]) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.FetchImageAsync(new Uri("https://media.example.com/a.gif"), "image/gif", 4_999, CancellationToken.None));
    }

    [TestMethod]
    public async Task FetchImageAsync_WrongTypeOrDisallowedLink_NullAndNoRequest()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "image/png", new byte[10]) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.FetchImageAsync(new Uri("https://media.example.com/a.gif"), "image/gif", 10_000, CancellationToken.None));
        Assert.IsNull(await previewer.FetchImageAsync(new Uri("http://10.0.0.1/a.gif"), "image/gif", 10_000, CancellationToken.None));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    /// <summary>
    /// The card keeps the site icon from <c>/favicon.ico</c> of the link when the site has one.
    /// </summary>
    [TestMethod]
    public async Task Get_SiteHasIcon_KeepsIt()
    {
        byte[] icon = [1, 2, 3, 4];
        var handler = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url.EndsWith("/favicon.ico", StringComparison.Ordinal)
                ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/x-icon", icon)
                : HtmlResponse(Html),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        CollectionAssert.AreEqual(icon, preview.Icon);
    }

    /// <summary>
    /// The icon that the page names wins over /favicon.ico. When it is missing, the card falls back to /favicon.ico.
    /// </summary>
    [TestMethod]
    public async Task Get_PageNamesIcon_UsesIt_ElseFavicon()
    {
        byte[] named    = [9, 9, 9];
        byte[] favicon  = [1, 1, 1];
        string page     = "<html><head><title>T</title><link rel=\"icon\" href=\"/static/site.png\"></head></html>";
        bool namedFound = true;
        var handler     = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url.EndsWith("/static/site.png", StringComparison.Ordinal)
                ? (namedFound ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/png", named) : FakeHttpHandler.Response(HttpStatusCode.NotFound, "text/plain", "x"u8.ToArray()))
                : r.Url.EndsWith("/favicon.ico", StringComparison.Ordinal)
                    ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/x-icon", favicon)
                    : HtmlResponse(page),
        };

        using (var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct))
        {
            CollectionAssert.AreEqual(named, (await previewer.GetAsync(Page, CancellationToken.None))!.Icon);
        }

        namedFound = false;
        using (var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct))
        {
            CollectionAssert.AreEqual(favicon, (await previewer.GetAsync(Page, CancellationToken.None))!.Icon);
        }
    }

    /// <summary>
    /// The page names its icon with rel icon or shortcut icon, else apple-touch-icon. An SVG icon does not count. A
    /// relative link resolves against the page, and a local address never counts.
    /// </summary>
    [TestMethod]
    public void ParseIcon_PicksTheBitmapIcon()
    {
        Assert.AreEqual(new Uri(Page, "/a.png"), LinkPreviewer.ParseIcon("<link rel=\"shortcut icon\" href=\"/a.png\">", Page));
        Assert.AreEqual(new Uri(Page, "/t.png"), LinkPreviewer.ParseIcon("<link rel=\"apple-touch-icon\" href=\"/t.png\"><link rel=icon href=/i.svg>", Page));
        Assert.AreEqual(new Uri(Page, "/i.png"), LinkPreviewer.ParseIcon("<link rel=\"apple-touch-icon\" href=\"/t.png\"><link rel=\"icon\" type=\"image/svg+xml\" href=\"/i\"><link rel=\"icon\" href=\"/i.png\">", Page));
        Assert.IsNull(LinkPreviewer.ParseIcon("<link rel=\"stylesheet\" href=\"/s.css\">", Page));
        Assert.IsNull(LinkPreviewer.ParseIcon("<link rel=\"icon\" href=\"http://10.0.0.1/i.png\">", Page));
    }

    /// <summary>
    /// After the icon that the page names, the icon is tried on the site, then on each parent domain, as
    /// support.google.com uses the icon of google.com. A public suffix such as co.uk and an IP address get no parents.
    /// </summary>
    [TestMethod]
    public void IconCandidates_SiteThenParentDomains()
    {
        static string Of(Uri? declared, string site) => string.Join(" | ", LinkPreviewer.IconCandidates(declared, new Uri(site)).Select(u => u.AbsoluteUri));

        Assert.AreEqual(
            "https://support.google.com/i.png | https://support.google.com/favicon.ico | https://google.com/favicon.ico",
            Of(new Uri("https://support.google.com/i.png"), "https://support.google.com/a/b?c=1"));
        Assert.AreEqual("https://news.bbc.co.uk/favicon.ico | https://bbc.co.uk/favicon.ico", Of(null, "https://news.bbc.co.uk/"));
        Assert.AreEqual("https://example.com/favicon.ico", Of(new Uri("https://example.com/favicon.ico"), "https://example.com/"));
        Assert.AreEqual("http://127.0.0.1:8080/favicon.ico", Of(null, "http://127.0.0.1:8080/page"));
    }

    /// <summary>
    /// A subdomain with no icon of its own shows the icon of its parent domain.
    /// </summary>
    [TestMethod]
    public async Task Get_SubdomainHasNoIcon_UsesParentDomainIcon()
    {
        byte[] icon = [7, 7, 7];
        var handler = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url == "https://example.com/favicon.ico"
                ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/x-icon", icon)
                : r.Url.EndsWith("/favicon.ico", StringComparison.Ordinal)
                    ? FakeHttpHandler.Response(HttpStatusCode.NotFound, "text/plain", "x"u8.ToArray())
                    : HtmlResponse(Html),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(new Uri("https://support.example.com/article"), CancellationToken.None);

        Assert.IsNotNull(preview);
        CollectionAssert.AreEqual(icon, preview.Icon);
    }

    /// <summary>
    /// A site with no icon gives a card without one.
    /// </summary>
    [TestMethod]
    public async Task Get_SiteHasNoIcon_NoIcon()
    {
        var handler = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url.EndsWith("/favicon.ico", StringComparison.Ordinal)
                ? FakeHttpHandler.Response(HttpStatusCode.NotFound, "text/plain", "Not found"u8.ToArray())
                : HtmlResponse(Html),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.IsNull(preview.Icon);
    }

    [TestMethod]
    public async Task Get_SecondCall_Cached()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? first  = await previewer.GetAsync(Page, CancellationToken.None);
        LinkPreview? second = await previewer.GetAsync(Page, CancellationToken.None);

        // The first call fetches the page, then tries the icon of the site and of its parent domain. The second fetches
        // nothing.
        Assert.AreSame(first, second);
        Assert.AreEqual(3, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Get_UnsafeUrl_NoRequest()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(new Uri("https://192.168.1.1/"), CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    /// <summary>
    /// A link that the system sends through a proxy gets no preview, so the previewer never bypasses the proxy of the
    /// user. This rule came from a review fix.
    /// </summary>
    [TestMethod]
    public async Task Get_SystemProxy_NoRequest()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), new TestProxy(new Uri("http://proxy.example.com:8080/")));

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    [DataRow("https://192.168.1.1/")]
    [DataRow("http://www.example.com/b")]
    [DataRow("https://localhost/")]
    [DataRow("https://www.example.com:8443/")]
    public async Task Get_RedirectToUnsafe_NotFollowed(string target)
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => Redirect(target) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Get_Redirect_Followed_SiteIsFinalHost()
    {
        var handler = new FakeHttpHandler
        {
            Respond = (i, _) => i == 0 ? Redirect("https://other.example.org/page") : HtmlResponse(Html),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual(Page, preview.Url);
        Assert.AreEqual("other.example.org", preview.Site);
        Assert.AreEqual("https://other.example.org/page", handler.Requests[1].Url);
    }

    [TestMethod]
    public async Task Get_TooManyRedirects_Null()
    {
        var handler = new FakeHttpHandler { Respond = (i, _) => Redirect($"https://www.example.com/{i + 1}") };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
        Assert.AreEqual(4, handler.Requests.Count, "the link, then 3 redirects");
    }

    [TestMethod]
    public async Task Get_Failure_LogsTypeNotUrl()
    {
        var handler = new FakeHttpHandler { Respond = (_, r) => throw new HttpRequestException("boom " + r.Url) };
        var log     = new CollectingLogger();
        using var previewer = new LinkPreviewer(handler, false, log, TestProxy.Direct);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));

        string all = string.Join("\n", log.Messages);
        StringAssert.Contains(all, nameof(HttpRequestException));
        Assert.IsFalse(all.Contains("example.com", StringComparison.OrdinalIgnoreCase), all);
    }
}

/// <summary>
/// A system proxy for tests. It sends all requests through a proxy, or it uses <see cref="Direct"/> for no proxy.
/// </summary>
/// <param name="via">The proxy, or null to connect directly.</param>
internal sealed class TestProxy(Uri? via) : IWebProxy
{
    /// <summary>Gets a proxy setting that connects without a proxy.</summary>
    public static TestProxy Direct { get; } = new(null);

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => via ?? destination;

    public bool IsBypassed(Uri host) => via is null;
}
