using System.Numerics;

namespace Fahrenheit.Mods.Vrr;

public readonly record struct Transform(Vector3 Position, Quaternion Rotation);

public sealed class RenderInterpolator
{
    readonly record struct Slot(Transform? Previous, Transform Current, double SnapshotIntervalSeconds, double SnapshotAtSeconds);

    readonly Dictionary<string, Slot> _slots = new();
    readonly Func<double> _timestampSeconds;
    double _tickPeriodSeconds = 1.0 / 60.0;

    public RenderInterpolator()
        : this(() => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency) { }

    internal RenderInterpolator(Func<double> timestampSeconds) => _timestampSeconds = timestampSeconds;

    public void SetTickPeriod(double tickPeriodSeconds) => _tickPeriodSeconds = tickPeriodSeconds;

    public void Snapshot(string key, Transform current)
    {
        double now = _timestampSeconds();
        bool hasPrevious = _slots.TryGetValue(key, out var existing);
        Transform? previous = hasPrevious ? existing.Current : null;
        double interval = hasPrevious ? now - existing.SnapshotAtSeconds : _tickPeriodSeconds;
        _slots[key] = new Slot(previous, current, interval, now);
    }

    public bool TryGetInterpolated(string key, double timeSinceLastTickSeconds, out Transform result)
    {
        if (!_slots.TryGetValue(key, out var slot) || slot.Previous is not { } previous)
        {
            result = _slots.TryGetValue(key, out var onlyCurrent) ? onlyCurrent.Current : default;
            return false;
        }

        // A gap much longer than one tick period means the previous/current pair is not a
        // continuous motion (a teleport or scene load), not a rate hiccup - do not blend across it.
        if (slot.SnapshotIntervalSeconds > _tickPeriodSeconds * 1.5)
        {
            result = slot.Current;
            return false;
        }

        double alpha = Math.Clamp(timeSinceLastTickSeconds / _tickPeriodSeconds, 0.0, 1.0);

        result = new Transform(
            Vector3.Lerp(previous.Position, slot.Current.Position, (float)alpha),
            Quaternion.Slerp(previous.Rotation, slot.Current.Rotation, (float)alpha));
        return true;
    }
}
