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

    // =========================================================================
    // RIG
    // =========================================================================

    // A View Model On A Rig Controller That Records Every Step It Moves Through
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

    // =========================================================================
    // FAILURE MESSAGES
    // =========================================================================

    /// <summary>
    /// Every pairing failure maps to the owner's exact copy.
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
    /// 401 and 403 mean Google refused the sign-in; any other status is the generic message.
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
    /// Anything else (network, protocol) is the generic message.
    /// </summary>
    [TestMethod]
    public void MessageFor_OtherError_IsGeneric() =>
        Assert.AreEqual(Generic, PairingViewModel.MessageFor(new HttpRequestException("offline")));

    // =========================================================================
    // STEPS
    // =========================================================================

    /// <summary>
    /// A successful pairing moves SigningIn, WaitingForPhone, ShowingEmoji, Done and keeps the emoji.
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
    /// A 401 from SignInGaia (stale cookies) ends Failed with the sign-in message.
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
    /// A sign-in page that can't load (no WebView2 runtime) ends Failed with the generic message; Try again resets.
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

    // =========================================================================
    // SIGNED-OUT REASON
    // =========================================================================

    /// <summary>
    /// After Google signs the user out, the reason is shown above the sign-in.
    /// </summary>
    [TestMethod]
    public async Task SignedOut_ShowsReason()
    {
        await using var rig = new Rig();
        rig.SaveSession(TimeSpan.FromMinutes(30));
        rig.Respond = req => req.Url.EndsWith("/RegisterRefresh", StringComparison.Ordinal) ? FakeHttpHandler.Response(HttpStatusCode.Unauthorized) : null;
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);

        var view_model = new PairingViewModel(rig.Controller, a => a());

        Assert.AreEqual("You were signed out. Sign in again to keep getting texts.", view_model.SignedOutReason);
        Assert.IsTrue(view_model.ShowSignedOutReason);
    }

    /// <summary>
    /// A first-time pairing has no reason to show.
    /// </summary>
    [TestMethod]
    public async Task NotPaired_HasNoReason()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync().WaitAsync(Shared.HangGuard);

        var view_model = new PairingViewModel(rig.Controller, a => a());

        Assert.IsNull(view_model.SignedOutReason);
        Assert.IsFalse(view_model.ShowSignedOutReason);
    }
}
