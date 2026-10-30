using System.IO.Compression;
using FlaUI.Core.AutomationElements;
using GMessages.App.UITests.Driver;
using GMessages.Presentation;
using GMessages.Presentation.Messaging;
using GMessages.Protocol.Proto;

namespace GMessages.App.UITests;

/// <summary>
/// Photos, videos and files in the chat: the photo bubble (blurred, then sharp), its reserved size, its failure and
/// retry, its caption, and the file card that opens in its app.
/// </summary>
[TestClass]
[TestCategory("UI")]
[DoNotParallelize]
public sealed class MediaTests : UiTest
{
    // The Photo Bubble's Load States, As The Test Hooks Report Them In Its Item Status
    private const string Blurred = "Blurred";
    private const string Full    = "Full";
    private const string Failed  = "Failed";

    /// <summary>
    /// The seeded photo in Mia's chat shows: its bubble loads to the full image, and the image is on screen (blue).
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ShowsImage()
    {
        await OpenMediaChatAsync();

        AutomationElement? photo = null;
        Wait.Until(() => (photo = FirstPhoto()) is not null, AppDriver.DefaultTimeout, () => "Mia's chat showed no photo.");
        WaitForStatus(photo!, Full);
        Assert.AreEqual(ChatCopy.Photo, AppDriver.SafeName(photo), "The photo without a caption isn't named Photo.");

        System.Drawing.Color center = CenterPixel(photo!);
        Assert.IsTrue(center.B > 150 && center.R < 100 && center.G < 100, $"The photo's middle reads {center}, not the photo's blue.");
    }

