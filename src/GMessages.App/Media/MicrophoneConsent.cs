using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Media.Devices;

namespace GMessages.App.Media;

/// <summary>
/// Asks Windows for the microphone the way a desktop app can, which shows Windows' "Let Messages use your microphone?"
/// prompt the first time.
/// </summary>
/// <remarks>
/// <para>
/// <c>ActivateAudioInterfaceAsync</c> (mmdevapi) is the call Microsoft documents for desktop apps: it "may display a
/// consent prompt the first time it is called" to activate <c>IAudioClient</c> on a microphone, and "must be called on
/// the main UI thread so that the consent prompt can be shown"
/// (https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync).
/// <c>AppCapability.RequestAccessAsync</c> isn't supported in desktop apps, since it needs a CoreWindow
/// (https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-supported-api), which is why it
/// failed with <c>RPC_E_DISCONNECTED</c>.
/// </para>
/// <para>
/// Native AOT safe: the call is a <see cref="LibraryImportAttribute"/> and the two COM interfaces are source generated.
/// GUIDs and method order match <c>mmdeviceapi.h</c>, <c>audioclient.h</c> and <c>objidlbase.h</c> (SDK 10.0.26100.0).
/// </para>
/// </remarks>
internal static partial class MicrophoneConsent
{
    // IAudioClient (audioclient.h): The Interface Whose Activation On A Microphone Asks For Consent
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>
    /// Asks Windows for the default microphone, showing its consent prompt if the user hasn't decided yet.
    /// </summary>
    /// <remarks>
    /// Call it on the UI thread, so Windows can show the prompt. The task completes once the user answers (or at once
    /// when access is already decided). The activated audio client is released straight away; only the answer matters.
    /// </remarks>
    /// <returns>The activation's HRESULT: <c>S_OK</c> when access is given, <c>E_ACCESSDENIED</c> (0x80070005) when refused.</returns>
    /// <exception cref="MicrophoneUnavailableException">There is no microphone.</exception>
    public static async Task<int> RequestAsync()
    {
        // The Default Microphone; None Means No Device
        string device = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        if (string.IsNullOrEmpty(device))
        {
            throw new MicrophoneUnavailableException("NoDevice");
        }

        // Ask: Windows Shows Its Prompt, Then Calls The Handler On A Worker Thread
        var handler = new CompletionHandler();
        int called  = ActivateAudioInterfaceAsync(device, in AudioClientId, 0, handler, out IActivateAudioInterfaceAsyncOperation operation);
        Log("activate call", $"0x{called:X8}");
        if (called < 0)
        {
            return called;
        }

        int result = await handler.Result;

        // Windows Holds Both Until The Callback; Keep Them Alive Until Then Too
        GC.KeepAlive(handler);
        GC.KeepAlive(operation);
        return result;
    }

    /// <summary>
    /// Logs one step of the microphone request, with the thread it ran on (names and numbers only).
    /// </summary>
    /// <param name="step">The step, such as <c>check before</c> or <c>activate call</c>.</param>
    /// <param name="result">Its status name or HRESULT.</param>
    public static void Log(string step, string result)
    {
        string apartment = Thread.CurrentThread.GetApartmentState().ToString();
        AppLog.MicrophoneStep(App.ShellLog, step, result, Environment.CurrentManagedThreadId, apartment);
    }

    // =========================================================================
    // NATIVE INTEROP
    // =========================================================================

    // mmdeviceapi.h: STDAPI ActivateAudioInterfaceAsync(LPCWSTR, REFIID, PROPVARIANT*, Handler*, Operation**)
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        in Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>
    /// The handler Windows calls when the activation finishes (<c>mmdeviceapi.h</c>).
    /// </summary>
    [GeneratedComInterface]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    internal partial interface IActivateAudioInterfaceCompletionHandler
    {
        /// <summary>
        /// Called once the activation finishes.
        /// </summary>
        /// <param name="activateOperation">The finished activation, which holds its result.</param>
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>
    /// The activation in progress, and its result once finished (<c>mmdeviceapi.h</c>).
    /// </summary>
    [GeneratedComInterface]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    internal partial interface IActivateAudioInterfaceAsyncOperation
    {
        /// <summary>
        /// Gives the activation's result.
        /// </summary>
        /// <param name="activateResult">The activation's HRESULT.</param>
        /// <param name="activatedInterface">The activated interface (an <c>IUnknown</c>), or 0 when it failed.</param>
        void GetActivateResult(out int activateResult, out nint activatedInterface);
    }

    /// <summary>
    /// Marks an object as callable from any thread without marshaling (<c>objidlbase.h</c>).
    /// </summary>
    [GeneratedComInterface]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    internal partial interface IAgileObject;

    /// <summary>
    /// Completes <see cref="Result"/> with the activation's HRESULT. Agile, since Windows calls it on a worker thread.
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// The activation's HRESULT, once Windows calls back.
        /// </summary>
        public Task<int> Result => _result.Task;

        /// <inheritdoc />
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            int result;
            try
            {
                activateOperation.GetActivateResult(out result, out nint activated);

                // Only The Answer Matters: Let Go Of The Audio Client
                if (activated != 0)
                {
                    Marshal.Release(activated);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = ex.HResult;
            }

            Log("activate callback", $"0x{result:X8}");
            _result.TrySetResult(result);
        }
    }
}
