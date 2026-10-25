namespace Fahrenheit.Mods.Vrr;

internal sealed class FrameRateCounter
{
    private double _seconds = double.NaN;
    private long _rendered, _simulation;
    internal bool HasSample { get; private set; }
    internal double Rendered { get; private set; }
    internal double Simulation { get; private set; }

    internal void Sample(double seconds, long rendered, long simulation)
    {
        if (!double.IsNaN(_seconds))
        {
            double elapsed = seconds - _seconds;
            if (elapsed < 0.5) return;
            Rendered = (rendered - _rendered) / elapsed;
            Simulation = (simulation - _simulation) / elapsed;
            HasSample = true;
        }
        _seconds = seconds;
        _rendered = rendered;
        _simulation = simulation;
    }
}
