using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GMessages.App;

/// <summary>
/// Entry point. Replaces the XAML-generated Main so later tasks can run work before the app starts.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