    /// <summary>
    /// A thumbnail-only photo shows blurred first, asks the phone for its full size, and turns sharp when the phone sends
    /// it (the message edit swaps its media). The bubble keeps the photo's shape from its dimensions throughout.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ThumbnailThenFull()
    {
        await OpenMediaChatAsync();
        Server.Phone.FullSizeDelay = TimeSpan.FromSeconds(2);

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(300, 200, SplitColors), "split.png", withThumbnail: true);
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Blurred);
        WaitForStatus(photo, Full);

        System.Drawing.Rectangle box = photo.BoundingRectangle;
        Assert.AreEqual(1.5, (double)box.Width / box.Height, 0.03, $"The photo's box {box} doesn't keep the photo's 3:2 shape.");
    }

    /// <summary>
    /// A photo that arrives while its download fails already takes its full size in the chat, so when it does load (after
    /// a retry) nothing moves and the chat stays on its newest message.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_ReservesHeight_NoJump()
    {
        await OpenMediaChatAsync();
        Server.FailMediaDownloads = true;

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(400, 300, SplitColors), "tall.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Failed);
        Thread.Sleep(500);
        System.Drawing.Rectangle before = photo.BoundingRectangle;
        Assert.AreEqual(4.0 / 3, (double)before.Width / before.Height, 0.03, $"The unloaded photo's box {before} isn't the photo's 4:3 shape.");

        // Loads On Retry, In The Same Place
        Server.FailMediaDownloads = false;
        photo.Click();
        WaitForStatus(photo, Full);
        Thread.Sleep(500);
        System.Drawing.Rectangle after = photo.BoundingRectangle;
        Assert.AreEqual(before, after, "The photo moved or changed size when it loaded.");
        AssertAtNewest("Loading the photo");
    }

    /// <summary>
    /// A photo whose download fails says so over its placeholder, and a click tries again and shows it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedPhoto_DownloadFails_ShowsRetry()
    {
        await OpenMediaChatAsync();
        Server.FailMediaDownloads = true;

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(64, 64, SplitColors), "small.png");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        WaitForStatus(photo, Failed);
        Wait.Until(() => HasText(photo, ChatCopy.PhotoLoadFailed), AppDriver.DefaultTimeout, () => $"'{ChatCopy.PhotoLoadFailed}' didn't show on the photo.");

        Server.FailMediaDownloads = false;
        photo.Click();
        WaitForStatus(photo, Full);
        Assert.IsFalse(HasText(photo, ChatCopy.PhotoLoadFailed), "The failure note stayed after the photo loaded.");
    }

    /// <summary>
    /// A photo sent with a caption shows both in one bubble: the photo, named by the caption, with the caption under it.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task PhotoWithCaption_ShowsBoth()
    {
        await OpenMediaChatAsync();

        Message message         = Server.Phone.PushIncomingMedia("c-media", "image/png", Png(200, 200, SplitColors), "beach.png", caption: "At the beach");
        AutomationElement photo = App.Find("Bubble.Photo." + message.MessageID);
        AutomationElement text  = App.Find("Bubble." + message.MessageID);
        WaitForStatus(photo, Full);

        Assert.AreEqual("At the beach", AppDriver.SafeName(text), "The caption isn't the bubble's text.");
        Assert.AreEqual("At the beach", AppDriver.SafeName(photo), "The photo isn't named by its caption.");
        Assert.IsTrue(text.BoundingRectangle.Top >= photo.BoundingRectangle.Bottom, $"The caption ({text.BoundingRectangle}) isn't under the photo ({photo.BoundingRectangle}).");
    }

    /// <summary>
    /// A received file shows as a card with its name and size; a click downloads it, writes it to the private temp folder
    /// and opens it (the test hook notes the path instead), and the file holds the bytes sent.
    /// </summary>
    /// <returns>A task for the test.</returns>
    [TestMethod]
    public async Task ReceivedFile_Card_OpensDownloaded()
    {
        await OpenMediaChatAsync();
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 notes for the trip");

        Message message        = Server.Phone.PushIncomingMedia("c-media", "application/pdf", bytes, "notes.pdf");
        AutomationElement card = App.Find("Bubble.File." + message.MessageID);
        Assert.AreEqual("notes.pdf", AppDriver.SafeName(card), "The card isn't named by the file name.");
        Wait.Until(() => HasText(card, "27 bytes"), AppDriver.DefaultTimeout, () => "The card doesn't show the file's size.");

        card.Click();
        string launched = Path.Combine(App.DataDir, "launched.txt");
        Wait.Until(() => File.Exists(launched), AppDriver.DefaultTimeout, () => "Clicking the card opened nothing.");
        string path = File.ReadAllLines(launched)[^1];
        Assert.AreEqual("notes.pdf", Path.GetFileName(path), "The opened file lost its name.");
        Assert.IsTrue(path.StartsWith(App.DataDir, StringComparison.OrdinalIgnoreCase), $"The file was written outside the app's private folder: {path}.");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), "The opened file doesn't hold the bytes sent.");
        Wait.Until(() => card.Properties.ItemStatus.ValueOrDefault == "Ready" && HasText(card, "27 bytes"), AppDriver.DefaultTimeout,
            () => $"The card stayed '{card.Properties.ItemStatus.ValueOrDefault}' after opening.");
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    // Two Halves: Red Left, Blue Right
    private static (byte R, byte G, byte B) SplitColors(int x, int y, int width) => x < width / 2 ? ((byte)220, (byte)40, (byte)40) : ((byte)40, (byte)60, (byte)220);

    // Seeds Mia's Photo Chat, Pairs, Opens The Main Window And Then Her Chat
    private async Task OpenMediaChatAsync()
    {
        Server.Phone.Chats.AddPhotoChat();
        await OpenMainAsync();
        App.Find("Row.c-media").Patterns.SelectionItem.Pattern.Select();
        App.Find("Chat.Draft");
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
    }

    // The Chat's First Photo, Or Null
    private AutomationElement? FirstPhoto() =>
        App.TryFind("Chat.Messages") is { } list
            ? AppDriver.Search(list, e => e.Properties.AutomationId.ValueOrDefault?.StartsWith("Bubble.Photo.", StringComparison.Ordinal) == true)
            : null;

    // Waits For The Photo's Load State (Its Item Status Under The Test Hooks)
    private static void WaitForStatus(AutomationElement photo, string status) =>
        Wait.Until(() => photo.Properties.ItemStatus.ValueOrDefault == status, AppDriver.DefaultTimeout,
            () => $"The photo stayed '{photo.Properties.ItemStatus.ValueOrDefault}', not '{status}'.");

    // Whether Any Text Under The Element Reads This
    private static bool HasText(AutomationElement root, string text) => AppDriver.Search(root, e => AppDriver.SafeName(e) == text) is not null;

    // The Screen Pixel In The Middle Of An Element, Read Once Its Fade Has Finished
    private System.Drawing.Color CenterPixel(AutomationElement element)
    {
        App.WaitForWindow(AppInfo.DisplayName, AppDriver.DefaultTimeout).SetForeground();
        Thread.Sleep(500);
        System.Drawing.Rectangle box = element.BoundingRectangle;
        using FlaUI.Core.Capturing.CaptureImage capture = FlaUI.Core.Capturing.Capture.Rectangle(new System.Drawing.Rectangle(box.X + (box.Width / 2), box.Y + (box.Height / 2), 1, 1));
        return capture.Bitmap.GetPixel(0, 0);
    }

    // The Message List Sits On Its Newest Message: Less Than A Pixel Short Of Its End
    private void AssertAtNewest(string what)
    {
        AutomationElement list = App.Find("Chat.Messages");
        var scroll             = list.Patterns.Scroll.Pattern;
        double view_size       = scroll.VerticalViewSize.ValueOrDefault;
        double viewport        = list.BoundingRectangle.Height;
        double scrollable      = view_size is > 0 and < 100 ? viewport * 100 / view_size - viewport : 0;
        double short_by        = (100 - scroll.VerticalScrollPercent.ValueOrDefault) / 100 * scrollable;
        Assert.IsTrue(short_by < 1, $"{what} left the list {short_by:0.0} px short of the newest message.");
    }

    /// <summary>
    /// Makes a PNG of the given size (8-bit RGB, stored with zlib), each pixel colored by a function.
    /// </summary>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="color">Gives a pixel's color from its x, its y and the width.</param>
    /// <returns>The PNG file's bytes.</returns>
    internal static byte[] Png(int width, int height, Func<int, int, int, (byte R, byte G, byte B)> color)
    {
        // Rows: A Filter Byte (None), Then RGB
        byte[] raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0, at = 0; y < height; y++)
        {
            raw[at++] = 0;
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = color(x, y, width);
                raw[at++] = r;
                raw[at++] = g;
                raw[at++] = b;
            }
        }

        using var data = new MemoryStream();
        using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", data.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream png, string type, byte[] body)
        {
            byte[] length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. body];
            byte[] crc   = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.Write(length);
            png.Write(typed);
            png.Write(crc);
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            }

            return ~crc;
        }
    }
}
