namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint ParticleUvTs2 = 0x338ec0;
    private const nint ParticleUvTs3 = 0x33a370;
    private bool _particle_uv_unowned_presentation_reported;

    private bool init_particle_uv_hooks()
    {
        if (!_config.ParticleUvClock) return true;
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Particle UV binding refused: {error}");
            return false;
        }

        if (!particle_uv_binding_matches(ParticleUvTs2, 0xb8, 0x56)
            || !particle_uv_binding_matches(ParticleUvTs3, 0xf8, 0x5c))
        {
            _logger.Error("[Vrr] Particle UV binding refused: draw arguments, pause gate or private integration layout changed.");
            return false;
        }

        bool ok = hook_or_log(HookSiteId.ParticleUvPppDrawMdlTs2UVClock, "pppDrawMdlTs2 UV clock", ParticleUvTs2,
            () => new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs2, 0)).hook(this, with_hook_continuation<d_particle_uv_draw>(h_particle_uv_ts2)));
        ok &= hook_or_log(HookSiteId.ParticleUvPppDrawMdlTs3UVClock, "pppDrawMdlTs3 UV clock", ParticleUvTs3,
            () => new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs3, 0)).hook(this, with_hook_continuation<d_particle_uv_draw>(h_particle_uv_ts3)));
        return ok;
    }

    private static bool particle_uv_binding_matches(nint rva, byte frameSize, int integrationOffset)
    {
        byte* entry = FhUtil.ptr_at<byte>(rva);
        return MemoryProbe.IsReadable((nint)entry, integrationOffset + 14)
            && new ReadOnlySpan<byte>(entry, 22).SequenceEqual<byte>(
                [0x53, 0x8b, 0xdc, 0x83, 0xec, 0x08, 0x83, 0xe4, 0xf0, 0x83, 0xc4, 0x04,
                 0x55, 0x8b, 0x6b, 0x04, 0x89, 0x6c, 0x24, 0x04, 0x8b, 0xec])
            && entry[0x16] == 0x81 && entry[0x17] == 0xec
            && *(uint*)(entry + 0x18) == frameSize
            && entry[0x26] == 0x83 && entry[0x27] == 0x3d && entry[0x2c] == 0
            && *(uint*)(entry + 0x28) == (uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.PpvUserStopPartF)
            && new ReadOnlySpan<byte>(entry + 0x2d, 14).SequenceEqual<byte>(
                [0x8b, 0x43, 0x10, 0x8b, 0x53, 0x08, 0x56, 0x8b, 0x73, 0x0c, 0x57, 0x8b, 0x78, 0x0c])
            && new ReadOnlySpan<byte>(entry + 0x41, 3).SequenceEqual<byte>([0x8b, 0x4f, 0x08])
            && new ReadOnlySpan<byte>(entry + integrationOffset, 14).SequenceEqual<byte>(
                [0xd9, 0x84, 0x11, 0xa8, 0, 0, 0, 0xd8, 0x84, 0x11, 0xa4, 0, 0, 0]);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_particle_uv_draw(nint obj, nint data, nint program);

    private void prepare_particle_uv(nint obj, nint program)
    {
        bool paused = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) != 0;
        if (paused) return;
        // Extra images require the manager's pause scope to suppress keyframe additions as well as integration.
        if (RenderPassContext.IsPresentation)
        {
            if (!_particle_uv_unowned_presentation_reported)
            {
                _particle_uv_unowned_presentation_reported = true;
                _logger.Error("[Vrr] Particle UV draw reached an extra image outside its manager pause scope; this draw's timing is unsupported.");
            }
            return;
        }
        float elapsed = 1f / Scale;
        if (elapsed == 1f) return;

        nint descriptor = *(nint*)(program + 0xc);
        int offset = *(int*)(descriptor + 8);
        ParticleUvClock.Prepare(new Span<float>((void*)(obj + offset + 0xa0), 6), elapsed, false);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_uv_ts2(nint obj, nint data, nint program)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs2, 0)).chain_from(h_particle_uv_ts2).fnptr)(obj, data, program);
            return;
        }

        prepare_particle_uv(obj, program);
        new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs2, 0))
            .chain_from(h_particle_uv_ts2).fnptr!(obj, data, program);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_uv_ts3(nint obj, nint data, nint program)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs3, 0)).chain_from(h_particle_uv_ts3).fnptr)(obj, data, program);
            return;
        }

        prepare_particle_uv(obj, program);
        new FhCallHandle<d_particle_uv_draw>(new FhCallLocation(ParticleUvTs3, 0))
            .chain_from(h_particle_uv_ts3).fnptr!(obj, data, program);
    }
}
