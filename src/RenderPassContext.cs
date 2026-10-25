namespace Fahrenheit.Mods.Vrr;

internal static class RenderPassContext
{
    // Native jobs share one joined main-loop pass; thread-local phase would let workers advance on extra images.
    internal static volatile bool IsPresentation;
    internal static volatile bool IsActive;
    private static long _imageSerial, _simulationSerial, _historyEpoch;
    private static float _presentationAlpha = 1;
    private static volatile bool _finalImage;
    internal static long ImageSerial => System.Threading.Volatile.Read(ref _imageSerial);
    internal static long SimulationSerial => System.Threading.Volatile.Read(ref _simulationSerial);
    internal static float PresentationAlpha => System.Threading.Volatile.Read(ref _presentationAlpha);
    internal static bool IsFinalImage => _finalImage;
    internal static long HistoryEpoch => System.Threading.Volatile.Read(ref _historyEpoch);
    internal static void ResetHistory() => System.Threading.Interlocked.Increment(ref _historyEpoch);
    internal static void BeginSimulation() => System.Threading.Interlocked.Increment(ref _simulationSerial);
    internal static void SetPass(float alpha, bool finalImage)
    {
        System.Threading.Volatile.Write(ref _presentationAlpha, float.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1);
        _finalImage = finalImage;
    }
    private static double _seconds;
    private static int _waterConsumed;
    internal static double ImageSeconds => System.Threading.Volatile.Read(ref _seconds);
    internal static void BeginImage(double seconds)
    {
        System.Threading.Interlocked.Increment(ref _imageSerial);
        _finalImage = false;
        System.Threading.Volatile.Write(ref _seconds, seconds);
        System.Threading.Interlocked.Exchange(ref _waterConsumed, 0);
    }
    internal static double TakeWaterSeconds()
    {
        return System.Threading.Interlocked.Exchange(ref _waterConsumed, 1) == 0
            ? System.Threading.Volatile.Read(ref _seconds) : 0;
    }
}
