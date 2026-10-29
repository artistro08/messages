using System.Runtime.InteropServices;

namespace SignInSpike;

/// <summary>
/// Spike-only: package identity check and relaunch outside the MSIX package context.
/// </summary>
/// <remarks>
/// Proves whether a packaged app can run its WebView2 sign-in in a child that breaks away from the
/// package (PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY), so passkeys and Google sign-in work again.
/// </remarks>
internal static class Breakaway
{
    // Package Identity
    public static bool IsPackaged()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700;
    }

    // Relaunch This Exe Outside The Package With The Given Arguments
    public static bool Relaunch(string arguments)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list   = Marshal.AllocHGlobal(size);
        IntPtr policy = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                return false;
            }

            Marshal.WriteInt32(policy, 0x01); // PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_ENABLE_PROCESS_TREE
            if (!UpdateProcThreadAttribute(list, 0, 0x00020012, policy, sizeof(uint), IntPtr.Zero, IntPtr.Zero))
            {
                return false;
            }

            var info = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() }, AttributeList = list };
            string command = $"\"{Environment.ProcessPath}\" {arguments}";
            return CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, 0x00080000, IntPtr.Zero, null, ref info, out _);
        }
        finally
        {
            Marshal.FreeHGlobal(policy);
            Marshal.FreeHGlobal(list);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, IntPtr name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nint size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, string command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
