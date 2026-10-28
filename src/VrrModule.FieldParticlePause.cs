namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Field particles on presentation-only passes (port of upstream FFX-60fps FX-010). Those passes
///     run the native field step with ppvUserStopPartF set and emission skipped, so ages, clocks and
///     update kernels hold while every draw op still draws. Four native contracts break under that
///     pause and are repaired here: two camera-shape draw ops and the pyrefly draw op skip their whole
///     draw while paused (DRAWOPS), one keyed draw op adds its rates on any call whose key matches
///     (KEYADD), and the dispatcher tail restarts and frees objects after the logic draw, which native
///     code never shows before the next logic step (LOOPDEFER, FREEDEFER).
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint FpStep = 0x3122f0, FpStepEmitCall = 0x31231a, FpStepPauseCompare = 0x312362;
    private static readonly (nint Gate, nint Spawn, nint Draw, nint Integrate)[] FieldShapeCameraOps =
        [(0x347713, 0x347090, 0x3467b0, 0x3472b0), (0x348963, 0x3482e0, 0x3479d0, 0x348500)];
    private const nint PyreflyGateCompare = 0x35068e, PyreflyGateJump = 0x35069b, PyreflyInnerPause = 0x35164e;
    private const nint KeyedAddDrawOp = 0x35af80;
    private const nint LoopRestartSite = 0x317236, FreeCheckSite = 0x3173cc, FreeCheckJump = 0x3173d7;
    private const nint FreeCheckAlive = 0x317248, FreeCheckDie = 0x3173e0;
    private const nint ParticlePointerEncode = 0x644700, ParticlePointerDecode = 0x644650;

    private const string FpStepPattern = "55 8B EC 53 8B 5D 0C 56 8B 75 08 89 35 ?? ?? ?? ?? F6 C3 10 74 09 56 E8 ?? ?? ?? ?? 83 C4 04 56 C7 05 ?? ?? ?? ?? 00 00 00 00 E8";
    private const string ShapeCameraGatePattern = "83 3D ?? ?? ?? ?? 00 75 30 A1 ?? ?? ?? ?? 8B 7D 0C 80 78 1D 00 75 0D FF 75 10 57 53 E8 ?? ?? ?? ?? 83 C4 0C 56 FF 75 10 57 53 E8 ?? ?? ?? ?? 56 57 E8 ?? ?? ?? ?? 83 C4 18 5F 5E 5B 8B E5 5D C3";
    private const string PyreflyGatePattern = "83 3D ?? ?? ?? ?? 00 89 85 94 FE FF FF 0F 85 ?? ?? ?? ?? 83 3D ?? ?? ?? ?? 00 8B 8D A8 FE FF FF";
    private const string PyreflyInnerPattern = "83 3D ?? ?? ?? ?? 00 75 16 8B 85 A0 FE FF FF 8B 8D B4 FE FF FF 8B 40 08 01 84 39 90 02 00 00";
    private const string KeyedAddPattern = "53 8B DC 83 EC 08 83 E4 F0 83 C4 04 55 8B 6B 04 89 6C 24 04 8B EC 83 EC 38 A1 ?? ?? ?? ?? 33 C5 89 45 FC 8B 43 10 8B 4B 0C 8B 40 0C 56 8B 00 57 8B 7B 08 8D B7 A0 00 00 00 03 F0 8B 01 3B 47 0C 75 32 D9 41 08";
    private const string LoopRestartPattern = "8B 4B 18 56 89 4E 0C E8 ?? ?? ?? ?? 8B 5D F4 83 C4 04";
    private const string FreeCheckPattern = "8B 43 14 8B 5D F4 0B D8 89 5D F4 39 46 0C 0F 82 ?? ?? ?? ?? 57 E8 ?? ?? ?? ?? 8B 4D E0 6A 00 56 89 01 E8 ?? ?? ?? ?? 83 C4 0C E9";
    private const string IdentityPointerPattern = "55 8B EC 8B 45 08 5D C3";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_fx_shape_spawn(nint obj, nint data, nint descriptor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_fx_shape_integrate(nint obj, nint work);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int d_fx_free_check(nint obj);
    private d_fx_free_check? _fieldFreeCheck;
    private nint _fieldFreeStub;

    private static bool fx_code_matches(nint rva, string pattern)
    {
        int length = FieldParticlePauseCode.PatternLength(pattern);
        byte* at = FhUtil.ptr_at<byte>(rva);
        return MemoryProbe.IsReadable((nint)at, length) && FieldParticlePauseCode.Matches(new ReadOnlySpan<byte>(at, length), pattern);
    }

    private static bool fx_operand_is(nint rva, nint target)
        => *(uint*)FhUtil.ptr_at<byte>(rva) == (uint)(nuint)FhUtil.ptr_at<byte>(target);

    private static bool fx_call_is(nint rva, nint target)
        => (*FhUtil.ptr_at<byte>(rva) is 0xe8 or 0xe9) && rva + 5 + *(int*)FhUtil.ptr_at<byte>(rva + 1) == target;

    private string? fx_field_pause_contract_error()
    {
        if (_particle_lifecycle_result != true || !_fieldPresentationInstalled) return "particle lifetime hooks are not installed";
        if (!fx_code_matches(FpStep, FpStepPattern) || !fx_call_is(FpStepEmitCall, RenderParticleEmit)
            || !fx_code_matches(FpStepPauseCompare, "83 3D ?? ?? ?? ?? 00") || !fx_operand_is(FpStepPauseCompare + 2, EngineAddresses.PpvUserStopPartF)
            || !fx_call_is(0x312343, 0x317010))
            return "field step layout (pause compare, emission or dispatcher call) changed";
        foreach (var op in FieldShapeCameraOps)
            if (!fx_code_matches(op.Gate, ShapeCameraGatePattern) || !fx_operand_is(op.Gate + 2, EngineAddresses.PpvUserStopPartF)
                || !fx_operand_is(op.Gate + 0xa, PilotCurrentManager)
                || !fx_call_is(op.Gate + 0x1c, op.Spawn) || !fx_call_is(op.Gate + 0x2a, op.Draw) || !fx_call_is(op.Gate + 0x31, op.Integrate)
                || !fx_code_matches(op.Spawn, "55 8B EC 83 EC 58 A1") || !fx_code_matches(op.Integrate, "55 8B EC 83 EC 08 8B 45 0C 53 56 8B 75 08 57"))
                return $"DRAWOPS camera shape gate 0x{op.Gate:X} changed";
        if (!fx_code_matches(PyreflyGateCompare, PyreflyGatePattern) || !fx_operand_is(PyreflyGateCompare + 2, EngineAddresses.PpvUserStopPartF)
            || !fx_code_matches(PyreflyInnerPause, PyreflyInnerPattern) || !fx_operand_is(PyreflyInnerPause + 2, EngineAddresses.PpvUserStopPartF)
            || PyreflyGateJump + 6 + *(int*)FhUtil.ptr_at<byte>(PyreflyGateJump + 2) != PyreflyInnerPause + 0x1f)
            return "DRAWOPS pyrefly gate or its inner +0x290 pause check changed";
        if (!fx_code_matches(KeyedAddDrawOp, KeyedAddPattern)) return "KEYADD keyed draw op changed";
        if (!fx_code_matches(LoopRestartSite, LoopRestartPattern) || !fx_call_is(LoopRestartSite + 7, 0x312d20))
            return "LOOPDEFER restart call changed";
        if (!fx_code_matches(FreeCheckSite, FreeCheckPattern)
            || FreeCheckJump + 9 + *(int*)FhUtil.ptr_at<byte>(FreeCheckJump + 5) != FreeCheckAlive
            || !fx_code_matches(FreeCheckAlive, "89 75 E0 8B F7") || !fx_code_matches(FreeCheckDie, "57 E8")
            || !fx_call_is(FreeCheckDie + 1, ParticlePointerEncode) || !fx_call_is(FreeCheckSite + 0x22, 0x3168f0)
            || !fx_code_matches(ParticlePointerEncode, IdentityPointerPattern) || !fx_code_matches(ParticlePointerDecode, IdentityPointerPattern))
            return "FREEDEFER free check, its helpers or raw particle pointer encoding changed";
        return null;
    }

    private bool init_field_particle_pause()
    {
        if (fx_field_pause_contract_error() is { } error)
        {
            _logger.Error($"[Vrr] Field particle pause refused, presentation passes stay off: {error}.");
            return false;
        }
        bool ok = true;
        ok &= hook_or_log(HookSiteId.FieldParticlePauseFX010CameraShapeSpawn, "FX-010 camera shape spawn", 0x347090,
            () => new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x347090, 0)).hook(this, with_hook_continuation<d_fx_shape_spawn>(h_fx_shape_spawn_a)));
        ok &= hook_or_log(HookSiteId.FieldParticlePauseFX010CameraShapeIntegrate, "FX-010 camera shape integrate", 0x3472b0,
            () => new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x3472b0, 0)).hook(this, with_hook_continuation<d_fx_shape_integrate>(h_fx_shape_integrate_a)));
        ok &= hook_or_log(HookSiteId.FieldParticlePauseFX010CameraShapeDisPosSpawn, "FX-010 camera shape DisPos spawn", 0x3482e0,
            () => new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x3482e0, 0)).hook(this, with_hook_continuation<d_fx_shape_spawn>(h_fx_shape_spawn_b)));
        ok &= hook_or_log(HookSiteId.FieldParticlePauseFX010CameraShapeDisPosIntegrate, "FX-010 camera shape DisPos integrate", 0x348500,
            () => new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x348500, 0)).hook(this, with_hook_continuation<d_fx_shape_integrate>(h_fx_shape_integrate_b)));
        ok &= hook_or_log(HookSiteId.FieldParticlePauseFX010KeyedDrawOp, "FX-010 keyed draw op", KeyedAddDrawOp,
            () => new FhCallHandle<d_pilot_kernel>(new FhCallLocation(KeyedAddDrawOp, 0)).hook(this, with_hook_continuation<d_pilot_kernel>(h_fx_keyed_add)));
        if (!ok)
        {
            _logger.Error("[Vrr] Field particle pause refused: a draw-op hook could not be installed; no code was patched.");
            return false;
        }

        _fieldFreeCheck = obj => RuntimeEffectsArmed && _fieldDeferral.TryDeferFree(obj) ? 1 : 0;
        _fieldFreeStub = VirtualAlloc(0, 0x1000, 0x3000, 0x04);
        if (_fieldFreeStub == 0)
        {
            _logger.Error("[Vrr] Field particle pause refused: no memory for the free-check stub; no code was patched.");
            return false;
        }
        byte[] stub = FieldParticlePauseCode.FreeStub((uint)_fieldFreeStub,
            (uint)(nuint)FhUtil.ptr_at<byte>(FreeCheckAlive), (uint)(nuint)FhUtil.ptr_at<byte>(FreeCheckDie));
        stub.CopyTo(new Span<byte>((void*)_fieldFreeStub, stub.Length));
        *(uint*)(_fieldFreeStub + FieldParticlePauseCode.FreeStubLength - 4) = (uint)Marshal.GetFunctionPointerForDelegate(_fieldFreeCheck);
        if (!VirtualProtect(_fieldFreeStub, 0x1000, 0x20, out _) || !FlushInstructionCache(-1, _fieldFreeStub, 0x1000))
        {
            _logger.Error("[Vrr] Field particle pause refused: the free-check stub could not be made executable; no code was patched.");
            return false;
        }

        foreach (var op in FieldShapeCameraOps) _patches.Write(op.Gate + 7, [0x90, 0x90]);
        _patches.Write(PyreflyGateJump, [0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);
        byte[] jump = [0xe9, 0, 0, 0, 0, 0x90, 0x90, 0x90, 0x90];
        uint site = (uint)(nuint)FhUtil.ptr_at<byte>(FreeCheckJump);
        BitConverter.GetBytes(unchecked((uint)_fieldFreeStub - (site + 5))).CopyTo(jump, 1);
        _patches.Write(FreeCheckJump, jump);
        _fieldPauseInstalled = true;
        _logger.Info("[Vrr] Field particle pause installed: draw ops draw while paused, keyed adds held, loop restarts and frees deferred to the next logic step.");
        return true;
    }

    private static bool fx_particles_paused() => FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) != 0;

    // The native gate skipped these calls whenever paused; the gate now draws, so they keep skipping.
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fx_shape_spawn_a(nint obj, nint data, nint descriptor)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x347090, 0)).chain_from(h_fx_shape_spawn_a).fnptr)(obj, data, descriptor);
            return;
        }

        if (!fx_particles_paused())
            new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x347090, 0)).chain_from(h_fx_shape_spawn_a).fnptr!(obj, data, descriptor);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fx_shape_spawn_b(nint obj, nint data, nint descriptor)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x3482e0, 0)).chain_from(h_fx_shape_spawn_b).fnptr)(obj, data, descriptor);
            return;
        }

        if (!fx_particles_paused())
            new FhCallHandle<d_fx_shape_spawn>(new FhCallLocation(0x3482e0, 0)).chain_from(h_fx_shape_spawn_b).fnptr!(obj, data, descriptor);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fx_shape_integrate_a(nint obj, nint work)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x3472b0, 0)).chain_from(h_fx_shape_integrate_a).fnptr)(obj, work);
            return;
        }

        if (!fx_particles_paused())
            new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x3472b0, 0)).chain_from(h_fx_shape_integrate_a).fnptr!(obj, work);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fx_shape_integrate_b(nint obj, nint work)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x348500, 0)).chain_from(h_fx_shape_integrate_b).fnptr)(obj, work);
            return;
        }

        if (!fx_particles_paused())
            new FhCallHandle<d_fx_shape_integrate>(new FhCallLocation(0x348500, 0)).chain_from(h_fx_shape_integrate_b).fnptr!(obj, work);
    }

    // The op adds two rates whenever its key equals the object age and has no pause check of its own.
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fx_keyed_add(nint obj, nint data, nint descriptor)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_pilot_kernel>(new FhCallLocation(KeyedAddDrawOp, 0)).chain_from(h_fx_keyed_add).fnptr)(obj, data, descriptor);
            return;
        }

        int* key = null;
        int saved = 0;
        if (fx_particles_paused() && MemoryProbe.IsReadable(data, 4) && MemoryProbe.IsReadable(obj, 0x10)
            && *(int*)data == *(int*)(obj + 0xc))
        {
            key = (int*)data;
            saved = *key;
            *key = saved + 1;
        }
        try
        {
            new FhCallHandle<d_pilot_kernel>(new FhCallLocation(KeyedAddDrawOp, 0)).chain_from(h_fx_keyed_add).fnptr!(obj, data, descriptor);
        }
        finally { if (key != null) *key = saved; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
}
