namespace Fahrenheit.Mods.Vrr;

internal sealed class TimingOwnership : IDisposable
{
    private readonly System.Threading.Mutex _handle;

    private TimingOwnership(System.Threading.Mutex handle) => _handle = handle;

    public static bool TryAcquire(out TimingOwnership? ownership)
    {
        // createdNew distinguishes loaded assemblies even on the same thread, where WaitOne is reentrant.
        var handle = new System.Threading.Mutex(false, $"Local\\FfxFrameTiming-{Environment.ProcessId}", out bool created);
        if (!created)
        {
            handle.Dispose();
            ownership = null;
            return false;
        }

        ownership = new(handle);
        return true;
    }

    public void Dispose() => _handle.Dispose();
}
