using System.Threading;

namespace Fahrenheit.Mods.Vrr;

internal sealed class PresentationAdmission
{
    private sealed record Refusal(string Reason, long RejectedThrough);

    private readonly object _gate = new();
    private long _lastImage;
    private Refusal? _refusal;
    internal string? Failure => Volatile.Read(ref _refusal)?.Reason;

    internal sealed class Image(PresentationAdmission owner, long serial)
    {
        internal long Serial { get; } = serial;
        internal bool IsRejected => Volatile.Read(ref owner._refusal) is { } refusal
            && Serial <= refusal.RejectedThrough;
    }

    internal Image BeginImage()
    {
        lock (_gate) return new(this, ++_lastImage);
    }

    // History resets cannot prove that a rejected producer is safe again.
    internal bool Refuse(string reason)
    {
        lock (_gate)
        {
            if (_refusal is not null) return false;
            Volatile.Write(ref _refusal, new(reason, _lastImage));
            return true;
        }
    }
}
