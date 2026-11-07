using System.Xml.Linq;
using GMessages.Presentation.Messaging;
using GMessages.Presentation.Notifications;

namespace GMessages.Presentation.Tests.Notifications;

[TestClass]
public sealed class NotificationXmlTests
{
    private const string Send = "file:///send.png";

    private static XDocument Doc(string title = "Ann", string body = "hi", string? avatar = null) =>
        XDocument.Parse(NotificationXml.Build(new NotificationContent("m1", "c1", title, body, "p1"), avatar, Send));

    /// <summary>
    /// A toast with a copied one-time code says so in the small attribution line of Windows under the text.
    /// Any other toast has no such line.
    /// </summary>
    [TestMethod]
    public void Build_CodeCopied_SaysSoUnderTheText()
    {
        var content = new NotificationContent("m1", "c1", "Bank", "Your code is 123456", "p1");

        XElement? copied = XDocument.Parse(NotificationXml.Build(content with { CodeCopied = true }, null, Send)).Descendants("text")
            .SingleOrDefault(t => (string?)t.Attribute("placement") == "attribution");
        Assert.AreEqual(ChatCopy.CodeCopied, copied?.Value);

        Assert.IsFalse(XDocument.Parse(NotificationXml.Build(content, null, Send)).Descendants("text").Any(t => t.Attribute("placement") is not null));
    }

    /// <summary>
    /// The toast escapes markup characters in the title and the body, so they show as text.
    /// </summary>
    [TestMethod]
    public void Build_EscapesMarkupInTitleAndBody()
    {
        const string Body = "a <b> & \"c\"";
        var texts = Doc("<t>&", Body).Descendants("text").ToList();

        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("<t>&", texts[0].Value);
        Assert.AreEqual(Body, texts[1].Value);
        Assert.IsFalse(Doc(body: Body).Descendants("b").Any());
    }

    [TestMethod]
    public void Build_StripsXmlIllegalCharacters_KeepsEmoji()
    {
        var texts = Doc("A\u0001nn\uD800", "\u0000h\u001Fi \uD83D\uDE00\uFFFE").Descendants("text").ToList();

        Assert.AreEqual("Ann", texts[0].Value);
        Assert.AreEqual("hi \uD83D\uDE00", texts[1].Value);
    }

    [TestMethod]
    public void Build_HasAppSnoozeAndSystemDismiss_NoSnoozePicker()
    {
        var actions = Doc().Descendants("action").ToList();

        // The snooze button belongs to the app. Its arguments parse back to the conversation and message of the toast.
        XElement snooze = actions.Single(a => NotificationArgs.Parse((string?)a.Attribute("arguments") ?? "")?.Action == "snooze");
        Assert.AreEqual(new NotificationArgs("snooze", "c1", "m1"), NotificationArgs.Parse((string)snooze.Attribute("arguments")!));
        Assert.AreEqual("Snooze", (string?)snooze.Attribute("content"));
        Assert.AreEqual("foreground", (string?)snooze.Attribute("activationType"));
        Assert.IsNull(snooze.Attribute("hint-inputId"));

        Assert.IsTrue(actions.Any(a => (string?)a.Attribute("activationType") == "system" && (string?)a.Attribute("arguments") == "dismiss"));
        Assert.IsFalse(Doc().Descendants("selection").Any());
        Assert.AreEqual("reply", (string?)Doc().Descendants("input").Single().Attribute("id"));
    }

    [TestMethod]
    public void Build_ReplyIsWindowsQuickReply()
    {
        // This is the quick reply of Microsoft. The button links to the text input with hint-inputId.
        // The icon of the button comes from imageUri.
        XElement reply = Doc().Descendants("action").First();

        Assert.AreEqual("reply", (string?)reply.Attribute("hint-inputId"));
        Assert.AreEqual(Send, (string?)reply.Attribute("imageUri"));
        Assert.AreEqual("Send", (string?)reply.Attribute("hint-toolTip"));
    }

    [TestMethod]
    public void Build_InputsComeBeforeActions()
    {
        // The toast schema is a sequence. Every input must come before any action.
        // Otherwise Windows shows "New notification" instead of the toast.
        var names = Doc().Descendants("actions").Single().Elements().Select(e => e.Name.LocalName).ToList();

        Assert.AreEqual(names.LastIndexOf("input") + 1, names.IndexOf("action"));
    }

    [TestMethod]
    public void Build_NoAvatar_HasNoImage() => Assert.IsFalse(Doc().Descendants("image").Any());

    [TestMethod]
    public void Build_Avatar_HasCircleImage()
    {
        XElement image = Doc(avatar: "file:///a.png").Descendants("image").Single();

        Assert.AreEqual("file:///a.png", (string?)image.Attribute("src"));
        Assert.AreEqual("circle", (string?)image.Attribute("hint-crop"));
    }

    [TestMethod]
    public void Build_ReplyAndLaunchArgs_ParseBack()
    {
        XDocument doc = Doc();
        var launch    = NotificationArgs.Parse((string)doc.Root!.Attribute("launch")!);
        var reply     = NotificationArgs.Parse((string)doc.Descendants("action").First().Attribute("arguments")!);

        Assert.AreEqual(new NotificationArgs("open", "c1", null), launch);
        Assert.AreEqual(new NotificationArgs("reply", "c1", "m1"), reply);
    }

    [TestMethod]
    public void Build_All_MatchesThePlan3Shape()
    {
        // The original toast design, character for character. The order is the quick reply with a text input and a
        // send button, then the snooze button of the app, then the dismiss button of Windows.
        const string Expected = "<toast launch=\"action=open&amp;conversation=c1\"><visual><binding template=\"ToastGeneric\"><text>Ann</text><text>hi</text></binding></visual>"
            + "<actions><input id=\"reply\" type=\"text\" placeHolderContent=\"Reply\" />"
            + "<action content=\"\" hint-inputId=\"reply\" activationType=\"foreground\" arguments=\"action=reply&amp;conversation=c1&amp;message=m1\" imageUri=\"file:///send.png\" hint-toolTip=\"Send\" />"
            + "<action activationType=\"foreground\" arguments=\"action=snooze&amp;conversation=c1&amp;message=m1\" content=\"Snooze\" />"
            + "<action activationType=\"system\" arguments=\"dismiss\" content=\"\" /></actions></toast>";
        var content           = new NotificationContent("m1", "c1", "Ann", "hi", "p1");

        Assert.AreEqual(Expected, NotificationXml.Build(content, null, Send));
    }

    [TestMethod]
    public void BuildReplyFailed_TitleBodyAndOpenLaunch()
    {
        XDocument doc = XDocument.Parse(NotificationXml.BuildReplyFailed("c 1", "Ann\u0001"));
        var texts     = doc.Descendants("text").Select(t => t.Value).ToList();

        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("Ann", texts[0]);
        Assert.AreEqual("Your reply wasn't sent. Open the chat to try again.", texts[1]);
        Assert.AreEqual(new NotificationArgs("open", "c 1", null), NotificationArgs.Parse((string)doc.Root!.Attribute("launch")!));
        Assert.IsFalse(doc.Descendants("actions").Any());
    }

    [TestMethod]
    public void Args_RoundTrip() => Assert.AreEqual(new NotificationArgs("reply", "c 1", "m&2"), NotificationArgs.Parse("action=reply&conversation=c%201&message=m%262"));

    [TestMethod]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("snooze")]
    [DataRow("action=open")]
    public void Args_Garbage_ReturnsNull(string s) => Assert.IsNull(NotificationArgs.Parse(s));
}
