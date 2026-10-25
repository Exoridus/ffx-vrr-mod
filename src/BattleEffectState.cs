namespace Fahrenheit.Mods.Vrr;

internal static class BattleEffectState
{
    internal const int ActorCount = 31;
    internal const int ActorStride = 0xf90;
    internal const int SnapshotLength = ActorCount * ActorStride;
    private const int OverlayStateOffset = 0xdfb;

    internal static bool CanPresent(ReadOnlySpan<byte> actors)
    {
        if (actors.Length < SnapshotLength) return false;
        for (int slot = 0; slot < ActorCount; slot++)
            if (actors[slot * ActorStride + OverlayStateOffset] is 4 or 5) return false;
        return true;
    }
}
