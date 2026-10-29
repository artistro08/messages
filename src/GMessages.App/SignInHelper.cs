using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using GMessages.Presentation.Pairing;
using GMessages.Presentation.Settings;
using Microsoft.Extensions.Logging;

namespace GMessages.App;

/// <summary>
/// Starts the sign-in helper (this exe with <c>--sign-in</c>) and receives its one message over a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// Google sign-in fails in WebView2 inside the MSIX package identity (no passkeys, "This browser or app may not be
/// secure"), so the sign-in runs in a helper process. Packaged, it is launched with the desktop-app break-away policy
/// (ported from <c>tools/SignInSpike/Breakaway.cs</c>). That policy puts the helper's <i>children</i> outside the
/// package, so the packaged helper relays to a second helper that runs outside it (see <c>Program</c>), which was
/// verified in the installed MSIX. Unpackaged, it is a plain launch.
/// This process never loads WebView2.
/// </para>
/// <para>
/// The pipe has a random name, is made before the launch, is limited to the current user, takes one client and is
/// read-only on this side. The message format and its checks are <see cref="SignInPipeMessage"/>.
/// </para>
/// </remarks>
internal sealed class SignInHelper : IDisposable
{
    // A Message Written Just Before Exit Is Still In The Pipe; The Helper Deletes Its Profile Before Exiting
    private static readonly TimeSpan ReadGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitWait  = TimeSpan.FromSeconds(10);

    private readonly NamedPipeServerStream _pipe;
    private readonly Process _process;
    private readonly ILogger _log;
    private bool _messageReceived;

    private SignInHelper(NamedPipeServerStream pipe, Process process, Task connect, ILogger log)
    {
        _pipe      = pipe;
        _process   = process;
        _log       = log;
        Completion = WaitAsync(connect);
    }

    /// <summary>
    /// Gets the helper's result: the captured cookies, or null when the user closed the sign-in window.
    /// </summary>
    /// <remarks>
    /// Faults when the helper sent a bad message (<see cref="InvalidDataException"/>) or ended with an error and no
    /// message (<see cref="IOException"/>). Completes on a background thread.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, string>?> Completion { get; }

    /// <summary>
    /// Makes the pipe, launches the helper and lets it take the foreground.
    /// </summary>
    /// <param name="logsFolder">Where the helper writes its own log.</param>
    /// <param name="theme">The app theme for the sign-in window.</param>
    /// <param name="log">Writes the launch (never cookie values).</param>
    /// <returns>The running helper; dispose it to stop it.</returns>
    /// <exception cref="Win32Exception">The launch failed.</exception>
    /// <exception cref="IOException">The pipe couldn't be made.</exception>
    public static SignInHelper Start(string logsFolder, AppTheme theme, ILogger log)
    {
        // Pipe: Listening Before The Launch, So The Helper Can Connect At Once
        string pipe_name = Guid.NewGuid().ToString("N");
        var pipe         = new NamedPipeServerStream(pipe_name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            Task connect     = pipe.WaitForConnectionAsync();
            string arguments = SignInHelperArgs.Join([.. SignInHelperArgs.Build(pipe_name, logsFolder, theme), .. TestArguments()]);

            // Launch: Outside The Package When Packaged
            Process process = AppIdentity.IsPackaged
                ? StartBrokenAway(arguments)
                : Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false })!;

