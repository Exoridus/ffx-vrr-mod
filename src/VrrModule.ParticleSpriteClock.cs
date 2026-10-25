namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint ParticleShape = 0x340a70, ParticleShapeX = 0x340f60;
    private readonly ParticleSpriteClock _particle_sprite_clock = new();
    private bool _particle_sprite_installed, _particle_sprite_refused;
    private bool? _particle_lifecycle_result;
    private d_particle_sprite_draw? _particle_shape_original, _particle_shape_x_original;
    private d_pilot_create? _particle_create_original;
    private d_pilot_object? _particle_delete_original, _particle_reset_original, _particle_free_original;

    private bool particle_lifecycle_bindings_match()
    {
        foreach (nint target in new nint[] { 0x316770, 0x3168f0, 0x312d20, 0x3123c0, 0x311f20 })
        {
            byte* entry = FhUtil.ptr_at<byte>(target);
            if (!MemoryProbe.IsReadable((nint)entry, 3)
                || !new ReadOnlySpan<byte>(entry, 3).SequenceEqual<byte>([0x55, 0x8b, 0xec])) return false;
        }
        return true;
    }

    private bool preflight_particle_sprite_clock()
    {
        if (!_config.ParticleSpriteClock) return true;
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Particle sprite clock refused: {error}");
            return false;
        }
        foreach (nint target in new nint[] { ParticleShape, ParticleShapeX })
        {
            byte* entry = FhUtil.ptr_at<byte>(target);
            int pause = target == ParticleShape ? 0x3b7 : 0x467;
            int step = target == ParticleShape ? 0x3c3 : 0x473;
            if (!MemoryProbe.IsReadable((nint)entry, step + 7)
                || !new ReadOnlySpan<byte>(entry, 22).SequenceEqual<byte>(
                    [0x53, 0x8b, 0xdc, 0x83, 0xec, 0x08, 0x83, 0xe4, 0xf0, 0x83, 0xc4, 0x04,
                     0x55, 0x8b, 0x6b, 0x04, 0x89, 0x6c, 0x24, 0x04, 0x8b, 0xec])
                || entry[pause] != 0x83 || entry[pause + 1] != 0x3d || entry[pause + 6] != 0
                || *(uint*)(entry + pause + 2) != (uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.PpvUserStopPartF)
                || !new ReadOnlySpan<byte>(entry + step, 7).SequenceEqual<byte>([0x66, 0x8b, 0x40, 0x08, 0x66, 0x01, 0x06]))
            {
                _logger.Error($"[Vrr] Particle sprite clock refused: leaf {target:X} changed.");
                return false;
            }
        }
        if (particle_lifecycle_bindings_match()) return true;
        _logger.Error("[Vrr] Particle sprite clock refused: lifetime binding changed.");
        return false;
    }

    private bool init_particle_lifecycle()
    {
        if (_particle_lifecycle_result is { } result) return result;
        _particle_lifecycle_result = false;
        if (!particle_lifecycle_bindings_match()) return false;
        bool ok = hook_or_log("particle object creation", 0x316770,
            () => new FhCallHandle<d_pilot_create>(new FhCallLocation(0x316770, 0)).hook(this, h_particle_pilot_create));
        ok &= hook_or_log("particle object deletion", 0x3168f0,
            () => new FhCallHandle<d_pilot_object>(new FhCallLocation(0x3168f0, 0)).hook(this, h_particle_pilot_delete));
        ok &= hook_or_log("particle object loop reset", 0x312d20,
            () => new FhCallHandle<d_pilot_object>(new FhCallLocation(0x312d20, 0)).hook(this, h_particle_pilot_reset));
        ok &= hook_or_log("particle manager free", 0x311f20,
            () => new FhCallHandle<d_pilot_object>(new FhCallLocation(0x311f20, 0)).hook(this, h_particle_pilot_free));
        _particle_lifecycle_result = ok;
        return ok;
    }

    private bool init_particle_sprite_clock()
    {
        if (!_config.ParticleSpriteClock) return true;
        if (!preflight_particle_sprite_clock() || !init_particle_lifecycle()) return false;
        bool ok = hook_or_log("particle Shape sprite clock", ParticleShape,
            () => new FhCallHandle<d_particle_sprite_draw>(new FhCallLocation(ParticleShape, 0)).hook(this, h_particle_shape));
        ok &= hook_or_log("particle ShapeX sprite clock", ParticleShapeX,
            () => new FhCallHandle<d_particle_sprite_draw>(new FhCallLocation(ParticleShapeX, 0)).hook(this, h_particle_shape_x));
        _particle_sprite_installed = ok;
        return ok;
    }

    private void refuse_particle_sprite_clock()
    {
        if (_particle_sprite_refused) return;
        _particle_sprite_refused = true;
        _logger.Error("[Vrr] Particle sprite clock encountered unowned fractional timing or exceeded its lifetime bound; that draw retains native timing.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_particle_sprite_draw(nint obj, nint operand, nint program);

    private void particle_sprite_draw(nint obj, nint operand, nint program, int operandLength, d_particle_sprite_draw original)
    {
        int paused = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
        if (RenderPassContext.IsPresentation)
        {
            // Zero step still consumes native duration debt; the native pause gate preserves the complete phase.
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
            try { original(obj, operand, program); }
            finally { FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused); }
            return;
        }
        if (paused != 0 || *(uint*)(operand + 4) == 0xffff)
        {
            original(obj, operand, program);
            return;
        }
        nint descriptor = pilot_pointer(program + 0xc);
        int offset = *(int*)descriptor;
        if (!_particle_sprite_clock.TryScale(obj, program, offset, *(short*)(operand + 8), (int)TargetFramerate, out short step))
        {
            refuse_particle_sprite_clock();
            original(obj, operand, program);
            return;
        }
        Span<byte> local = stackalloc byte[operandLength];
        new ReadOnlySpan<byte>((void*)operand, operandLength).CopyTo(local);
        fixed (byte* copy = local)
        {
            *(short*)(copy + 8) = step;
            original(obj, (nint)copy, program);
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_shape(nint obj, nint operand, nint program)
    {
        var original = _particle_shape_original ??= new FhCallHandle<d_particle_sprite_draw>(new FhCallLocation(ParticleShape, 0))
            .chain_from(h_particle_shape).fnptr!;
        particle_sprite_draw(obj, operand, program, 12, original);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_shape_x(nint obj, nint operand, nint program)
    {
        var original = _particle_shape_x_original ??= new FhCallHandle<d_particle_sprite_draw>(new FhCallLocation(ParticleShapeX, 0))
            .chain_from(h_particle_shape_x).fnptr!;
        particle_sprite_draw(obj, operand, program, 16, original);
    }
}
