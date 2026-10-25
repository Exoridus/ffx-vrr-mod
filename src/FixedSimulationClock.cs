namespace Fahrenheit.Mods.Vrr;

public readonly record struct SimulationFramePlan(int Steps, float Alpha, bool Reset, int DroppedSteps);

public sealed class FixedSimulationClock(int frequency = 60)
{
    public static uint ResolveExec(uint native, bool presentation) => presentation || native == 0 ? 0u : 1u;
    private readonly double _stepSeconds = frequency is 30 or 60 or 120
        ? 1d / frequency : throw new ArgumentOutOfRangeException(nameof(frequency));
    // The engine's own catch-up (FUN_00821f90) clamps a frame's elapsed time to 0.3 s and then runs
    // every pass that time covers. Dropping more than that made each loading hitch lengthen every
    // tick-counted wait by the hitch, which is why scene transitions took seconds longer than native.
    private const double MaximumGapSeconds = 0.3;
    private double _previous = double.NaN;
    private double _accumulator;

    public void Reset()
    {
        _previous = double.NaN;
        _accumulator = 0;
    }

    public SimulationFramePlan Plan(double now, double speed = 1)
    {
        double elapsed = now - _previous;
        _previous = double.IsFinite(now) ? now : double.NaN;
        if (!double.IsFinite(elapsed) || elapsed < 0)
        {
            _accumulator = 0;
            return new(1, 0, true, 0);
        }

        // A gap beyond the engine's clamp still covers the clamped time; only the history is invalid.
        bool reset = elapsed > MaximumGapSeconds;
        int lost = reset ? (int)Math.Floor((elapsed - MaximumGapSeconds) / _stepSeconds) : 0;
        _accumulator += Math.Min(elapsed, MaximumGapSeconds) * (double.IsFinite(speed) ? Math.Clamp(speed, 0, 8) : 1);
        // Floating-point timestamp subtraction must not postpone an exact tick boundary.
        int due = (int)Math.Floor((_accumulator + 1e-9) / _stepSeconds);
        _accumulator = Math.Max(0, _accumulator - due * _stepSeconds);
        return new(due, (float)Math.Clamp(_accumulator / _stepSeconds, 0, 1), reset, lost);
    }
}
