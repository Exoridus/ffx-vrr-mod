using System.Threading;

namespace Fahrenheit.Mods.Vrr;

internal sealed class PresentationAdmission
{
    private string? _failure;
    internal string? Failure => Volatile.Read(ref _failure);

    // History resets cannot prove that a rejected producer is safe again.
    internal bool Refuse(string reason) => Interlocked.CompareExchange(ref _failure, reason, null) is null;
}
