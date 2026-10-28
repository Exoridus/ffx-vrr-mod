using TerraFX.Interop.DirectX;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private readonly object _textureFrameLock = new();
    private TextureFrameBank? _textureFrameBank;
    private bool _textureFrameBound;
    private nint _textureFrameContext;
    private long _textureFrameBlendCount;
    private int _textureFrameEvidence;

    private sealed class TextureFrameBank(nint model, nint animation, nint[] resources) : IDisposable
    {
        internal readonly nint Model = model, Animation = animation;
        internal readonly nint[] Resources = resources;
        internal TextureFrameGpuBlender? Gpu;
        internal bool Failed;
        public void Dispose()
        {
            Gpu?.Dispose();
            Gpu = null;
            foreach (nint resource in Resources) if (resource != 0) ((ID3D11Resource*)resource)->Release();
            Array.Clear(Resources);
        }
    }

    private bool init_texture_frame_interpolation()
    {
        if (!_config.TextureFrameInterpolation) return true;
        if (!NativeBuild.TryValidate(out _) || !texture_binding_prefix(TextureProbeResolve,
            [0x55, 0x8b, 0xec, 0x8b, 0x55, 0x08, 0x53, 0x56, 0x57, 0x85, 0xd2])) return false;
        _textureFrameBound = hook_or_log(HookSiteId.TextureFrameInterpolationTextureFrameInterpolation, "texture frame interpolation", TextureProbeResolve,
            () => new FhCallHandle<d_texture_frame_resolve>(new FhCallLocation(TextureProbeResolve, 0)).hook(this, with_hook_continuation<d_texture_frame_resolve>(h_texture_frame_resolve)));
        if (_textureFrameBound) AppDomain.CurrentDomain.ProcessExit += (_, _) => invalidate_texture_frame_gpu();
        return _textureFrameBound;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_texture_frame_resolve(nint model, nint animation, uint frame, nint name);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_texture_frame_resolve(nint model, nint animation, uint frame, nint name)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_texture_frame_resolve>(new FhCallLocation(TextureProbeResolve, 0)).chain_from(h_texture_frame_resolve).fnptr)(model, animation, frame, name);

        var original = new FhCallHandle<d_texture_frame_resolve>(new FhCallLocation(TextureProbeResolve, 0))
            .chain_from(h_texture_frame_resolve).fnptr!;
        nint texture = original(model, animation, frame, name);
        if (!_textureFrameBound || frame >= 8) return texture;
        try
        {
            Span<byte> text = stackalloc byte[96];
            Span<byte> descriptor = stackalloc byte[16];
            Span<byte> header = stackalloc byte[0x2c];
            if (!MemoryProbe.TryCopy(name, text)) return texture;
            int end = text.IndexOf((byte)0);
            if (end < 0 || !TextureBlendProbeState.IsTarget(System.Text.Encoding.ASCII.GetString(text[..end]))
                || !MemoryProbe.TryCopy(animation, descriptor)
                || (BitConverter.ToUInt32(descriptor[8..]) & 0x7fffffff) != 8
                || !MemoryProbe.TryCopy(texture, header)) return texture;
            nint current = (nint)(nuint)BitConverter.ToUInt32(header[0x28..]);
            lock (_textureFrameLock)
                if (_textureFrameBank is { } bank && bank.Model == model && bank.Animation == animation
                    && bank.Resources[frame] == current) return texture;
            nint[] resources = new nint[8];
            try
            {
                for (uint index = 0; index < 8; index++)
                {
                    // This resolver only reads the current model's authored frame map and texture list.
                    nint source = index == frame ? texture : original(model, animation, index, name);
                    if (!MemoryProbe.TryCopy(source, header)) throw new InvalidDataException("Animation frame texture is unavailable.");
                    nint resource = (nint)(nuint)BitConverter.ToUInt32(header[0x28..]);
                    if (resource == 0 || resources.Contains(resource)) throw new InvalidDataException("Animation frame resources are missing or alias.");
                    ((ID3D11Resource*)resource)->AddRef();
                    resources[index] = resource;
                }
                lock (_textureFrameLock)
                {
                    if (!_textureFrameBound) return texture;
                    _textureFrameBank?.Dispose();
                    _textureFrameBank = new(model, animation, resources);
                }
                resources = [];
            }
            finally { foreach (nint resource in resources) if (resource != 0) ((ID3D11Resource*)resource)->Release(); }
        }
        catch (Exception error)
        {
            if (System.Threading.Interlocked.Increment(ref _textureFrameEvidence) <= 4)
                _logger.Warning($"[Vrr] Texture frame capture retained native output: {error.Message}");
        }
        return texture;
    }

    private void invalidate_texture_frame_gpu()
    {
        lock (_textureFrameLock)
        {
            _textureFrameBank?.Dispose();
            _textureFrameBank = null;
        }
    }

    private void texture_frame_interpolation_device(ID3D11DeviceContext* context)
    {
        if (!_textureFrameBound || context == null) return;
        try
        {
            lock (_textureFrameLock)
            {
                invalidate_texture_frame_gpu();
                _textureFrameContext = (nint)context;
                if (!texture_draw_device(context)) throw new InvalidOperationException("Texture draw hook refused.");
            }
        }
        catch (Exception error)
        {
            _textureFrameBound = false;
            _logger.Error($"[Vrr] Texture interpolation device binding refused: {error.Message}");
        }
    }

    private ID3D11ShaderResourceView* texture_frame_prepare_draw(ID3D11DeviceContext* context)
    {
        if (!_textureFrameBound || (nint)context != _textureFrameContext) return null;
        ID3D11ShaderResourceView* native = null;
        context->PSGetShaderResources(1, 1, &native);
        if (native == null) return null;
        bool replaced = false;
        try
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC view;
            native->GetDesc(&view);
            if (view.Format != DXGI_FORMAT.DXGI_FORMAT_BC3_UNORM
                || view.ViewDimension != D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D
                || view.Texture2D.MostDetailedMip != 0 || view.Texture2D.MipLevels != 10) return null;
            ID3D11Resource* resource = null;
            native->GetResource(&resource);
            TextureFrameBank? bank;
            int nativeFrame;
            try
            {
                lock (_textureFrameLock)
                {
                    bank = _textureFrameBank;
                    nativeFrame = bank is null ? -1 : Array.IndexOf(bank.Resources, (nint)resource);
                }
            }
            finally { if (resource != null) resource->Release(); }
            if (bank is null || nativeFrame < 0 || bank.Failed
                || !try_texture_frame_blend((uint)nativeFrame, out uint first, out uint next, out float alpha)) return null;
            lock (_textureFrameLock)
            {
                if (_textureFrameBank != bank || bank.Failed) return null;
                try
                {
                    if (bank.Gpu is null)
                    {
                        ID3D11Device* device = null;
                        context->GetDevice(&device);
                        try { bank.Gpu = new TextureFrameGpuBlender(device, context, bank.Resources); }
                        finally { if (device != null) device->Release(); }
                    }
                    ID3D11ShaderResourceView* blended = bank.Gpu.Blend(first, next, alpha);
                    long blends = System.Threading.Interlocked.Increment(ref _textureFrameBlendCount);
                    if (blends == 1) _logger.Info("[Vrr] w001 texture interpolation active: eight BC3_UNORM source frames, all ten mips, owned RGBA8_UNORM output at PS slot 1.");
                    if (blends <= 64) diagnostic_event("texture_frame_blend", new { native_frame = nativeFrame,
                        first_frame = first, next_frame = next, alpha, ps_slot = 1, mip_levels = 10, blends });
                    context->PSSetShaderResources(1, 1, &blended);
                    replaced = true;
                    return native;
                }
                catch (Exception error)
                {
                    bank.Failed = true;
                    bank.Gpu?.Dispose();
                    bank.Gpu = null;
                    _logger.Error($"[Vrr] Texture GPU interpolation stopped; native binding retained: {error.Message}");
                    return null;
                }
            }
        }
        finally { if (!replaced) native->Release(); }
    }

    private static void texture_frame_restore_draw(ID3D11DeviceContext* context, ID3D11ShaderResourceView* native)
    {
        if (native == null) return;
        context->PSSetShaderResources(1, 1, &native);
        native->Release();
    }

}
