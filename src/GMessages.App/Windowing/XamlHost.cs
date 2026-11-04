using Microsoft.Windows.ApplicationModel.DynamicDependency;
using Windows.System;
using static GMessages.App.Windowing.WindowInterop;

namespace GMessages.App.Windowing;

/// <summary>
/// Readies the UI thread for system XAML: the WinUI 2.8 framework package, a dispatcher queue and the await context.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3's Application.Start did all of this; XAML Islands leave it to the app. Packaged, the manifest's dependency
/// puts WinUI 2.8 (the Microsoft.UI.Xaml.2.8 framework package) in the app's package graph. Unpackaged (Debug, the UI
/// tests), a dynamic dependency adds it to the process, which needs the framework package installed (most Windows 11
/// PCs have it; installing the MSIX brings it). It goes ahead of the Windows App SDK runtime the bootstrapper added,
/// whose WinUI 3 Microsoft.UI.Xaml.dll otherwise loads in its place and XAML fails to start.
/// </para>
/// <para>
/// The dispatcher queue is made before XAML starts, so XAML and the app share it (Windows Terminal's order). Awaits on
/// the UI thread come back through it; an exception that escapes such a continuation goes to
/// <see cref="UnhandledException"/>, as WinUI 3 sent it to Application.UnhandledException.
/// </para>
/// </remarks>
/// @link https://learn.microsoft.com/windows/apps/windows-app-sdk/use-dynamic-dependencies
internal static unsafe class XamlHost
{
    // Microsoft.UI.Xaml.2.8, 2.8.7 Or Later
    private const string WinUIFramework = "Microsoft.UI.Xaml.2.8_8wekyb3d8bbwe";
    private static readonly Windows.ApplicationModel.PackageVersion WinUIMinVersion = new() { Major = 8, Minor = 2501, Build = 31001, Revision = 0 };

    // DQTYPE_THREAD_CURRENT, DQTAT_COM_STA
    private const int ThreadCurrent = 2;
    private const int ComSta        = 2;

    /// <summary>
    /// Gets the UI thread's dispatcher queue (every window and element lives on that one thread). WinUI 3's
    /// DependencyObject.DispatcherQueue, which system XAML doesn't have.
    /// </summary>
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    // Kept For The Life Of The Process: The Queue's Controller, And WinUI 2.8 In The Package Graph
    internal static nint DispatcherQueueController { get; private set; }

    internal static PackageDependencyContext? WinUIContext { get; private set; }

    /// <summary>
    /// Handles an exception that escaped an await continuation on the UI thread; returns true when it was handled.
    /// Unhandled, the app ends (as before launch, so a broken start-up is never hidden).
    /// </summary>
    public static Func<Exception, bool>? UnhandledException { get; set; }

    /// <summary>
    /// Makes WinUI 2.8 loadable, then the UI thread's dispatcher queue and await context. Call once, before the App.
    /// </summary>
    public static void Start()
    {
        // WinUI 2.8 For An Unpackaged Run
        if (!AppIdentity.IsPackaged)
        {
            var options = new CreatePackageDependencyOptions { LifetimeArtifactKind = PackageDependencyLifetimeArtifactKind.Process };
            PackageDependency dependency = PackageDependency.Create(WinUIFramework, WinUIMinVersion, options);
            WinUIContext = dependency.Add(new AddPackageDependencyOptions { Rank = -1 });
        }

        // Dispatcher Queue On This Thread
        var queueOptions = new DispatcherQueueOptions { dwSize = sizeof(DispatcherQueueOptions), threadType = ThreadCurrent, apartmentType = ComSta };
        nint controller;
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(queueOptions, &controller));
        DispatcherQueueController = controller;
        Dispatcher                = DispatcherQueue.GetForCurrentThread();

        SynchronizationContext.SetSynchronizationContext(new UiSynchronizationContext(Dispatcher));
    }

    /// <summary>
    /// Runs awaits' continuations on the UI thread's dispatcher queue.
    /// </summary>
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
                    // Handled And Logged By The App
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
