using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private bool _bxPresented;
    private bool _staticEffectVmClockInstalled;
    private bool HasEffectVmClockBackend => _staticEffectVmClockInstalled || _battleEffectPauseInstalled;
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
            if ((_bxHoldActive != 0 && _bxHoldOwner != Environment.CurrentManagedThreadId)
                || (_effectVmClockActive != 0 && _effectVmClockOwner != Environment.CurrentManagedThreadId)) return;
            // A pass reset revokes outstanding tokens before their finally blocks can restore an old byte.
            _bxHoldGeneration++;
            _effectVmClockActive = 0;
            _effectVmClockOwner = 0;
            _effectVmClockBlock = 0;
            _bxHoldActive = 0;
            _bxHoldOwner = 0;
            _bxHoldBlock = 0;
            if (_bx != null) _bx[0] = presentation ? (byte)1 : (byte)0;
        }
    }

    private void battle_effects_end_pass() => battle_effects_set_pass(false);
    private readonly record struct EffectVmClockToken(long Serial, long Parent, long Generation, int Owner, long Epoch);
    private long _effectVmClockSerial, _effectVmClockActive, _effectVmClockEpoch;
    private int _effectVmClockOwner;
    private nint _effectVmClockBlock;

    private bool static_effect_vm_presentation_ready()
    {
        long epoch = RenderPassContext.HistoryEpoch, image = RenderPassContext.ImageSerial;
        return RuntimeEffectsArmed && HasEffectVmClockBackend && OwnsRenderPass && RenderPassContext.IsPresentation
            && Environment.CurrentManagedThreadId == _particle_pilot_thread && ReferenceEquals(s_battle_effects, this)
            && _bx != null && MemoryProbe.IsReadable((nint)_bx, 0x20) && battle_effect_presentation_is_qualified()
            && epoch == RenderPassContext.HistoryEpoch && image == RenderPassContext.ImageSerial;
    }
    private bool HasEffectVmClockHold { get { lock (_bxHoldGate) return _effectVmClockActive != 0; } }

    private bool IsOwnedEffectVmClockHold
    {
        get
        {
            lock (_bxHoldGate)
                return RuntimeEffectsArmed && _effectVmClockActive != 0
                    && _effectVmClockOwner == Environment.CurrentManagedThreadId
                    && _effectVmClockOwner == _particle_pilot_thread
                    && _effectVmClockEpoch == RenderPassContext.HistoryEpoch
                    && OwnsRenderPass && !RenderPassContext.IsPresentation && HasEffectVmClockBackend
                    && ReferenceEquals(s_battle_effects, this) && _bx != null && _effectVmClockBlock == (nint)_bx;
        }
    }

    private bool effect_vm_clock_begin(out EffectVmClockToken token)
    {
        token = default;
        int thread = Environment.CurrentManagedThreadId;
        nint block = (nint)_bx;
        if (block == 0 || !MemoryProbe.IsReadable(block, 0x20)) return false;
        lock (_bxHoldGate)
        {
            if (!RuntimeEffectsArmed || !OwnsRenderPass || RenderPassContext.IsPresentation
                || !HasEffectVmClockBackend || !ReferenceEquals(s_battle_effects, this)
                || block != (nint)_bx || thread != _particle_pilot_thread
                || (_effectVmClockActive != 0 && (_effectVmClockOwner != thread || _effectVmClockBlock != block))) return false;
            token = new(++_effectVmClockSerial, _effectVmClockActive, _bxHoldGeneration, thread, RenderPassContext.HistoryEpoch);
            _effectVmClockActive = token.Serial;
            _effectVmClockEpoch = token.Epoch;
            _effectVmClockOwner = thread;
            _effectVmClockBlock = block;
            return true;
        }
    }

    private void effect_vm_clock_end(EffectVmClockToken token)
    {
        lock (_bxHoldGate)
        {
            if (token.Serial == 0 || token.Owner != Environment.CurrentManagedThreadId
                || token.Owner != _effectVmClockOwner || token.Generation != _bxHoldGeneration
                || token.Serial != _effectVmClockActive || _effectVmClockBlock != (nint)_bx) return;
            _effectVmClockActive = token.Parent;
            if (token.Parent == 0) _effectVmClockOwner = 0;
        }
    }
}