            // Foreground: The Helper's Window Comes Up In Front Of The Pairing Window. Packaged, the window belongs to a
            // grandchild (see Program's relay), whose ID isn't known here, so any process may take it
            NativeMethods.AllowSetForegroundWindow(AppIdentity.IsPackaged ? NativeMethods.ASFW_ANY : (uint)process.Id);
            AppLog.SignInHelperLaunched(log, process.Id, AppIdentity.IsPackaged);
            return new SignInHelper(pipe, process, connect, log);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Stops the helper unless it already sent its message, then deletes the profile a killed helper left behind.
    /// </summary>
    /// <remarks>
    /// A helper that sent its message is left to delete its own profile and exit. Otherwise the whole helper tree
    /// (its WebView2 browser processes too) is killed.
    /// </remarks>
    public void Dispose()
    {
        _pipe.Dispose();

        // Kill The Whole Tree, Then Clear The Profile It Can No Longer Delete
        bool killed = false;
        if (!_messageReceived)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    killed = true;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Already Gone
            }
        }

        _process.Dispose();
        if (killed)
        {
            SignIn.DeleteLeftoverProfiles(_log);
        }
    }

    // =========================================================================
    // PIPE
    // =========================================================================

    // The Message, Or (No Message) How The Helper Ended
    private async Task<IReadOnlyDictionary<string, string>?> WaitAsync(Task connect)
    {
        Task<IReadOnlyDictionary<string, string>?> read = ReadAsync(connect);
        Task exited = _process.WaitForExitAsync();

        // Exited First: Give The Read A Moment To Drain The Pipe
        await Task.WhenAny(read, exited).ConfigureAwait(false);
        if (!read.IsCompleted)
        {
            await Task.WhenAny(read, Task.Delay(ReadGrace)).ConfigureAwait(false);
        }

        // A Message (Or A Bad One)
        if (read.IsCompletedSuccessfully)
        {
            _messageReceived = true;
            return read.Result;
        }

        if (read.Exception?.InnerException is InvalidDataException bad)
        {
            throw bad;
        }

        // No Message: A Clean Exit Is A Closed Window, Anything Else A Failure
        await exited.WaitAsync(ExitWait).ConfigureAwait(false);
        return _process.ExitCode == 0
            ? null
            : throw new IOException($"The sign-in helper exited with code {_process.ExitCode} without a message.");
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadAsync(Task connect)
    {
        await connect.ConfigureAwait(false);
        return await SignInPipeMessage.ReadAsync(_pipe, CancellationToken.None).ConfigureAwait(false);
    }

    // =========================================================================
    // LAUNCH
    // =========================================================================

    // Debug: The Helper Talks To The Same Fake Server And Uses The Same Test Names
    private static string[] TestArguments()
    {
#if EnableTestHooks
        if (TestHooks.EndpointBase is { } endpoint && TestHooks.DataDir is { } data_dir)
        {
            return ["--test-endpoints", endpoint.AbsoluteUri, "--test-data-dir", data_dir];
        }
#endif
        return [];
    }

    // Ported From The Sign-In Spike's Breakaway.Relaunch: PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY With
    // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE, So The Child (And Its Browser Processes) Run Outside The Package
    private static Process StartBrokenAway(string arguments)
    {
        nint size = 0;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list   = Marshal.AllocHGlobal(size);
        IntPtr policy = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception();
            }

            try
            {
                Marshal.WriteInt32(policy, NativeMethods.PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE);
                if (!NativeMethods.UpdateProcThreadAttribute(list, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY, policy, sizeof(int), IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception();
                }

                var info = new NativeMethods.STARTUPINFOEXW
                {
                    StartupInfo     = new NativeMethods.STARTUPINFOW { cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEXW>() },
                    lpAttributeList = list,
                };
                char[] command = $"\"{Environment.ProcessPath}\" {arguments}\0".ToCharArray();
                if (!NativeMethods.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, NativeMethods.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref info, out var created))
                {
                    throw new Win32Exception();
                }

                // Open .NET's Own Handle While Ours Still Pins The Process, So Its Exit Code Can't Be Lost
                try
                {
                    var process = Process.GetProcessById(created.dwProcessId);
                    _ = process.SafeHandle;
                    return process;
                }
                finally
                {
                    NativeMethods.CloseHandle(created.hThread);
                    NativeMethods.CloseHandle(created.hProcess);
                }
            }
            finally
            {
                NativeMethods.DeleteProcThreadAttributeList(list);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(list);
        }
    }
}
