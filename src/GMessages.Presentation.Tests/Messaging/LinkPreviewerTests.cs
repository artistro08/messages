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
    public void IsPublic_RefusesPrivateAddresses(string address)
    {
        Assert.IsFalse(LinkPreviewer.IsPublic(IPAddress.Parse(address)));
    }

    [TestMethod]
    [DataRow("8.8.8.8")]
    [DataRow("172.32.0.1")]
    [DataRow("100.128.0.1")]
    [DataRow("2606:4700::1111")]
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
        Assert.AreEqual("Site Name", site);
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

    [TestMethod]
    public void ParseHead_DecodesEntities_NoMarkup()
    {
        (string title, _, _) = LinkPreviewer.ParseHead("<title>Tom &amp; <b>Jerry</b> &#8211; &quot;Hi&quot;</title>", Page)!.Value;

        Assert.AreEqual("Tom & Jerry \u2013 \"Hi\"", title);
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
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.AreEqual(Page, preview.Url);
        Assert.AreEqual("Example Title", preview.Title);
        Assert.AreEqual("Example", preview.Site);
        CollectionAssert.AreEqual(png, preview.Image);
        Assert.AreEqual(GmConstants.UserAgent, handler.Requests[0].Headers["User-Agent"]);
        Assert.IsFalse(handler.Requests[0].Headers.ContainsKey("Cookie"));
    }

    [TestMethod]
    public async Task Get_NotHtml_Null()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => FakeHttpHandler.Response(HttpStatusCode.OK, "application/json", "{}"u8.ToArray()) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));
    }

    [TestMethod]
    public async Task Get_TooLarge_Null()
    {
        string big  = Html + new string('x', LinkPreviewer.MaxPageBytes);
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(big) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

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
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

        LinkPreview? preview = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.IsNotNull(preview);
        Assert.IsNull(preview.Image);
    }

    [TestMethod]
    public async Task Get_SecondCall_Cached()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

        LinkPreview? first  = await previewer.GetAsync(Page, CancellationToken.None);
        LinkPreview? second = await previewer.GetAsync(Page, CancellationToken.None);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Get_UnsafeUrl_NoRequest()
    {
        var handler = new FakeHttpHandler { Respond = (_, _) => HtmlResponse(Html) };
        using var previewer = new LinkPreviewer(handler, false, new CollectingLogger());

        Assert.IsNull(await previewer.GetAsync(new Uri("https://192.168.1.1/"), CancellationToken.None));
        Assert.AreEqual(0, handler.Requests.Count);
    }

    [TestMethod]
    public async Task Get_Failure_LogsTypeNotUrl()
    {
        var handler = new FakeHttpHandler { Respond = (_, r) => throw new HttpRequestException("boom " + r.Url) };
        var log     = new CollectingLogger();
        using var previewer = new LinkPreviewer(handler, false, log);

        Assert.IsNull(await previewer.GetAsync(Page, CancellationToken.None));

        string all = string.Join("\n", log.Messages);
        StringAssert.Contains(all, nameof(HttpRequestException));
        Assert.IsFalse(all.Contains("example.com", StringComparison.OrdinalIgnoreCase), all);
    }
}
