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
    private double _previousSpeed = double.NaN;
    private const long MaximumExactSteps = 1L << 53;
    private double _mappingOrigin = double.NaN;
    private long _mappingSteps, _deadlineFirstStep;
    private double _deadlineSpacing;
    private int _deadlineCount;
    /// <summary>Whether the latest interval has a stable model time mapping, including zero-step images.</summary>
    public bool HasMappedInterval { get; private set; }

    public void Reset()
    {
        _previous = double.NaN;
        _accumulator = 0;
        _previousSpeed = double.NaN;
        _mappingOrigin = double.NaN;
        _deadlineCount = 0;
        HasMappedInterval = false;
    }

    /// <summary>
    /// Returns a model tick boundary in absolute clock seconds for the latest plan only.
    /// Stable consecutive speed samples assume no intervening change; boundaries are not hardware deadlines.
    /// Unmapped plans and invalid indices return false with NaN. Plan and Reset invalidate previous availability.
    /// </summary>
    public bool TryGetStepDeadline(int stepIndex, out double deadline)
    {
        deadline = double.NaN;
        if (stepIndex < 0 || stepIndex >= _deadlineCount) return false;
        deadline = StepDeadline(stepIndex);
        return true;
    }

    private double StepDeadline(int stepIndex)
        => _mappingOrigin + (_deadlineFirstStep + stepIndex) * _deadlineSpacing;

    public SimulationFramePlan Plan(double now, double speed = 1)
    {
        _deadlineCount = 0;
        HasMappedInterval = false;
        double previous = _previous;
        double accumulatorBefore = _accumulator;
        speed = double.IsFinite(speed) ? Math.Clamp(speed, 0, 8) : 1;
        bool stableSpeed = speed == _previousSpeed;
        _previousSpeed = speed;
        double elapsed = now - _previous;
        _previous = double.IsFinite(now) ? now : double.NaN;
        if (!double.IsFinite(elapsed) || elapsed < 0)
        {
            _accumulator = 0;
            _mappingOrigin = double.NaN;
            return new(1, 0, true, 0);
        }

        // A gap beyond the engine's clamp still covers the clamped time; only the history is invalid.
        bool reset = elapsed > MaximumGapSeconds;
        int lost = reset ? (int)Math.Floor((elapsed - MaximumGapSeconds) * speed / _stepSeconds) : 0;
        _accumulator += Math.Min(elapsed, MaximumGapSeconds) * speed;
        // Floating-point timestamp subtraction must not postpone an exact tick boundary.
        int due = Math.Max(0, (int)Math.Floor((_accumulator + 1e-9) / _stepSeconds));
        // Retain any epsilon-borrowed time so image partitions cannot manufacture ticks.
        _accumulator -= due * _stepSeconds;
        if (!reset && stableSpeed && speed > 0 && previous >= 0 && now >= 0)
        {
            if (double.IsNaN(_mappingOrigin))
            {
                _mappingOrigin = previous - accumulatorBefore / speed;
                _mappingSteps = 0;
            }
            _deadlineSpacing = _stepSeconds / speed;
            _deadlineFirstStep = _mappingSteps + 1;
            double last = previous;
            // Keep one expression and ordinal across images so rounding cannot move observations between ticks.
            for (int i = 0; i < due && _mappingSteps <= MaximumExactSteps - due; i++)
            {
                double deadline = StepDeadline(i);
                if (!double.IsFinite(deadline) || deadline <= last) break;
                last = deadline;
                if (i == due - 1) _deadlineCount = due;
            }
            HasMappedInterval = due == 0 || _deadlineCount == due;
            if (HasMappedInterval) _mappingSteps += due;
            else _mappingOrigin = double.NaN;
        }
        else _mappingOrigin = double.NaN;
        return new(due, (float)Math.Clamp(_accumulator / _stepSeconds, 0, 1), reset, lost);
    }
}
