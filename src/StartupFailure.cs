namespace Fahrenheit.Mods.Vrr;

internal static class StartupFailure
{
    public static void Abort(Action report, Action cleanup, Action<string> terminate)
    {
        Abort(() => { try { report(); } finally { cleanup(); } }, terminate);
    }

    public static void Abort(Action cleanup, Action<string> terminate)
    {
        try
        {
            cleanup();
        }
        finally
        {
            terminate("VRR initialization failed after queued hooks may have been registered; stock Fahrenheit cannot safely cancel them.");
            throw new InvalidOperationException("Startup termination returned unexpectedly.");
        }
    }
}
