namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint TextureUvOutput = 0x244150;
    private const nint TextureFinalDraw = 0x42ed50;
    private const nint TextureWorkCreate = 0x37f9c0;
    private const nint TextureSlotDelete = 0x37c770;
    private readonly TextureUvHistory _textureUvHistory = new();
    private readonly object _textureUvLock = new();
    private bool _texturePresentationBound;
    private long _textureUvLastImage = -1, _textureUvHistoryEpoch = -1;
    private readonly int[] _textureBlendOwnerSeen = new int[TextureSlotCount];
    [ThreadStatic] private static bool _textureUvCapturing;
    [ThreadStatic] private static int _textureUvSlot;

    private bool init_texture_presentation()
    {
        if (!IndependentPresentationEnabled && !_config.TextureFrameInterpolation) return true;
        if (!texture_binding_prefix(TextureUvOutput, [0x55, 0x8b, 0xec, 0x81, 0xec, 0x08, 0x03, 0, 0])
            || !texture_binding_prefix(TextureFinalDraw, [0x55, 0x8b, 0xec, 0x81, 0xec, 0x20, 0x04, 0, 0])
            || !texture_binding_prefix(TextureWorkCreate, [0x55, 0x8b, 0xec, 0x83, 0xec, 0x10, 0x8b, 0x45, 0x08])
            || !texture_binding_prefix(TextureSlotDelete, [0x55, 0x8b, 0xec, 0x56, 0x8b, 0x75, 0x08, 0x85, 0xf6]))
        {
            _logger.Error("[Vrr] Texture presentation refused: output, draw or slot lifecycle contract changed.");
            return false;
        }
        bool ok = true;
        if (IndependentPresentationEnabled) ok = hook_or_log(HookSiteId.TexturePresentationTextureUVOutput, "texture UV output", TextureUvOutput,
            () => new FhCallHandle<d_texture_uv_output>(new FhCallLocation(TextureUvOutput, 0)).hook(this, with_hook_continuation<d_texture_uv_output>(h_texture_uv_output)));
        ok &= hook_or_log(HookSiteId.TexturePresentationTextureFinalImage, "texture final image", TextureFinalDraw,
            () => new FhCallHandle<d_texture_final_draw>(new FhCallLocation(TextureFinalDraw, 0)).hook(this, with_hook_continuation<d_texture_final_draw>(h_texture_final_draw)));
        ok &= hook_or_log(HookSiteId.TexturePresentationTextureWorkLifetime, "texture work lifetime", TextureWorkCreate,
            () => new FhCallHandle<d_new_texture_advance>(new FhCallLocation(TextureWorkCreate, 0)).hook(this, with_hook_continuation<d_new_texture_advance>(h_texture_work_create)));
        ok &= hook_or_log(HookSiteId.TexturePresentationTextureSlotLifetime, "texture slot lifetime", TextureSlotDelete,
            () => new FhCallHandle<d_texture_delete>(new FhCallLocation(TextureSlotDelete, 0)).hook(this, with_hook_continuation<d_texture_delete>(h_texture_slot_delete)));
        _texturePresentationBound = ok && IndependentPresentationEnabled;
        _textureFramePhaseBound = ok && _config.TextureFrameInterpolation;
        return ok;
    }

    private static bool texture_binding_prefix(nint address, ReadOnlySpan<byte> expected)
        => MemoryProbe.IsReadable((nint)FhUtil.ptr_at<byte>(address), expected.Length)
            && new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(address), expected.Length).SequenceEqual(expected);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texture_uv_output(int record, int set, float u, float v, nint renderer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texture_final_draw();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texture_delete(int slot);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texture_work_create(int slot, nint model, nint descriptor)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_new_texture_advance>(new FhCallLocation(TextureWorkCreate, 0)).chain_from(h_texture_work_create).fnptr)(slot, model, descriptor);
            return;
        }

        invalidate_texture_uv(slot);
        new FhCallHandle<d_new_texture_advance>(new FhCallLocation(TextureWorkCreate, 0))
            .chain_from(h_texture_work_create).fnptr!(slot, model, descriptor);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texture_slot_delete(int slot)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_texture_delete>(new FhCallLocation(TextureSlotDelete, 0)).chain_from(h_texture_slot_delete).fnptr)(slot);
            return;
        }

        invalidate_texture_uv(slot);
        new FhCallHandle<d_texture_delete>(new FhCallLocation(TextureSlotDelete, 0))
            .chain_from(h_texture_slot_delete).fnptr!(slot);
    }

    private void invalidate_texture_uv(int slot)
    {
        lock (_textureUvLock) _textureUvHistory.Invalidate(slot);
        invalidate_texture_frame(slot);
        invalidate_texture_frame_gpu();
        if ((uint)slot < TextureSlotCount) System.Threading.Volatile.Write(ref _textureBlendOwnerSeen[slot], 0);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texture_uv_output(int record, int set, float u, float v, nint renderer)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_texture_uv_output>(new FhCallLocation(TextureUvOutput, 0)).chain_from(h_texture_uv_output).fnptr)(record, set, u, v, renderer);
            return;
        }

        if (_textureUvCapturing)
            capture_texture_uv(FhUtil.ptr_at<byte>(TextureAnimationSlots + _textureUvSlot * TextureSlotStride),
                _textureUvSlot, record, set, u, v, (uint)(nuint)renderer);
        new FhCallHandle<d_texture_uv_output>(new FhCallLocation(TextureUvOutput, 0))
            .chain_from(h_texture_uv_output).fnptr!(record, set, u, v, renderer);
    }

    private void capture_texture_uv(byte* slot, int index, int record, int set, float u, float v, uint renderer)
    {
        if (!RenderPassContext.IsActive || RenderPassContext.IsPresentation || (uint)index >= TextureSlotCount) return;
        uint descriptor = *(uint*)slot, workAddress = *(uint*)(slot + 4);
        if (descriptor == 0 || workAddress == 0 || renderer == 0 || slot[0xc] != 2 || slot[0xd] != 1) return;
        byte* work = (byte*)(nuint)workAddress;
        if (!MemoryProbe.IsReadable((nint)work, 8)) return;
        int count = *(int*)work;
        if ((uint)count > TextureRecordCapacity || (uint)record >= (uint)count) return;
        byte* entry = work + 8 + record * TextureRecordStride;
        if (!MemoryProbe.IsReadable((nint)entry, TextureRecordStride) || *(short*)(entry + 0x1c) != 3) return;
        short height = *(short*)(entry + 0xe);
        if (height <= 0) return;
        // The native output fixes U at zero; V is the only scroll accumulator published to this material path.
        // Each authored-set iteration integrates before uploading, including intermediate catch-up passes.
        float delta = -*(float*)(entry + 0x28) / height;
        var target = new TextureUvTarget(index, record, set, descriptor, workAddress, renderer, height);
        lock (_textureUvLock)
        {
            observe_texture_uv_image();
            _textureUvHistory.Capture(target, RenderPassContext.SimulationSerial, u, v, delta);
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texture_final_draw()
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_texture_final_draw>(new FhCallLocation(TextureFinalDraw, 0)).chain_from(h_texture_final_draw).fnptr)();
            return;
        }

        bool drawn = FhUtil.get_at<int>(0xefb7d8) == 0;
        if (_texturePresentationBound)
        {
            var output = new FhCallHandle<d_texture_uv_output>(new FhCallLocation(TextureUvOutput, 0))
                .chain_from(h_texture_uv_output).fnptr!;
            present_texture_uv(RenderPassContext.IsActive, RenderPassContext.IsFinalImage && drawn, texture_uv_target_live,
                (target, u, v) => output(target.Record, target.Set, u, v, (nint)target.Renderer));
        }
        if (_textureFramePhaseBound && drawn && (!RenderPassContext.IsActive || RenderPassContext.IsFinalImage))
            publish_texture_frame_phase(texture_frame_owner_live);
        new FhCallHandle<d_texture_final_draw>(new FhCallLocation(TextureFinalDraw, 0))
            .chain_from(h_texture_final_draw).fnptr!();
    }

    private void present_texture_uv(bool active, bool finalImage, Func<TextureUvTarget, bool> valid,
        Action<TextureUvTarget, float, float> output)
    {
        lock (_textureUvLock)
        {
            if (!active) { _textureUvHistory.Reset(); _textureUvLastImage = -1; return; }
            observe_texture_uv_image();
            if (!finalImage) return;
            // This boundary follows the joined character jobs; material output never writes canonical UV work.
            _textureUvHistory.Present(RenderPassContext.ImageSerial, RenderPassContext.SimulationSerial,
                RenderPassContext.PresentationAlpha, valid, output);
        }
    }

    private void observe_texture_uv_image()
    {
        long image = RenderPassContext.ImageSerial;
        long epoch = RenderPassContext.HistoryEpoch;
        // A clock reset can occur between adjacent images; image gaps alone cannot identify that discontinuity.
        if (epoch != _textureUvHistoryEpoch || (_textureUvLastImage >= 0 && image > _textureUvLastImage + 1))
            _textureUvHistory.Reset();
        _textureUvHistoryEpoch = epoch;
        _textureUvLastImage = image;
    }

    private void report_texture_animation_owner(int slot, nint model, nint renderer)
    {
        if (!_config.TextureBlendProbe || System.Threading.Interlocked.Exchange(ref _textureBlendOwnerSeen[slot], 1) != 0) return;
        byte* name = (byte*)model;
        int length = 0;
        while (length < 64 && MemoryProbe.IsReadable(model + length, 1) && name[length] != 0) length++;
        bool printable = true;
        for (int i = 0; i < length; i++) printable &= name[i] >= 0x20 && name[i] <= 0x7e;
        string modelName = length == 0 || !printable ? "" : System.Text.Encoding.ASCII.GetString(new ReadOnlySpan<byte>(name, length));
        diagnostic_event("texture_animation_owner", new { slot, model_name = modelName, model = (long)model, renderer = (long)renderer });
    }

    private static bool texture_uv_slot_matches(byte* slot, TextureUvTarget target)
    {
        if (*(uint*)slot != target.Descriptor || *(uint*)(slot + 4) != target.Work || slot[0xc] != 2 || slot[0xd] != 1) return false;
        if ((uint)target.Set >= 32 || (target.Set != 1 && target.Set != 2 && target.Set != 0x15 && *(int*)(slot + 8) != target.Set)
            || (slot[0xf + target.Set] & 2) != 0) return false;
        byte* work = (byte*)(nuint)target.Work;
        if (!MemoryProbe.IsReadable((nint)work, 8)) return false;
        int count = *(int*)work;
        if ((uint)count > TextureRecordCapacity || (uint)target.Record >= (uint)count) return false;
        byte* record = work + 8 + target.Record * TextureRecordStride;
        return MemoryProbe.IsReadable((nint)record, TextureRecordStride)
            && MemoryProbe.IsReadable((nint)(work + 0xd08 + target.Record), 1) && work[0xd08 + target.Record] != 0xff && *(short*)(record + 0x1c) == 3
            && *(short*)(record + 0xe) == target.Height;
    }

    private static bool texture_uv_target_live(TextureUvTarget target)
    {
        if (!texture_uv_slot_matches(FhUtil.ptr_at<byte>(TextureAnimationSlots + target.Slot * TextureSlotStride), target)) return false;
        int count = FhUtil.get_at<int>(0x1fc44e0);
        byte* actors = (byte*)(nuint)FhUtil.get_at<uint>(0x1fc44e4);
        return texture_uv_actor_matches(actors, count, target);
    }

    private static bool texture_uv_actor_matches(byte* actors, int count, TextureUvTarget target)
    {
        if ((uint)count > 4096 || !MemoryProbe.IsReadable((nint)actors, count * 0x880)) return false;
        for (int i = 0; i < count; i++)
        {
            byte* actor = actors + i * 0x880;
            if (actor[2] != 0 && *(uint*)(actor + 0x6fc) != 0 && *(int*)(actor + 0x700) == target.Slot
                && *(uint*)(actor + 0x830) == target.Renderer) return true;
        }
        return false;
    }
}
