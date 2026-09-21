using AppAtlas.Sdk;

/// <summary>
/// The .NET Core runtime's startup hook: named by the package's targets
/// (`STARTUP_HOOKS` in the app's runtimeconfig), called on the main thread
/// before the app's own Main. A crash in the app's first line is already
/// caught; the framework hooks and the UI watchdog attach as the frameworks
/// load. Global namespace and this exact shape, because the runtime looks
/// for nothing else.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            Atlas.Start();
        }
        catch (System.Exception)
        {
            // A start that cannot happen here still can from Main.
        }
    }
}
