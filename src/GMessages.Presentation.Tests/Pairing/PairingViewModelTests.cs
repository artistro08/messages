using System.Collections.Concurrent;
using System.Net;
using GMessages.Presentation.Connection;
using GMessages.Presentation.Pairing;
using GMessages.Protocol.Http;
using GMessages.Protocol.Pairing;
using GMessages.Protocol.Proto;
using GMessages.Protocol.Tests.Fakes;
using Google.Protobuf;
using Rig = GMessages.Presentation.Tests.Connection.ConnectionControllerTests.Rig;
using Shared = GMessages.Presentation.Tests.Connection.ConnectionControllerTests;

namespace GMessages.Presentation.Tests.Pairing;

/// <summary>
/// Checks the pairing window's view model: its steps, its plain-English failure messages, Try again, Cancel and the
/// signed-out reason, against the same fake server and fake phone as the connection controller tests.
/// </summary>
[TestClass]
public sealed class PairingViewModelTests
{
    private const string NotAccepted = "Google didn't accept the sign-in. Try again.";
    private const string Generic     = "Something went wrong while pairing. Try again.";

    // A view model on a rig controller that records every step it moves through.
    private sealed class Watched
    {
        public Watched(Rig rig)
        {
            ViewModel = new PairingViewModel(rig.Controller, a => a());
            ViewModel.PropertyChanged += (_, _) =>
            {
                if (Steps.LastOrDefault() != ViewModel.Step)
                {
                    Steps.Enqueue(ViewModel.Step);
                }
            };
        }

        public PairingViewModel ViewModel { get; }

        public ConcurrentQueue<PairingStep> Steps { get; } = new();
    }

    /// <summary>
    /// Every pairing failure maps to the exact text that the owner wrote.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="expected">The exact text shown.</param>
    [TestMethod]
    [DataRow(PairingFailure.IncorrectEmoji, "That wasn't the matching emoji. Try again.")]
    [DataRow(PairingFailure.Canceled, "Pairing on phone was cancelled")]
    [DataRow(PairingFailure.NotMe, "Pairing was declined on your phone.")]
    [DataRow(PairingFailure.Timeout, "Your phone didn't respond in time. Make sure Google Messages is open on your phone, then try again.")]
    [DataRow(PairingFailure.InitTimeout, "Your phone didn't respond in time. Make sure Google Messages is open on your phone, then try again.")]
    [DataRow(PairingFailure.NoDevicesFound, "No phone with Google Messages was found on this Google account.")]
    [DataRow(PairingFailure.PhoneNotFound, "No phone with Google Messages was found on this Google account.")]
    [DataRow(PairingFailure.NoCookies, NotAccepted)]
    [DataRow(PairingFailure.Unknown, Generic)]
    public void MessageFor_PairingFailure_IsExactCopy(PairingFailure failure, string expected) =>
        Assert.AreEqual(expected, PairingViewModel.MessageFor(new PairingException(failure, "go text")));

    /// <summary>
    /// A 401 or 403 means Google refused the sign-in. Any other status gives the generic message.
    /// </summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="expected">The exact text shown.</param>
    [TestMethod]
    [DataRow(401, NotAccepted)]
    [DataRow(403, NotAccepted)]
    [DataRow(500, Generic)]
    public void MessageFor_RequestError_IsExactCopy(int status, string expected) =>
        Assert.AreEqual(expected, PairingViewModel.MessageFor(new GmRequestException(status, null, [])));

    /// <summary>
    /// Any other failure, such as a network or protocol error, gives the generic message.
    /// </summary>
    [TestMethod]
    public void MessageFor_OtherError_IsGeneric() =>
        Assert.AreEqual(Generic, PairingViewModel.MessageFor(new HttpRequestException("offline")));

