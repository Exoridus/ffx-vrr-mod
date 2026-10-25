namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private byte[]? _battleEffectSnapshot;

    private bool battle_effect_presentation_is_qualified()
    {
        nint actors = FhUtil.get_at<nint>(EngineAddresses.PlayerChrs);
        if (actors == 0) return true;
        int owner = System.Threading.Volatile.Read(ref _render_owner_thread);
        if (owner == 0 || Environment.CurrentManagedThreadId != owner) return false;
        byte[] snapshot = _battleEffectSnapshot ??= new byte[BattleEffectState.SnapshotLength];
        return MemoryProbe.TryCopy(actors, snapshot)
            && FhUtil.get_at<nint>(EngineAddresses.PlayerChrs) == actors
            && BattleEffectState.CanPresent(snapshot);
    }
}
