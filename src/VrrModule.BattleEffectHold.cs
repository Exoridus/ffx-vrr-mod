using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private bool _bxPresented;
    private readonly object _bxHoldGate = new();
    private long _bxHoldGeneration, _bxHoldSerial, _bxHoldActive;
    private int _bxHoldOwner;
    private nint _bxHoldBlock;

    private readonly record struct BattleEffectHoldToken(nint Block, long Generation, long Serial, long Parent, int Owner, byte Previous);

    private bool IsOwnedBattleEffectHold
    {
        get
        {
            lock (_bxHoldGate)
                return _bxHoldActive != 0 && _bxHoldOwner == Environment.CurrentManagedThreadId
                    && _particle_pilot_thread == _bxHoldOwner && _battleEffectPauseInstalled
                    && ReferenceEquals(s_battle_effects, this) && _bx != null && _bxHoldBlock == (nint)_bx;
        }
    }

    private bool battle_effects_hold_begin(out BattleEffectHoldToken token)
    {
        token = default;
        int thread = Environment.CurrentManagedThreadId;
        nint block = (nint)_bx;
        if (block == 0 || !MemoryProbe.IsReadable(block, 0x20)) return false;
        lock (_bxHoldGate)
        {
            if (!_battleEffectPauseInstalled || !ReferenceEquals(s_battle_effects, this)
                || block != (nint)_bx
                || thread != Volatile.Read(ref _particle_pilot_thread)
                || (RenderSchedulerReady && !IsOwnedRenderLoop)
                || (_bxHoldActive != 0 && (_bxHoldOwner != thread || _bxHoldBlock != block))) return false;
            token = new((nint)_bx, _bxHoldGeneration, ++_bxHoldSerial, _bxHoldActive, thread, _bx[0]);
            _bxHoldBlock = (nint)_bx;
            _bxHoldOwner = thread;
            _bxHoldActive = token.Serial;
            _bx[0] = 1;
            _bxPresented = true;
            return true;
        }
    }

    private void battle_effects_hold_end(BattleEffectHoldToken token)
    {
        lock (_bxHoldGate)
        {
            if (token.Serial == 0 || token.Owner != Environment.CurrentManagedThreadId
                || token.Owner != _bxHoldOwner || token.Generation != _bxHoldGeneration
                || token.Serial != _bxHoldActive || token.Block != (nint)_bx
                || !ReferenceEquals(s_battle_effects, this)) return;
            _bx[0] = token.Previous;
            _bxHoldActive = token.Parent;
            if (_bxHoldActive == 0) _bxHoldOwner = 0;
        }
    }

    private void battle_effects_set_pass(bool presentation)
    {
        lock (_bxHoldGate)
        {
            if (_bxHoldActive != 0 && _bxHoldOwner != Environment.CurrentManagedThreadId) return;
            // A pass reset revokes outstanding tokens before their finally blocks can restore an old byte.
            _bxHoldGeneration++;
            _bxHoldActive = 0;
            _bxHoldOwner = 0;
            _bxHoldBlock = 0;
            if (_bx != null) _bx[0] = presentation ? (byte)1 : (byte)0;
        }
    }

    private void battle_effects_end_pass() => battle_effects_set_pass(false);
}