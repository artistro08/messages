using System.Net;
using System.Text;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Http;
using GMessages.Protocol.Tests.Fakes;

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
    public void FirstLink_TrimsPunctuation(string text, string expected) =>
        Assert.AreEqual(new Uri(expected), LinkPreviewer.FirstLink(text));

    [TestMethod]
    public void FirstLink_NoLink_Null() => Assert.IsNull(LinkPreviewer.FirstLink("no link here, ftp://x.com"));

    // =========================================================================
    // LINKS IN TEXT
    // =========================================================================

    // Each Link As "Start:Shown Text>Url", Joined By Spaces
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
    /// Review Focus 5: only HTTPS to a named public host is fetched.
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
    /// Review Focus 5: one address per refused range.
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
        const string html =
            """
            <head>
            <title>Fallback</title>
            <meta content="Og Title" property="og:title" />
            <meta property='og:site_name' content='Site Name'>
            <META CONTENT="/img/card.png" PROPERTY="og:image">
            </head>
            """;

        (string title, string site, Uri? image) = LinkPreviewer.ParseHead(html, Page)!.Value;

        Assert.AreEqual("Og Title", title);
        Assert.AreEqual("example.com", site, "the site is the real host, never og:site_name");
        Assert.AreEqual(new Uri("https://www.example.com/img/card.png"), image);
    }

    [TestMethod]
    public void ParseHead_FallsBackToTitleAndHost()
    {
        (string title, string site, Uri? image) = LinkPreviewer.ParseHead("<html><head><title>\n  Plain   Title </title></head></html>", Page)!.Value;

        Assert.AreEqual("Plain Title", title);
        Assert.AreEqual("example.com", site);
        Assert.IsNull(image);
    }

    /// <summary>
    /// A host with non-ASCII letters (a Cyrillic look-alike of google.com) shows in its punycode form, so it can't
    /// pass as the real site.
    /// </summary>
    [TestMethod]
    public void ParseHead_NonAsciiHost_ShowsPunycode()
    {
        (_, string site, _) = LinkPreviewer.ParseHead("<title>Sign in</title>", new Uri("https://www.gооgle.com/"))!.Value;

        Assert.AreEqual("xn--ggle-55da.com", site);
    }

    [TestMethod]
    public void ParseHead_DecodesEntities_NoMarkup()
    {
        (string title, _, _) = LinkPreviewer.ParseHead("<title>Tom &amp; <b>Jerry</b> &#8211; &quot;Hi&quot;</title>", Page)!.Value;

        Assert.AreEqual("Tom & Jerry \u2013 \"Hi\"", title);
    }

    [TestMethod]
    public void ParseHead_StripsBidiControls_CapsLength()
    {
        string long_title    = new('a', 500);
        (string title, _, _) = LinkPreviewer.ParseHead($"<title>Pay\u202Egnp.exe {long_title}</title>", Page)!.Value;

        Assert.IsFalse(title.Contains('\u202E', StringComparison.Ordinal), title);
        StringAssert.StartsWith(title, "Paygnp.exe aaa");
        Assert.AreEqual(301, title.Length);
        Assert.IsTrue(title.EndsWith('\u2026'));
    }

    [TestMethod]
    public void ParseHead_UnsafeImage_Dropped()
    {
        (_, _, Uri? image) = LinkPreviewer.ParseHead("<title>T</title><meta property=\"og:image\" content=\"http://10.0.0.1/x.png\">", Page)!.Value;

        Assert.IsNull(image);
    }

    [TestMethod]
    public void ParseHead_NoTitle_Null() => Assert.IsNull(LinkPreviewer.ParseHead("<html><body>nothing</body></html>", Page));

    // =========================================================================
    // FETCHING
    // =========================================================================

    [TestMethod]
    public async Task Get_Html_GivesPreview_WithImage()
    {
        byte[] png   = [1, 2, 3];
        var handler  = new FakeHttpHandler
        {
            Respond = (_, r) => r.Url.EndsWith(".png", StringComparison.Ordinal)
                ? FakeHttpHandler.Response(HttpStatusCode.OK, "image/png", png)
                : HtmlResponse(Html.Replace("</head>", "<meta property=\"og:image\" content=\"https://cdn.example.com/c.png\"></head>", StringComparison.Ordinal)),
        };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual(Page, preview.Url);
        Assert.AreEqual("Example Title", preview.Title);
        Assert.AreEqual("example.com", preview.Site);
        CollectionAssert.AreEqual(png, preview.Image);
        Assert.AreEqual(GmConstants.UserAgent, handler.Requests[0].Headers["User-Agent"]);
        Assert.IsFalse(handler.Requests[0].Headers.ContainsKey("Cookie"));
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
    public async Task Get_SecondCall_Cached()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger(), TestProxy.Direct);

        LinkPreview? first  = await previewer.GetAsync(Page, CancellationToken.None);
        LinkPreview? second = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, handler.Requests.Count);
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
    /// Review fix: a link the system would send through a proxy gets no preview, so the user's proxy is never bypassed.
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
/// A system proxy for tests: one that sends everything through a proxy, or <see cref="Direct"/> for none.
/// </summary>
/// <param name="via">The proxy, or null to connect directly.</param>
internal sealed class TestProxy(Uri? via) : IWebProxy
{
    /// <summary>Gets a proxy setting that connects directly.</summary>
    public static TestProxy Direct { get; } = new(null);

    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => via ?? destination;

    public bool IsBypassed(Uri host) => via is null;
}
