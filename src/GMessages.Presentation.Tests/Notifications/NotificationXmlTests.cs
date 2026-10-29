using System.Xml.Linq;
using GMessages.Presentation.Notifications;

namespace GMessages.Presentation.Tests.Notifications;

[TestClass]
public sealed class NotificationXmlTests
{
    private const string Send = "file:///send.png";

    private static XDocument Doc(string title = "Ann", string body = "hi", string? avatar = null) =>
        XDocument.Parse(NotificationXml.Build(new NotificationContent("m1", "c1", title, body, "p1"), avatar, Send));

    [TestMethod]
    public void Build_EscapesMarkupInTitleAndBody()
    {
        const string body = "a <b> & \"c\"";
        var texts = Doc("<t>&", body).Descendants("text").ToList();

        Assert.AreEqual(2, texts.Count);
        Assert.AreEqual("<t>&", texts[0].Value);
        Assert.AreEqual(body, texts[1].Value);
        Assert.IsFalse(Doc(body: body).Descendants("b").Any());
    }

    [TestMethod]
    public void Build_HasSystemSnoozeAndDismiss()
    {
        var actions = Doc().Descendants("action").ToList();

        Assert.IsTrue(actions.Any(a => (string?)a.Attribute("activationType") == "system" && (string?)a.Attribute("arguments") == "snooze" && (string?)a.Attribute("hint-inputId") == "snoozeTime"));
        Assert.IsTrue(actions.Any(a => (string?)a.Attribute("activationType") == "system" && (string?)a.Attribute("arguments") == "dismiss"));
        Assert.AreEqual(4, Doc().Descendants("selection").Count());
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
    public void Args_RoundTrip() => Assert.AreEqual(new NotificationArgs("reply", "c 1", "m&2"), NotificationArgs.Parse("action=reply&conversation=c%201&message=m%262"));

    [TestMethod]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("snooze")]
    [DataRow("action=open")]
    public void Args_Garbage_ReturnsNull(string s) => Assert.IsNull(NotificationArgs.Parse(s));
}
