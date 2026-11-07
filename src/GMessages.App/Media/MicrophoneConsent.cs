using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Media.Devices;

namespace GMessages.App.Media;

/// <summary>
/// Asks Windows for the microphone in the way that a desktop app can. The first time, Windows shows its "Let Messages
/// use your microphone?" prompt.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft documents <c>ActivateAudioInterfaceAsync</c> of mmdevapi for desktop apps. The call activates
/// <c>IAudioClient</c> on a microphone and "may display a consent prompt the first time it is called". It also "must
/// be called on the main UI thread so that the consent prompt can be shown". See
/// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-activateaudiointerfaceasync.
/// Desktop apps do not support <c>AppCapability.RequestAccessAsync</c>, because it needs a CoreWindow. See
/// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-supported-api. For that reason it
/// failed with <c>RPC_E_DISCONNECTED</c>.
/// </para>
/// <para>
/// The class is safe under Native AOT. The call is a <see cref="LibraryImportAttribute"/>, and the source generator
/// makes the COM interfaces. The GUIDs and the method order match <c>mmdeviceapi.h</c>, <c>audioclient.h</c> and
/// <c>objidlbase.h</c> of SDK 10.0.26100.0.
/// </para>
/// </remarks>
internal static partial class MicrophoneConsent
{
    // The ID of IAudioClient in audioclient.h. Its activation on a microphone asks for consent.
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    /// <summary>
    /// Asks Windows for the default microphone. Windows shows its consent prompt when the user has not decided yet.
    /// </summary>
    /// <remarks>
    /// The caller must call it on the UI thread, so that Windows can show the prompt. The task completes when the user
    /// answers, or at once when access is already decided. The method releases the activated audio client at once,
    /// because only the answer matters.
    /// </remarks>
    /// <returns>
    /// The HRESULT of the activation: <c>S_OK</c> when the user gives access, <c>E_ACCESSDENIED</c> 0x80070005 when the
    /// user refuses.
    /// </returns>
    /// <exception cref="MicrophoneUnavailableException">There is no microphone.</exception>
    public static async Task<int> RequestAsync()
    {
        string device = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        if (string.IsNullOrEmpty(device))
        {
            throw new MicrophoneUnavailableException("NoDevice");
        }

        // Windows shows its prompt, then calls the handler on a worker thread.
        var handler = new CompletionHandler();
        int called  = ActivateAudioInterfaceAsync(device, in AudioClientId, 0, handler, out IActivateAudioInterfaceAsyncOperation operation);
        Log("activate call", $"0x{called:X8}");
        if (called < 0)
        {
            return called;
        }

        int result = await handler.Result;

        // Windows holds both objects until the callback, so the app must keep them alive until then too.
        GC.KeepAlive(handler);
        GC.KeepAlive(operation);
        return result;
    }

    /// <summary>
    /// Logs a step of the microphone request and the thread that it ran on. The log gets only names and numbers.
    /// </summary>
    /// <param name="step">The step, such as <c>check before</c> or <c>activate call</c>.</param>
    /// <param name="result">The status name or the HRESULT of the step.</param>
    public static void Log(string step, string result)
    {
        string apartment = Thread.CurrentThread.GetApartmentState().ToString();
        AppLog.MicrophoneStep(App.ShellLog, step, result, Environment.CurrentManagedThreadId, apartment);
    }

    // The native signature in mmdeviceapi.h is
    // STDAPI ActivateAudioInterfaceAsync(LPCWSTR, REFIID, PROPVARIANT*, Handler*, Operation**).
    [LibraryImport("Mmdevapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        in Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>
    /// Declares the handler that Windows calls when the activation finishes. See <c>mmdeviceapi.h</c>.
    /// </summary>
    [GeneratedComInterface]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    internal partial interface IActivateAudioInterfaceCompletionHandler
    {
        /// <summary>
        /// Windows calls this method when the activation finishes.
        /// </summary>
        /// <param name="activateOperation">The finished activation, which holds its result.</param>
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>
    /// Declares the activation in progress, which holds its result when it finishes. See <c>mmdeviceapi.h</c>.
    /// </summary>
    [GeneratedComInterface]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    internal partial interface IActivateAudioInterfaceAsyncOperation
    {
        /// <summary>
        /// Gives the result of the activation.
        /// </summary>
        /// <param name="activateResult">The HRESULT of the activation.</param>
        /// <param name="activatedInterface">The activated <c>IUnknown</c>, or 0 when the activation failed.</param>
        void GetActivateResult(out int activateResult, out nint activatedInterface);
    }

    /// <summary>
    /// Marks an object that any thread can call without marshaling. See <c>objidlbase.h</c>.
    /// </summary>
    [GeneratedComInterface]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    internal partial interface IAgileObject;

    /// <summary>
    /// Completes <see cref="Result"/> with the HRESULT of the activation. The class is agile, because Windows calls it
    /// on a worker thread.
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<int> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the HRESULT of the activation when Windows calls back.
        /// </summary>
        public Task<int> Result => _result.Task;

        /// <inheritdoc />
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            int result;
            try
            {
                activateOperation.GetActivateResult(out result, out nint activated);

                // Only the answer matters, so the handler releases the audio client.
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