    /// <summary>
    /// A successful pairing moves through SigningIn, WaitingForPhone, ShowingEmoji and Done. It keeps the emoji.
    /// </summary>
    [TestMethod]
    public async Task Pair_Success_MovesThroughEverySteps()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, new GaiaPairingResponseContainer().ToByteArray());
        var watched = new Watched(rig);
        Assert.AreEqual(PairingStep.SigningIn, watched.ViewModel.Step);

        await watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies).WaitAsync(Shared.HangGuard);

        CollectionAssert.AreEqual(new[] { PairingStep.WaitingForPhone, PairingStep.ShowingEmoji, PairingStep.Done }, watched.Steps.ToArray());
        Assert.IsFalse(string.IsNullOrEmpty(watched.ViewModel.Emoji), "emoji shown");
        Assert.IsNull(watched.ViewModel.Message);
        Assert.AreEqual(ConnectionState.Connected, rig.Controller.State);
    }

    /// <summary>
    /// A wrong emoji tap ends Failed with its message and no emoji.
    /// </summary>
    [TestMethod]
    public async Task Pair_WrongEmoji_IsFailedWithMessage()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected }.ToByteArray());
        var watched = new Watched(rig);

        await watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies).WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.Failed, watched.ViewModel.Step);
        Assert.AreEqual("That wasn't the matching emoji. Try again.", watched.ViewModel.Message);
        Assert.IsNull(watched.ViewModel.Emoji);
        Assert.IsTrue(watched.ViewModel.IsFailed);
    }

    /// <summary>
    /// A 401 from SignInGaia, which means stale cookies, ends Failed with the sign-in message.
    /// </summary>
    [TestMethod]
    public async Task Pair_SignInUnauthorized_IsFailedWithSignInMessage()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, finished: null);
        Func<RecordedRequest, HttpResponseMessage?> scripted = rig.Respond!;
        rig.Respond = req => req.Url.EndsWith("/SignInGaia", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : scripted(req);
        var watched = new Watched(rig);

        await watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies).WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.Failed, watched.ViewModel.Step);
        Assert.AreEqual(NotAccepted, watched.ViewModel.Message);
    }

    /// <summary>
    /// Try again after a failure goes back to signing in with no message or emoji.
    /// </summary>
    [TestMethod]
    public async Task TryAgain_AfterFailure_ResetsToSigningIn()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, new GaiaPairingResponseContainer { FinishErrorType = 1, FinishErrorCode = GaiaPairingErrorCode.WrongVerificationCodeSelected }.ToByteArray());
        var watched = new Watched(rig);
        await watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies).WaitAsync(Shared.HangGuard);

        watched.ViewModel.TryAgain();

        Assert.AreEqual(PairingStep.SigningIn, watched.ViewModel.Step);
        Assert.IsTrue(watched.ViewModel.IsSigningIn);
        Assert.IsNull(watched.ViewModel.Message);
        Assert.IsNull(watched.ViewModel.Emoji);
    }

    /// <summary>
    /// Cancel while the emoji waits for a tap stops the pairing without showing a failure.
    /// </summary>
    [TestMethod]
    public async Task Cancel_WhileShowingEmoji_StopsWithoutFailure()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, finished: null);
        var watched = new Watched(rig);

        Task pairing = watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies);
        Shared.WaitUntil(() => watched.ViewModel.Step == PairingStep.ShowingEmoji, "emoji shown");
        watched.ViewModel.Cancel();
        await pairing.WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.ShowingEmoji, watched.ViewModel.Step, "no failure shown");
        Assert.IsNull(watched.ViewModel.Message);
        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
        Assert.IsNull(rig.Controller.CurrentClient);
    }

    /// <summary>
    /// An unpair while the emoji waits is no failure. The controller cancels the pairing. The window goes back to the
    /// sign-in without "Something went wrong".
    /// </summary>
    [TestMethod]
    public async Task UnpairDuringPairing_IsNotGenericFailure()
    {
        await using var rig = new Rig();
        Directory.CreateDirectory(rig.DataFolder);
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, finished: null);
        var watched = new Watched(rig);

        Task pairing = watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies);
        Shared.WaitUntil(() => watched.ViewModel.Step == PairingStep.ShowingEmoji, "emoji shown");
        await rig.Controller.UnpairAsync().WaitAsync(Shared.HangGuard);
        await pairing.WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.SigningIn, watched.ViewModel.Step);
        Assert.IsNull(watched.ViewModel.Message);
        Assert.IsFalse(watched.Steps.Contains(PairingStep.Failed), "no failure shown");
        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
    }

    /// <summary>
    /// After a sign-out, an unpair cancels the pairing while the emoji waits. The window goes back to the sign-in and
    /// shows no failure, also while the unpair still waits for the gate.
    /// </summary>
    [TestMethod]
    public async Task UnpairDuringPairing_AfterSignOut_IsNotGenericFailure()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromMinutes(30));
        rig.Respond = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Assert.AreEqual(ConnectionState.SignedOut, rig.Controller.State);
        Shared.ScriptPairing(rig, finished: null);
        var watched = new Watched(rig);

        Task pairing = watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies);
        Shared.WaitUntil(() => watched.ViewModel.Step == PairingStep.ShowingEmoji, "emoji shown");

        // A start on a locked session file takes the gate before the unpair. The start holds the gate on its 1 s wait.
        File.WriteAllBytes(rig.Paths.SessionFile, []);
        var locked     = new FileStream(rig.Paths.SessionFile, FileMode.Open, FileAccess.Read, FileShare.None);
        int retryWaits = rig.Time.CountTimers(TimeSpan.FromSeconds(1));
        Task start     = rig.Controller.StartAsync();
        Task unpair    = rig.Controller.UnpairAsync();

        // The page settles while the unpair still waits for the gate.
        await pairing.WaitAsync(Shared.HangGuard);
        PairingStep step  = watched.ViewModel.Step;
        string? message   = watched.ViewModel.Message;
        bool failedShown  = watched.Steps.Contains(PairingStep.Failed);
        bool unpairWaited = !unpair.IsCompleted;
        bool startOnLock  = SpinWait.SpinUntil(() => rig.Time.CountTimers(TimeSpan.FromSeconds(1)) > retryWaits, Shared.HangGuard);

        // The test frees the gate before any assert, so the rig can dispose.
        await locked.DisposeAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await start.WaitAsync(Shared.HangGuard);
        await unpair.WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.SigningIn, step);
        Assert.IsNull(message);
        Assert.IsFalse(failedShown, "no failure shown");
        Assert.IsTrue(unpairWaited, "unpair still waited for the gate");
        Assert.IsTrue(startOnLock, "start waited on the lock");
        Assert.AreEqual(ConnectionState.NotPaired, rig.Controller.State);
    }

    /// <summary>
    /// A sign-in page that cannot load, because the WebView2 runtime is missing, ends Failed with the generic message.
    /// Try again resets it.
    /// </summary>
    [TestMethod]
    public async Task SignInFailed_IsFailedWithGenericMessage()
    {
        await using var rig = new Rig();
        var watched = new Watched(rig);

        watched.ViewModel.SignInFailed();

        Assert.AreEqual(PairingStep.Failed, watched.ViewModel.Step);
        Assert.AreEqual(Generic, watched.ViewModel.Message);
        watched.ViewModel.TryAgain();
        Assert.AreEqual(PairingStep.SigningIn, watched.ViewModel.Step);
    }

    /// <summary>
    /// Cookies captured twice in one attempt start only one pairing.
    /// </summary>
    [TestMethod]
    public async Task CookiesCaptured_Twice_PairsOnce()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);
        Shared.ScriptPairing(rig, new GaiaPairingResponseContainer().ToByteArray());
        var watched = new Watched(rig);

        Task first  = watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies);
        Task second = watched.ViewModel.OnCookiesCapturedAsync(Shared.PairCookies);
        await Task.WhenAll(first, second).WaitAsync(Shared.HangGuard);

        Assert.AreEqual(PairingStep.Done, watched.ViewModel.Step);
        Assert.HasCount(1, rig.Requests("/SignInGaia"));
    }

    /// <summary>
    /// After Google signs the user out, the sign-in shows the reason above it.
    /// </summary>
    [TestMethod]
    public async Task SignedOut_ShowsReason()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromMinutes(30));
        rig.Respond = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);

        var viewModel = new PairingViewModel(rig.Controller, a => a());

        Assert.AreEqual("You were signed out. Sign in again to keep getting texts.", viewModel.SignedOutReason);
        Assert.IsTrue(viewModel.ShowSignedOutReason);
    }

    /// <summary>
    /// A first-time pairing has no reason to show.
    /// </summary>
    [TestMethod]
    public async Task NotPaired_HasNoReason()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);

        var viewModel = new PairingViewModel(rig.Controller, a => a());

        Assert.IsNull(viewModel.SignedOutReason);
        Assert.IsFalse(viewModel.ShowSignedOutReason);
    }

    /// <summary>
    /// The pairing window draws the emoji from Noto Color Emoji images bundled with the app. The image names use the
    /// code points without VS16. Every emoji that either list can pick has an image.
    /// </summary>
    [TestMethod]
    public void EmojiImage_NamedByCodePoints_AndAllBundled()
    {
        Assert.AreEqual("emoji_u1f62e_200d_1f4a8.png", PairingViewModel.EmojiFileName("\U0001F62E\u200D\U0001F4A8"));
        Assert.AreEqual("emoji_u2764.png", PairingViewModel.EmojiFileName("\u2764\uFE0F"));

        string folder = Path.Combine(AppInfoTests.FindRepoRoot(), "src", "GMessages.App", "Assets", "Emoji");
        string[] missing = [.. PairingEmojis.V0.Concat(PairingEmojis.V1).Distinct(StringComparer.Ordinal)
            .Select(PairingViewModel.EmojiFileName)
            .Where(name => !File.Exists(Path.Combine(folder, name)))];
        Assert.AreEqual(0, missing.Length, "Missing: " + string.Join(", ", missing));
    }
}
