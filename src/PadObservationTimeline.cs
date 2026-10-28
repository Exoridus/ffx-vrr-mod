namespace Fahrenheit.Mods.Vrr;

internal sealed record PadObservedSample(long PollSequence, int Port, long ObservationStartTicks,
    long ObservationEndTicks, uint[] RawWords);
internal sealed record PadTickCorrelation(long Epoch, string? Boundary, PadObservedSample[] Observations,
    PadObservedSample?[] Latest);

/// <summary>
/// Correlates complete observations with model time without publishing native input or inferring edges.
/// Returned payloads are independent copies. Missing per-port candidates mean unknown input.
/// </summary>
internal sealed class PadObservationTimeline
{
    private const long MaximumExactTicks = 1L << 53;
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly int _capacity;
    private readonly List<PadObservedSample> _pending = [];
    private readonly PadObservedSample?[] _lastObserved = new PadObservedSample?[2];
    private readonly PadObservedSample?[] _latest = new PadObservedSample?[2];
    private long _epoch;
    private string? _boundary;
    private double _previousDeadline = double.NaN;

    internal PadObservationTimeline(long frequency, int capacity = 1024)
    {
        if (frequency <= 0 || frequency > MaximumExactTicks) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _frequency = frequency;
        _capacity = capacity;
    }

    internal long Epoch { get { lock (_gate) return _epoch; } }
    internal string? Boundary { get { lock (_gate) return _boundary; } }

    internal void Reset(string reason)
    {
        lock (_gate) ResetCore(reason);
    }

    internal bool Observe(long sequence, int port, long startTicks, long endTicks, ReadOnlySpan<uint> rawWords)
    {
        lock (_gate)
        {
            if (sequence <= 0 || port is < 0 or > 1 || rawWords.Length != 8
                || startTicks < 0 || endTicks < startTicks || endTicks > MaximumExactTicks)
            {
                ResetCore("invalid_observation");
                return false;
            }
            if (Math.BitIncrement(endTicks / (double)_frequency) <= _previousDeadline)
            {
                ResetCore("late_observation");
                return false;
            }
            var previous = _lastObserved[port];
            if (previous is not null && (sequence <= previous.PollSequence || startTicks < previous.ObservationEndTicks))
            {
                ResetCore("observation_order");
                return false;
            }
            if (previous is not null && (previous.RawWords[0] != rawWords[0] || previous.RawWords[5] != rawWords[5]
                || (previous.RawWords[7] & 0xffff0000u) != (rawWords[7] & 0xffff0000u)))
                ResetCore("observed_status_change");
            if (_pending.Count >= _capacity)
            {
                ResetCore("observation_overflow");
                return false;
            }
            var sample = new PadObservedSample(sequence, port, startTicks, endTicks, rawWords.ToArray());
            _pending.Add(sample);
            _lastObserved[port] = sample;
            return true;
        }
    }

    internal PadObservedSample?[] LatestObserved()
    {
        lock (_gate) return _lastObserved.Select(sample => sample is null ? null : Clone(sample)).ToArray();
    }

    internal PadTickCorrelation Correlate(double deadlineSeconds)
    {
        lock (_gate)
        {
            if (!double.IsFinite(deadlineSeconds) || deadlineSeconds < 0 || deadlineSeconds <= _previousDeadline)
            {
                ResetCore("invalid_deadline");
                return Result([]);
            }
            _previousDeadline = deadlineSeconds;
            List<PadObservedSample> selected = [];
            int kept = 0;
            for (int i = 0; i < _pending.Count; i++)
            {
                var sample = _pending[i];
                // Round availability upward; exact equality may wait one floating-point ULP, never arrive early.
                if (Math.BitIncrement(sample.ObservationEndTicks / (double)_frequency) <= deadlineSeconds)
                {
                    _latest[sample.Port] = sample;
                    selected.Add(Clone(sample));
                }
                else _pending[kept++] = sample;
            }
            _pending.RemoveRange(kept, _pending.Count - kept);
            return Result(selected.ToArray());
        }
    }

    private PadTickCorrelation Result(PadObservedSample[] selected)
        => new(_epoch, _boundary, selected, _latest.Select(sample => sample is null ? null : Clone(sample)).ToArray());

    private static PadObservedSample Clone(PadObservedSample sample) => sample with { RawWords = (uint[])sample.RawWords.Clone() };

    private void ResetCore(string reason)
    {
        _pending.Clear();
        Array.Clear(_lastObserved);
        Array.Clear(_latest);
        _previousDeadline = double.NaN;
        _boundary = reason;
        _epoch++;
    }
}
