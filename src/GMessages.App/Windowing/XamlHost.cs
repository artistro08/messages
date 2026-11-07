using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Windows.System;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Windowing;

/// <summary>
/// Readies the UI thread for system XAML: the WinUI 2.8 framework package, a dispatcher queue and the await context.
/// </summary>
/// <remarks>
/// <para>
/// Application.Start of WinUI 3 did all of this. XAML Islands leave it to the app. Packaged, the dependency in the
/// manifest puts WinUI 2.8 in the package graph of the app (the Microsoft.UI.Xaml.2.8 framework package). Unpackaged,
/// as in Debug and the UI tests, a dynamic dependency adds it to the process. That needs the framework package on the
/// PC. Most Windows 11 PCs have it, and an install of the MSIX brings it. The dependency goes ahead of the Windows App
/// SDK runtime that the bootstrapper added. Otherwise the WinUI 3 Microsoft.UI.Xaml.dll of that runtime loads in its
/// place, and XAML fails to start.
/// </para>
/// <para>
/// The dispatcher queue exists before XAML starts, so XAML and the app share it, in the order of Windows Terminal.
/// Awaits on the UI thread resume through it. An exception that escapes such a continuation goes to
/// <see cref="UnhandledException"/>, as WinUI 3 sent it to Application.UnhandledException.
/// </para>
/// </remarks>
/// <seealso href="https://learn.microsoft.com/windows/apps/windows-app-sdk/use-dynamic-dependencies"/>
internal static unsafe class XamlHost
{
    // Microsoft.UI.Xaml.2.8, version 2.8.7 or later.
    private const string WinUIFramework = "Microsoft.UI.Xaml.2.8_8wekyb3d8bbwe";
    private static readonly Windows.ApplicationModel.PackageVersion WinUIMinVersion = new() { Major = 8, Minor = 2501, Build = 31001, Revision = 0 };

    // DQTYPE_THREAD_CURRENT and DQTAT_COM_STA.
    private const int ThreadCurrent = 2;
    private const int ComSta        = 2;

    /// <summary>
    /// Gets the dispatcher queue of the UI thread, where every window and element lives. It replaces
    /// DependencyObject.DispatcherQueue of WinUI 3, which system XAML does not have.
    /// </summary>
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    // The process keeps the controller of the queue and WinUI 2.8 in the package graph for its whole life.
    internal static nint DispatcherQueueController { get; private set; }

    internal static PackageDependencyContext? WinUIContext { get; private set; }

    /// <summary>
    /// Handles an exception that escapes an await continuation on the UI thread, and returns true when it handled it.
    /// </summary>
    /// <remarks>
    /// An exception that it does not handle ends the app, as before the launch. Thus a broken start-up is never hidden.
    /// </remarks>
    public static Func<Exception, bool>? UnhandledException { get; set; }

    /// <summary>
    /// Makes WinUI 2.8 loadable, then makes the dispatcher queue and the await context of the UI thread. Call it once,
    /// before the app.
    /// </summary>
    public static void Start()
    {
        // An unpackaged run gets WinUI 2.8 from a dynamic dependency.
        if (!AppIdentity.IsPackaged)
        {
            var options = new CreatePackageDependencyOptions { LifetimeArtifactKind = PackageDependencyLifetimeArtifactKind.Process };
            PackageDependency dependency = PackageDependency.Create(WinUIFramework, WinUIMinVersion, options);
            WinUIContext = dependency.Add(new AddPackageDependencyOptions { Rank = -1 });
        }

        var queueOptions = new DispatcherQueueOptions { dwSize = sizeof(DispatcherQueueOptions), threadType = ThreadCurrent, apartmentType = ComSta };
        nint controller;
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(queueOptions, &controller));
        DispatcherQueueController = controller;
        Dispatcher                = DispatcherQueue.GetForCurrentThread();

        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(Dispatcher));
    }

    // Runs the continuations of awaits on the dispatcher queue of the UI thread.
    private sealed class UiSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            _ = queue.TryEnqueue(() =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex) when (UnhandledException?.Invoke(ex) == true)
                {
                    // The app handled and logged the exception.
                }
            });

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!queue.HasThreadAccess)
            {
                throw new NotSupportedException("Send is only supported on the UI thread.");
            }

            d(state);
        }

        public override SynchronizationContext CreateCopy() => new UiSynchronizationContext(queue);
    }
}
