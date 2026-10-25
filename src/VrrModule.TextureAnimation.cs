namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint NewTextureAdvance = 0x37f360;
    private const nint NewTextureStepRead = 0x37f3e3;
    private const nint TextureAnimationSlots = 0x1f10000;
    private const int TextureSlotCount = 64;
    private const int TextureRecordCapacity = 64;
    private const int TextureSlotStride = 0x30;
    private const int TextureRecordStride = 0x34;
    private byte* _texture_step_shadow;
    private long _texture_step_calls;

    private string texture_animation_counts() => $"texanim_calls={_texture_step_calls}";

    private bool init_texture_animation_hook()
    {
        if (!_config.TextureAnimationStep) return true;
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Character texture binding refused: {error}");
            return false;
        }
        byte* entry = FhUtil.ptr_at<byte>(NewTextureAdvance);
        byte* step = FhUtil.ptr_at<byte>(NewTextureStepRead);
        if (!MemoryProbe.IsReadable((nint)entry, 0x20) || !MemoryProbe.IsReadable((nint)step, 7)
            || !new ReadOnlySpan<byte>(entry, 9).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x81, 0xec, 0x84, 0, 0, 0])
            || !new ReadOnlySpan<byte>(entry + 0x13, 9).SequenceEqual<byte>([0x8b, 0x45, 0x08, 0x8b, 0x4d, 0x0c, 0x8b, 0x55, 0x10])
            || !new ReadOnlySpan<byte>(step, 3).SequenceEqual<byte>([0x0f, 0xbe, 0x86])
            || *(uint*)(step + 3) != (uint)(nuint)FhUtil.ptr_at<byte>(TextureAnimationSlots + 0xe))
        {
            _logger.Error("[Vrr] Character texture binding refused: native arguments or signed step read changed.");
            return false;
        }

        // The patched operand retains this storage until image rollback at process teardown.
        _texture_step_shadow = (byte*)NativeMemory.AllocZeroed(TextureSlotCount * TextureSlotStride);
        if (!hook_or_log("new-format character texture advance", NewTextureAdvance,
            () => new FhCallHandle<d_new_texture_advance>(new FhCallLocation(NewTextureAdvance, 0)).hook(this, h_new_texture_advance)))
            return false;
        _patches.Write(NewTextureStepRead + 3, BitConverter.GetBytes((uint)(nuint)(_texture_step_shadow + 0xe)));
        return init_texture_presentation();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_new_texture_advance(int slot, nint model, nint renderer);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_new_texture_advance(int slot, nint model, nint renderer)
    {
        var original = new FhCallHandle<d_new_texture_advance>(new FhCallLocation(NewTextureAdvance, 0))
            .chain_from(h_new_texture_advance).fnptr!;
        if ((uint)slot >= TextureSlotCount) { original(slot, model, renderer); return; }
        System.Threading.Interlocked.Increment(ref _texture_step_calls);
        report_texture_animation_owner(slot, model, renderer);
        float scale = Math.Max(1f, Scale);
        float elapsed = RenderPassContext.IsPresentation ? 0 : 1f / scale;
        bool advance = !RenderPassContext.IsPresentation && (scale <= 1f || advance_this_frame());
        bool previousCapture = _textureUvCapturing;
        int previousSlot = _textureUvSlot;
        _textureUvCapturing = _texturePresentationBound && RenderPassContext.IsActive && !RenderPassContext.IsPresentation;
        _textureUvSlot = slot;
        try
        {
            with_texture_animation_clock(FhUtil.ptr_at<byte>(TextureAnimationSlots + slot * TextureSlotStride),
                _texture_step_shadow + slot * TextureSlotStride + 0xe, elapsed, advance, original, slot, model, renderer);
        }
        finally { _textureUvCapturing = previousCapture; _textureUvSlot = previousSlot; }
        if (_textureFramePhaseBound && !RenderPassContext.IsPresentation) capture_texture_frame_phase(slot, model, renderer, scale);
    }

    private static void with_texture_animation_clock(byte* slot, byte* shadow, float elapsed, bool advance,
        d_new_texture_advance original, int index, nint model, nint renderer)
    {
        // Only this callee reads the shadow; engine pause/resume writes always retain their own byte.
        *shadow = advance ? slot[0xe] : (byte)0;
        uint descriptor = *(uint*)slot;
        uint workAddress = *(uint*)(slot + 4);
        if (elapsed == 1f || descriptor == 0 || workAddress == 0 || slot[0xc] != 2 || slot[0xd] != 1)
        {
            original(index, model, renderer);
            return;
        }

        byte* work = (byte*)(nuint)workAddress;
        int count = *(int*)work;
        if ((uint)count > TextureRecordCapacity) { original(index, model, renderer); return; }
        Span<uint> saved = stackalloc uint[TextureRecordCapacity * 2];
        Span<uint> written = stackalloc uint[TextureRecordCapacity * 2];
        ulong changed = 0;
        for (int i = 0; i < count; i++)
        {
            byte* record = work + 8 + i * TextureRecordStride;
            if (*(short*)(record + 0x1c) != 3) continue;
            changed |= 1UL << i;
            for (int axis = 0; axis < 2; axis++)
            {
                float* rate = (float*)(record + 0x24 + axis * 4);
                int at = i * 2 + axis;
                saved[at] = *(uint*)rate;
                *rate *= elapsed;
                written[at] = *(uint*)rate;
            }
        }

        try { original(index, model, renderer); }
        finally
        {
            // These rates are private copies in a freshly allocated per-slot work buffer, never asset fields.
            bool owned = *(uint*)slot == descriptor && *(uint*)(slot + 4) == workAddress && slot[0xc] == 2 && slot[0xd] == 1;
            if (owned)
            {
                for (int i = 0; i < count; i++)
                {
                    if ((changed & (1UL << i)) == 0) continue;
                    byte* record = work + 8 + i * TextureRecordStride;
                    for (int axis = 0; axis < 2; axis++)
                    {
                        uint* rate = (uint*)(record + 0x24 + axis * 4);
                        int at = i * 2 + axis;
                        if (*rate == written[at]) *rate = saved[at];
                    }
                }
            }
        }
    }
}
