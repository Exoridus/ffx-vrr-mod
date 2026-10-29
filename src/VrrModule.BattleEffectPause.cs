using System.Threading;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Battle effect adapters for presentation-only passes (port of the upstream FFX-60fps MAG rows).
///     Guards cover particle pause tests, selected walker dispatch entries, WAIT countdowns,
///     integration, record restoration, family-X walks and the current magic overlay's driver,
///     motion script and arc node. Logic passes reset stale overlay pause state; the shader guard
///     rejects draws whose materials are not ready. These contracts do not establish handler-body
///     or helper purity, RNG isolation, allocation lifetime or draw-output stability.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint BxMagicFunctions = 0x864ce8, BxPppPauseCompare = 0x31228a, BxPppSpawnCall = 0x31226d;
    private const nint BxUnitProgramSite = 0x4008fe, BxPauseSetter = 0x32a750;
    private const nint BxOppDispatch = 0x40c3c5, BxOppTable = 0x848e78;
    private const nint BxFallbackDispatch = 0x4210a2, BxShaderSetup = 0x2a48d0;
    private const nint BxNodeWalkSite = 0x525498, BxNodeWalk = 0x530300, BxNodeStep = 0x52ed90;
    private const nint BxRecordStepSite = 0x52f1ab, BxRecordStep = 0x52eec0;
    private const nint BxMagicFileStart = 0x5da7f0, BxMagicHandle = 0x864cac, BxMagicId = 0x864ca0, BxInitMagicPrx = 0x1efb6a0;
    private static readonly (int Op, nint Handler)[] BxOppOps = [(1, 0x411120), (9, 0x4124b0), (10, 0x411530), (17, 0x411bc0)];

    private const string BxStepPauseOpcodes = "83 3D ?? ?? ?? ?? 00 75 42";
    private const string BxUnitProgramPattern = "A1 ?? ?? ?? ?? 6A 30 57 C7 00 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B F0 83 C4 08 85 F6 74 ?? 0F B7 47 1E";
    private const string BxPauseSetterPattern = "55 8B EC 8B 45 08 A3 ?? ?? ?? ?? 5D C3";
    private const string BxFollowPattern = "8A 83 BA 00 00 00 84 C0 74 15 3C 0C 73 11 0F B6 C0 57 8B 04 85 ?? ?? ?? ?? 56 FF D0 83 C4 08 66 FF 86 18 02 00 00";
    private const string BxAttachPattern = "55 8B EC 53 8B 5D 08 56 57 8B BB 20 02 00 00 0F B7 87 A0 00 00 00 50 8D 83 00 03 00 00 57 50 E8 ?? ?? ?? ?? 83 C4 0C 8B B0 BC 00 00 00 0F BF 06";
    private const string BxFindUnitPattern = "55 8B EC 56 8B 75 0C 57 66 8B 7D 10 66 85 FF 79 4F";
    private const string BxOppDispatchPattern = "0F B6 C0 8B 04 85 ?? ?? ?? ?? 85 C0 0F 85 ?? ?? ?? ?? 68";
    private const string BxFallbackPattern = "E8 ?? ?? ?? ?? 85 C0 75 1C 50 E8 ?? ?? ?? ?? 83 C4 04 80 3D ?? ?? ?? ?? 00 74 0A E8";
    private const string BxShaderPattern = "55 8B EC 81 EC 8C 00 00 00 A1 ?? ?? ?? ?? 33 C5 89 45 FC 53 8B 5D 0C 56 57 8B F9 68 ?? ?? ?? ?? 8B 47 3C 89 85 74 FF FF FF 8B 47 24 8B 80 94 00 00 00 FF 70 20 FF 15";
    private const string BxNodeWalkSitePattern = "8B 47 0C 8D 77 1C 56 A3 ?? ?? ?? ?? E8 ?? ?? ?? ?? 56 E8 ?? ?? ?? ?? 83 C4 20 85 C0 74 0B FF 77 04 E8";
    private const string BxNodeWalkPattern = "55 8B EC 56 8B 75 08 57 8B FE 85 F6 74 30 8B FF 57 E8 ?? ?? ?? ?? 8B 7F 08";
    private const string BxNodeStepPattern = "55 8B EC 56 8B 75 08 EB 07 8D A4 24 00 00 00 00 8B 86 AC 00 00 00 83 26 FD 8B 44 86 0C 85 C0 74 06 56 FF D0 83 C4 04 F6 06 02 75 E4 8B 86 B0 00 00 00 85 C0 74 0B F6 06 04 75 06 56 FF D0 83 C4 04 5E 5D C3";
    private const string BxRecordStepSitePattern = "A1 ?? ?? ?? ?? 6A 30 57 FF 30 E8 ?? ?? ?? ?? 83 C4 2C 85 C0 0F 85";
    private const string BxRecordStepPattern = "55 8B EC FF 75 10 A1 ?? ?? ?? ?? 8B 4D 08 FF 75 0C 89 08 E8 ?? ?? ?? ?? 83 C4 08 5D C3";

    private bool _battleEffectPauseInstalled;
    private nint _bxPool, _bxAdapter, _bxFindUnitFn;
    private int _bxPoolUsed;
    private const int BxPoolSize = 0x100000;
    private int _bxWantedPause;
    private (nint Init, int MagicId) _bxOverlayKey;
    private BxOverlay? _bxOverlay;
    private readonly Dictionary<(nint Base, string Name), BxOverlay> _bxOverlays = [];
    private readonly List<(nint HeadOperand, nint Trampoline)> _bxDrivers = [];
    private long _bxDriverHolds, _bxPauseResets, _bxFollowSkips;
    private int _bxTrackFailures;

    private sealed class BxOverlay(nint image, string name)
    {
        internal readonly nint Base = image;
        internal readonly string Name = name;
        internal readonly List<(nint Site, byte[] Original, byte[] Patch)> Patches = [];
        internal readonly List<string> Rows = [];
        internal PauseSetter? Setter;
        internal byte[]? SetterBytes;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_bx_setter(int value);

    private static nint bx_va(nint rva) => (nint)FhUtil.ptr_at<byte>(rva);

    private static (ushort Opcode, uint Target)[] bx_opu_handlers()
        => BxOpuHandlers.Select(entry => (entry.Opcode, (uint)bx_va(entry.TargetRva))).ToArray();

    private string? bx_contract_error()
    {
        if (!_particle_pilot_installed || !_fieldPauseInstalled) return "the particle presentation and field pause are not installed";
        nint table = BxMagicFunctions;
        if (FhUtil.get_at<uint>(table + 0xb20) != (uint)bx_va(EngineAddresses.PppRunPart)
            || FhUtil.get_at<uint>(table + 0xb30) != (uint)bx_va(PilotWorker)
            || FhUtil.get_at<uint>(table + 0x3f4) != (uint)bx_va(RenderGetExec))
            return "gMagicFunctions particle step, walker or exec-count slot changed";
        if (!fx_code_matches(BxPppPauseCompare, BxStepPauseOpcodes) || !fx_operand_is(BxPppPauseCompare + 2, EngineAddresses.PpvUserStopPartF)
            || !fx_call_is(BxPppSpawnCall, RenderParticleEmit))
            return "MAG-006 particle step pause test or spawn call changed";
        if (!fx_code_matches(BxUnitProgramSite, BxUnitProgramPattern) || !fx_call_is(BxUnitProgramSite + 0xe, EngineAddresses.PppRunPart))
            return "MAG-029 effect-unit particle program call changed";
        if (!fx_code_matches(BxPauseSetter, BxPauseSetterPattern) || !fx_operand_is(BxPauseSetter + 7, EngineAddresses.PpvUserStopPartF))
            return "MAG-028 particle pause setter changed";
        if (!fx_code_matches(BxStreamHead, BxHeadPattern) || !fx_code_matches(BxSentry, BxSentryPattern)
            || !fx_code_matches(BxHandler, BxHandlerPattern) || !fx_code_matches(BxDec, BxDecPattern)
            || !fx_code_matches(BxPcCommit, BxPcCommitPattern) || !fx_code_matches(BxFreshWait, BxFreshWaitPattern)
            || !fx_code_matches(BxNext, BxNextPattern) || !fx_code_matches(BxGate, BxGatePattern))
            return "MAG-025 walker stream loop changed";
        Span<byte> opuTable = stackalloc byte[(0x061 + 1) * 4];
        if (!MemoryProbe.TryCopy(bx_va(BxOpuTable), opuTable) || !OpuHandlerTableMatches(opuTable, bx_opu_handlers()))
            return "MAG-025 OPU candidate handler table changed or is unreadable";
        if (!fx_code_matches(BxFollowSite, BxFollowPattern) || !fx_code_matches(BxFollowAttach, BxAttachPattern)
            || !fx_call_is(BxFollowAttach + 0x1f, BxFindUnit) || !fx_code_matches(BxFindUnit, BxFindUnitPattern))
            return "MAG-025 follow call, attach handler or unit lookup changed";
        if (!fx_operand_is(BxFollowSite + 0x15, BxFollowTable) || FhUtil.get_at<uint>(BxFollowTable + 10 * 4) != (uint)bx_va(BxFollowAttach))
            return "MAG-025 follow handler table changed";
        if (!fx_code_matches(BxRunAfterWalk, BxRunAfterPattern) || !fx_call_is(BxRunAfterWalk + 0xe, PilotWorker)
            || !fx_code_matches(BxRunBeforeWalk, "6A 00 6A 00 E8") || !fx_call_is(BxRunBeforeWalk + 4, PilotWorker)
            || !fx_code_matches(BxPassTwoWalk, "6A 02 6A 00 E8") || !fx_call_is(BxPassTwoWalk + 4, PilotWorker)
            || !fx_call_is(BxRunAfterWalk + 0x15, 0x40bd50))
            return "MAG-025 executable walker call sites changed";
        if (!fx_code_matches(BxOppDispatch, BxOppDispatchPattern) || !fx_operand_is(BxOppDispatch + 6, BxOppTable))
            return "MAG-030 OPP handler dispatch changed";
        foreach (var (op, handler) in BxOppOps)
            if (FhUtil.get_at<uint>(BxOppTable + op * 4) != (uint)bx_va(handler)) return $"MAG-030 OPP op {op} handler changed";
        if (!fx_code_matches(BxFallbackDispatch, BxFallbackPattern) || !fx_call_is(BxFallbackDispatch, RenderGetExec)
            || !fx_call_is(BxFallbackDispatch + 0xa, EngineAddresses.MsEffectProcess))
            return "MAG-008 exec==0 fallback dispatch changed";
        if (!fx_code_matches(BxShaderSetup, BxShaderPattern)
            || *(uint*)(nint)(*(uint*)FhUtil.ptr_at<byte>(BxShaderSetup + 0x1c)) != 0x002f4c47)
            return "MAG-019 VFX shader setup changed";
        if (!fx_code_matches(BxNodeWalkSite, BxNodeWalkSitePattern) || !fx_call_is(BxNodeWalkSite + 0xc, BxNodeWalk)
            || !fx_code_matches(BxNodeWalk, BxNodeWalkPattern) || !fx_call_is(BxNodeWalk + 0x11, BxNodeStep)
            || !fx_code_matches(BxNodeStep, BxNodeStepPattern))
            return "MAG-020 family-X node walk changed";
        if (!fx_code_matches(BxRecordStepSite, BxRecordStepSitePattern) || !fx_call_is(BxRecordStepSite + 0xa, BxRecordStep)
            || !fx_code_matches(BxRecordStep, BxRecordStepPattern) || !fx_call_is(BxRecordStep + 0x13, EngineAddresses.PppRunPart))
            return "MAG-026 family-X record particle program call changed";
        if (!fx_code_matches(BxMagicFileStart, "83 3D ?? ?? ?? ?? FF") || !fx_operand_is(BxMagicFileStart + 2, BxMagicHandle)
            || !fx_code_matches(BxMagicFileStart + 0x1c, "81 3D ?? ?? ?? ?? 9E 02 00 00") || !fx_operand_is(BxMagicFileStart + 0x1e, BxMagicId)
            || !fx_code_matches(BxMagicFileStart + 0x34, "FF 15") || !fx_operand_is(BxMagicFileStart + 0x36, BxInitMagicPrx))
            return "magic overlay handle, id or InitMagicPRX cell changed";
        return null;
    }

    private bool init_battle_effect_pause()
    {
        if (bx_contract_error() is { } error)
        {
            _logger.Error($"[Vrr] Battle effect pause refused, battle and magic overlays keep the native clock: {error}.");
            return false;
        }
        if (!hook_or_log(HookSiteId.BattleEffectPauseMAG028ParticlePauseSetter, "MAG-028 particle pause setter", BxPauseSetter,
                () => new FhCallHandle<d_bx_setter>(new FhCallLocation(BxPauseSetter, 0)).hook(this, with_hook_continuation<d_bx_setter>(h_bx_pause_setter))))
        {
            _logger.Error("[Vrr] Battle effect pause refused: the pause setter hook could not be installed; no code was patched.");
            return false;
        }
        byte* data = (byte*)NativeMemory.AllocZeroed(0x20);
        nint code = VirtualAlloc(0, 0x1000, 0x3000, 0x04);
        nint pool = VirtualAlloc(0, BxPoolSize, 0x3000, 0x40);
        if (code == 0 || pool == 0)
        {
            _logger.Error("[Vrr] Battle effect pause refused: no memory for its stubs; no code was patched.");
            return false;
        }
        _bx = data;
        s_battle_effects = this;
        _bxPool = pool;
        *(uint*)(data + 0x4) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&bx_on_stream_entry;
        *(uint*)(data + 0x8) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_stream_next;
        *(uint*)(data + 0xc) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, int>)&bx_on_follow;
        *(uint*)(data + 0x10) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, int, int, uint>)&bx_on_driver;

        uint Va(nint rva) => (uint)bx_va(rva);
        uint d = (uint)(nint)data;
        var sites = new OpuSites(Va(BxNext), Va(BxSentry + 7), Va(BxHandler + 7), Va(BxNext + 7), Va(BxGate + 7),
            Va(BxFollowReturn), *(uint*)FhUtil.ptr_at<byte>(BxFollowSite + 0x15), Va(BxFollowAttach), Va(BxFreshWait + 6));
        byte[] opu = OpuStubs((uint)code, sites, new OpuData(d + 1, d + 2, d + 3, d + 4, d + 8, d + 0xc), BxDeniedOpcodes, bx_opu_handlers(), out var at);
        int shader = (opu.Length + 15) & ~15;
        byte[] guard = ShaderSetupStub((uint)code + (uint)shader, Va(BxShaderSetup),
            new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(BxShaderSetup), 9));
        int adapter = (shader + guard.Length + 15) & ~15;
        if (adapter + FastcallAdapter.Length > 0x1000)
        {
            _logger.Error("[Vrr] Battle effect pause refused: its stubs exceed their page; no code was patched.");
            return false;
        }
        opu.CopyTo(new Span<byte>((void*)code, opu.Length));
        guard.CopyTo(new Span<byte>((void*)(code + shader), guard.Length));
        FastcallAdapter.CopyTo(new Span<byte>((void*)(code + adapter), FastcallAdapter.Length));
        if (!VirtualProtect(code, 0x1000, 0x20, out _) || !FlushInstructionCache(-1, code, 0x1000))
        {
            _logger.Error("[Vrr] Battle effect pause refused: its stubs could not be made executable; no code was patched.");
            return false;
        }
        _bxAdapter = code + adapter;
        _bxNodeWalkOriginal = bx_va(BxNodeWalk);
        _bxFindUnitFn = bx_va(BxFindUnit);
        foreach (var (op, handler) in BxOppOps) _bxOppOriginal[op] = bx_va(handler);

        try
        {
            void Jump(nint site, int offset, int length) => _patches.Write(site, JumpPatch(Va(site), (uint)code + (uint)offset, length));
            Jump(BxSentry, at.Sentry, 7);
            Jump(BxHandler, at.Handler, 7);
            Jump(BxDec, at.Dec, 6);
            Jump(BxPcCommit, at.PcCommit, 5);
            Jump(BxFreshWait, at.FreshWait, 6);
            Jump(BxNext, at.Next, 7);
            Jump(BxGate, at.Gate, 7);
            Jump(BxFollowCall, at.Follow, (int)(BxFollowReturn - BxFollowCall));
            Jump(BxShaderSetup, shader, 9);
            nint[] wrappers =
            [
                0,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_opp_1,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_opp_9,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_opp_10,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_opp_17,
            ];
            for (int i = 0; i < BxOppOps.Length; i++)
            {
                uint wrapper = (uint)wrappers[i + 1];
                _patches.Write(BxOppTable + BxOppOps[i].Op * 4, new ReadOnlySpan<byte>(&wrapper, 4));
            }
            uint walk = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, void>)&bx_on_node_walk;
            byte[] call = JumpPatch(Va(BxNodeWalkSite + 0xc), walk, 5);
            call[0] = 0xe8;
            _patches.Write(BxNodeWalkSite + 0xc, call);
        }
        catch (Exception patchError)
        {
            _logger.Error($"[Vrr] Battle effect pause refused while patching, battle keeps the native clock: {patchError.Message}");
            return false;
        }
        _battleEffectPauseInstalled = true;
        _logger.Info("[Vrr] Battle effect pause installed (MAG-006/008/018/019/020/023/024/025/026/028/029/030, MAG-016 per overlay): " +
                     "presentation passes use guarded battle-effect adapters; handler purity remains unqualified.");
        return true;
    }

    /// <summary>Called once per main-loop pass after the pass kind is known.</summary>
    private void battle_effects_begin_pass(bool presentation)
    {
        if (!_battleEffectPauseInstalled) return;
        try { bx_track_overlay(); }
        catch (Exception error)
        {
            _bxRepeat.Clear();
            _bxOverlay = null;
            if (++_bxTrackFailures <= 4) _logger.Error($"[Vrr] Battle effect pause could not inspect the magic overlay: {error.Message}");
        }
        battle_effects_set_pass(presentation);
        if (presentation) { _bxPresented = true; return; }
        if (!_bxPresented) return;
        _bxPresented = false;
        bx_reset_stale_pause();
    }

    private string battle_effect_counts()
        => !_battleEffectPauseInstalled ? "bx=off"
            : $"bx_walks={_bxHeldWalks} bx_streams={_bxRepeat.Ran}/{_bxRepeat.Blocked} bx_opp={_bxOppRestored}/{_bxOppEnded} " +
              $"bx_nodes={_bxNodeDraws} bx_drivers={_bxDriverHolds} bx_resets={_bxPauseResets} bx_follow={_bxFollowSkips} " +
              $"bx_overlay={_bxOverlay?.Name ?? "-"}";

    // A presentation draw writes (exec == 0) into an overlay's pause cache and the particle pause flag;
    // a native logic pass never sees either, so its update would stall behind them.
    private void bx_reset_stale_pause()
    {
        if (_bxOverlay is { Setter: { } setter, SetterBytes: { } bytes } overlay
            && MemoryProbe.TryCopy(overlay.Base + setter.Offset, _bxOppBuffer.AsSpan(0, bytes.Length))
            && _bxOppBuffer.AsSpan(0, bytes.Length).SequenceEqual(bytes))
        {
            nint cell = (nint)setter.PointerCell, cache = (nint)setter.Cache;
            nint flag = MemoryProbe.IsReadable(cell, 4) ? (nint)(*(uint*)cell) : 0;
            bool stale = cache == 0 || (MemoryProbe.IsReadable(cache, 4) && *(int*)cache == 1);
            if (cache != 0 && stale) { *(int*)cache = 0; _bxPauseResets++; }
            if (stale && flag != 0 && MemoryProbe.IsReadable(flag, 4) && *(int*)flag == 1) { *(int*)flag = 0; _bxPauseResets++; }
        }
        if (FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) == 1 && Volatile.Read(ref _bxWantedPause) == 0)
        {
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
            _bxPauseResets++;
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_bx_pause_setter(int value)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_bx_setter>(new FhCallLocation(BxPauseSetter, 0)).chain_from(h_bx_pause_setter).fnptr)(value);
            return;
        }

        Volatile.Write(ref _bxWantedPause, value);
        new FhCallHandle<d_bx_setter>(new FhCallLocation(BxPauseSetter, 0)).chain_from(h_bx_pause_setter).fnptr!(value);
    }

    // The attach follow handler reads the target unit's +0xBC without a check; a held DLL walk reaches it before the target exists.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int bx_on_follow(nint unit)
    {
        if (s_battle_effects?.RuntimeEffectsArmed != true) return 0;
        try
        {
            var module = s_battle_effects;
            if (module is null) return 0;
            nint obj = (nint)(*(uint*)(unit + 0x220));
            if (!MemoryProbe.IsReadable(obj, 0xa2)) return 0;
            nint target = ((delegate* unmanaged[Cdecl]<nint, nint, ushort, nint>)module._bxFindUnitFn)(unit + 0x300, obj, *(ushort*)(obj + 0xa0));
            if (target != 0 && MemoryProbe.IsReadable(target + 0xbc, 4) && *(uint*)(target + 0xbc) != 0) return 0;
            module._bxFollowSkips++;
            return 1;
        }
        catch (Exception) { return 0; }
    }

    // ---- MAG-016: PPP record driver of the current overlay ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint bx_on_driver(nint list, int param, int cookie)
    {
        var module = s_battle_effects;
        if (module is null) { Environment.FailFast("Battle driver has no owning module."); return 0; }
        if (!module.RuntimeEffectsArmed)
        {
            if ((uint)cookie >= (uint)module._bxDrivers.Count || module._bxAdapter == 0
                || module._bxDrivers[cookie].Trampoline == 0)
            { Environment.FailFast("Battle driver native continuation is unavailable."); return 0; }
            return ((delegate* unmanaged[Cdecl]<nint, int, nint, uint>)module._bxAdapter)(
                list, param, module._bxDrivers[cookie].Trampoline);
        }
        var (headOperand, trampoline) = module._bxDrivers[cookie];
        var call = (delegate* unmanaged[Cdecl]<nint, int, nint, uint>)module._bxAdapter;
        const int Limit = 128;
        nint* records = stackalloc nint[Limit];
        uint* fields = stackalloc uint[Limit * 5];
        int count = 0;
        try
        {
            nint cell = headOperand != 0 ? (nint)(*(uint*)headOperand) : list + 0x30;
            nint node = MemoryProbe.IsReadable(cell, 4) ? (nint)(*(uint*)cell) : 0;
            for (int guard = 0; node != 0 && count < Limit && guard < 4096 && MemoryProbe.IsReadable(node, 0xc); guard++)
            {
                nint record = (nint)(*(uint*)(node + 8));
                if (record != 0 && MemoryProbe.IsReadable(record, 0x126))
                {
                    uint* f = fields + count * 5;
                    f[0] = *(uint*)(record + 0x100); f[1] = *(uint*)(record + 0x104); f[2] = *(uint*)(record + 0x110);
                    f[3] = *(uint*)(record + 0x114); f[4] = *(uint*)(record + 0x118);
                    *(uint*)(record + 0x100) = 0; *(uint*)(record + 0x104) = 0; *(uint*)(record + 0x110) = 0;
                    records[count++] = record;
                }
                node = (nint)(*(uint*)(node + 4));
            }
        }
        catch (Exception) { }
        uint result = call(list, param, trampoline);
        for (int i = 0; i < count; i++)
        {
            nint record = records[i];
            if ((*(ushort*)(record + 0x124) & 0x8000) == 0) continue;
            uint* f = fields + i * 5;
            *(uint*)(record + 0x100) = f[0]; *(uint*)(record + 0x104) = f[1]; *(uint*)(record + 0x110) = f[2];
            *(uint*)(record + 0x114) = f[3]; *(uint*)(record + 0x118) = f[4];
        }
        if (count > 0) module._bxDriverHolds++;
        return result;
    }

    // ---- current magic overlay ----

    private void bx_track_overlay()
    {
        if (FhUtil.get_at<int>(BxMagicHandle) == -1) { _bxRepeat.Clear(); return; }
        nint init = (nint)FhUtil.get_at<uint>(BxInitMagicPrx);
        if (init == 0) { _bxRepeat.Clear(); return; }
        var key = (init, FhUtil.get_at<int>(BxMagicId));
        if (_bxOverlay is { } current && _bxOverlayKey == key && bx_overlay_patched(current)) return;
        _bxRepeat.Clear();
        _bxOverlayKey = key;
        _bxOverlay = bx_prepare_overlay(init);
    }

    private static bool bx_overlay_patched(BxOverlay overlay)
    {
        Span<byte> bytes = stackalloc byte[16];
        foreach (var (site, _, patch) in overlay.Patches)
            if (!MemoryProbe.TryCopy(site, bytes[..patch.Length]) || !bytes[..patch.Length].SequenceEqual(patch)) return false;
        return true;
    }

    private BxOverlay? bx_prepare_overlay(nint init)
    {
        if (VirtualQuery(init, out MEMORY_BASIC_INFORMATION info, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0
            || info.Type != 0x1000000 || info.State != 0x1000) return null;
        nint image = info.AllocationBase;
        Span<byte> header = stackalloc byte[0x400];
        if (!MemoryProbe.TryCopy(image, header) || header[0] != 'M' || header[1] != 'Z') return null;
        int nt = BitConverter.ToInt32(header[0x3c..]);
        if (nt is < 0 or > 0x3a8 || BitConverter.ToUInt32(header[nt..]) != 0x4550) return null;
        int size = BitConverter.ToInt32(header[(nt + 0x50)..]);
        if (size is <= 0 or > 0x4000000) return null;
        char* path = stackalloc char[260];
        uint chars = GetModuleFileNameW(image, path, 260);
        string name = chars == 0 ? $"0x{image:X8}" : Path.GetFileName(new string(path, 0, (int)chars));
        if (!name.StartsWith("magic_", StringComparison.OrdinalIgnoreCase)) return null;
        if (_bxOverlays.TryGetValue((image, name), out var cached))
        {
            bx_apply_overlay(cached);
            return cached;
        }
        byte[] bytes = new byte[size];
        if (!MemoryProbe.TryCopy(image, bytes)) return null;
        var overlay = new BxOverlay(image, name);
        bx_scan_overlay(overlay, bytes);
        _bxOverlays[(image, name)] = overlay;
        bx_apply_overlay(overlay);
        _logger.Info($"[Vrr] Battle effect pause: {name} at 0x{image:X8}: " +
                     (overlay.Rows.Count == 0 ? "no overlay rows apply." : string.Join(", ", overlay.Rows) + "."));
        return overlay;
    }

    private void bx_scan_overlay(BxOverlay overlay, byte[] image)
    {
        uint low = (uint)overlay.Base, high = low + (uint)image.Length;
        uint presentation = (uint)(nint)_bx;
        uint Va(int offset) => low + (uint)offset;

        if (FindPauseSetter(image, low, high) is { } setter)
        {
            overlay.Setter = setter;
            overlay.SetterBytes = image.AsSpan(setter.Offset, setter.Cache == 0 ? 21 : 51).ToArray();
            overlay.Rows.Add(setter.Cache == 0 ? "MAG-018 direct pause flag" : "MAG-018 pause cache");
        }

        int drivers = Count(image, PppDriverInner, out int inner);
        if (drivers == 1 && FindDriverEntry(image, inner) is { PrologueLength: 9 or 6 } driver)
        {
            byte[] prologue = image.AsSpan(driver.Entry, driver.PrologueLength).ToArray();
            int cookie = _bxDrivers.Count;
            uint origin = bx_pool(64);
            if (origin != 0)
            {
                byte[] stub = DriverStub(origin, presentation, presentation + 0x10, (uint)cookie, Va(driver.Entry), prologue, out int trampoline);
                stub.CopyTo(new Span<byte>((void*)(nint)origin, stub.Length));
                _bxDrivers.Add((driver.HeadOperand < 0 ? 0 : (nint)Va(driver.HeadOperand), (nint)(origin + (uint)trampoline)));
                overlay.Patches.Add(((nint)Va(driver.Entry), prologue, JumpPatch(Va(driver.Entry), origin, driver.PrologueLength)));
                overlay.Rows.Add($"MAG-016 driver +0x{driver.Entry:X}");
            }
        }
        else if (drivers > 1) overlay.Rows.Add($"MAG-016 refused ({drivers} driver bodies)");

        int scripts = Count(image, MotionScript, out int script);
        if (scripts == 1 && bx_pool(48) is var scriptOrigin and not 0)
        {
            byte[] first = image.AsSpan(script, 7).ToArray();
            byte[] stub = MotionScriptStub(scriptOrigin, presentation, Va(script), first);
            stub.CopyTo(new Span<byte>((void*)(nint)scriptOrigin, stub.Length));
            overlay.Patches.Add(((nint)Va(script), first, JumpPatch(Va(script), scriptOrigin, 7)));
            overlay.Rows.Add($"MAG-023 motion script +0x{script:X}");
        }
        else if (scripts > 1) overlay.Rows.Add($"MAG-023 refused ({scripts} motion scripts)");

        int arcs = Count(image, ArcNode, out int arc, at => U32(image, at + 5) == U32(image, at + 15));
        if (arcs == 1 && bx_pool(48) is var arcOrigin and not 0)
        {
            byte[] prologue = image.AsSpan(arc, 9).ToArray();
            byte[] stub = ArcNodeStub(arcOrigin, presentation, Va(arc), prologue);
            stub.CopyTo(new Span<byte>((void*)(nint)arcOrigin, stub.Length));
            overlay.Patches.Add(((nint)Va(arc), prologue, JumpPatch(Va(arc), arcOrigin, 9)));
            overlay.Rows.Add($"MAG-024 arc node +0x{arc:X}");
        }
        else if (arcs > 1) overlay.Rows.Add($"MAG-024 refused ({arcs} arc nodes)");
        FlushInstructionCache(-1, _bxPool, BxPoolSize);
    }

    private uint bx_pool(int length)
    {
        if (_bxPoolUsed + length > BxPoolSize)
        {
            if (_bxPoolUsed != int.MaxValue) _logger.Error("[Vrr] Battle effect pause: overlay stub pool exhausted; later overlays are left unpatched.");
            _bxPoolUsed = int.MaxValue;
            return 0;
        }
        uint origin = (uint)(_bxPool + _bxPoolUsed);
        _bxPoolUsed += (length + 15) & ~15;
        return origin;
    }

    // Writes each patch whose site still holds the bytes it was scanned with; a site that already
    // carries the patch is a reload the loader did not replace.
    private void bx_apply_overlay(BxOverlay overlay)
    {
        Span<byte> current = stackalloc byte[16];
        foreach (var (site, original, patch) in overlay.Patches)
        {
            if (!MemoryProbe.TryCopy(site, current[..patch.Length])) continue;
            if (current[..patch.Length].SequenceEqual(patch)) continue;
            if (!current[..original.Length].SequenceEqual(original)) continue;
            if (!VirtualProtect(site, (nuint)patch.Length, 0x40, out uint previous)) continue;
            patch.CopyTo(new Span<byte>((void*)site, patch.Length));
            VirtualProtect(site, (nuint)patch.Length, previous, out _);
            FlushInstructionCache(-1, site, (nuint)patch.Length);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
