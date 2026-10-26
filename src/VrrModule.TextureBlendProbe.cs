using TerraFX.Interop.DirectX;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint TextureProbeResolve = 0x2b1e40;
    private readonly TextureBlendProbeState _textureBlendProbe = new();
    private readonly object _textureBlendProbeLock = new();
    private readonly HashSet<nint> _textureProbeOwnedResources = [];
    private bool _textureProbeReady;
    private bool _textureProbeStopped;

    private void init_texture_blend_probe()
    {
        if (!_config.TextureBlendProbe || !NativeBuild.TryValidate(out _)) return;
        byte* entry = FhUtil.ptr_at<byte>(TextureProbeResolve);
        if (!MemoryProbe.IsReadable((nint)entry, 11)
            || !new ReadOnlySpan<byte>(entry, 11).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x8b, 0x55, 0x08, 0x53, 0x56, 0x57, 0x85, 0xd2]))
        {
            diagnostic_event("texture_blend_probe_refused", new { reason = "frame resolver entry mismatch" });
            return;
        }
        _textureProbeReady = HookSites.Install(HookSiteId.TextureBlendProbeResolve, _hookRegistration, TextureProbeResolve, _hookRegistrationGeneration, () => new FhCallHandle<d_texture_probe_resolve>(new FhCallLocation(TextureProbeResolve, 0))
            .hook(this, h_texture_probe_resolve));
        if (_textureProbeReady) AppDomain.CurrentDomain.ProcessExit += (_, _) => release_texture_blend_probe_resources();
        diagnostic_event("texture_blend_probe_ready", new { installed = _textureProbeReady,
            resource_limit = 32, binding_limit = 64, draw_limit = 4096,
            coverage = "w001-named eight-frame candidates; PS and VS slots 0..127; direct and indexed draws; names require owner correlation" });
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_texture_probe_resolve(nint model, nint animation, uint frame, nint name);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_texture_probe_resolve(nint model, nint animation, uint frame, nint name)
    {
        nint texture = new FhCallHandle<d_texture_probe_resolve>(new FhCallLocation(TextureProbeResolve, 0))
            .chain_from(h_texture_probe_resolve).fnptr!(model, animation, frame, name);
        if (!_textureProbeReady || (_textureBlendDiagnostics ?? _diagnostics)?.IsCapturing != true) return texture;
        try
        {
            Span<byte> descriptor = stackalloc byte[16];
            Span<byte> header = stackalloc byte[0x2c];
            Span<byte> text = stackalloc byte[96];
            if (!MemoryProbe.TryCopy(animation, descriptor)
                || (BitConverter.ToUInt32(descriptor[8..]) & 0x7fffffff) != 8
                || !MemoryProbe.TryCopy(texture, header)) return texture;
            nint resource = (nint)(nuint)BitConverter.ToUInt32(header[0x28..]);
            uint width = BitConverter.ToUInt32(header[0x1c..]);
            uint height = BitConverter.ToUInt32(header[0x20..]);
            string nativeName = "";
            if (MemoryProbe.TryCopy(name, text))
            {
                int end = text.IndexOf((byte)0);
                if (end >= 0) nativeName = System.Text.Encoding.ASCII.GetString(text[..end]);
            }
            if (!TextureBlendProbeState.IsTarget(nativeName)) return texture;
            var source = new TextureBlendProbeSource(model, animation, frame, nativeName, texture, width, height);
            lock (_textureBlendProbeLock)
                if (_textureProbeReady && _textureBlendProbe.Track(resource, source))
                {
                    ((ID3D11Resource*)resource)->AddRef();
                    _textureProbeOwnedResources.Add(resource);
                    diagnostic_event("texture_blend_source", new { model = (long)model, animation = (long)animation,
                        frame, frame_count = 8, native_name = nativeName, texture = (long)texture,
                        resource = (long)resource, width, height, identity = "w001 candidate; correlate texture_animation_owner" });
                }
        }
        catch (Exception) { /* Observation must preserve the native call. */ }
        return texture;
    }

    private void release_texture_blend_probe_resources()
    {
        lock (_textureBlendProbeLock)
        {
            foreach (nint resource in _textureProbeOwnedResources) ((ID3D11Resource*)resource)->Release();
            _textureProbeOwnedResources.Clear();
        }
    }

    private void texture_blend_probe_device(ID3D11DeviceContext* context)
    {
        if (!_textureProbeReady || context == null) return;
        try
        {
            lock (_textureBlendProbeLock)
            {
                release_texture_blend_probe_resources();
                _textureBlendProbe.Reset();
                _textureProbeStopped = false;
                diagnostic_event("texture_blend_draw_hook", new { shared = true, installed = texture_draw_device(context) });
            }
        }
        catch (Exception error) { diagnostic_event("texture_blend_device_probe_failed", new { error = error.Message }); }
    }

    private void texture_blend_probe_draw(ID3D11DeviceContext* context, bool indexed, uint count)
    {
        if (!_textureProbeReady || System.Threading.Volatile.Read(ref _textureProbeStopped)
            || (_textureBlendDiagnostics ?? _diagnostics)?.IsCapturing != true) return;
        try
        {
            lock (_textureBlendProbeLock)
            {
                if (!_textureBlendProbe.BeginDraw())
                {
                    if (!_textureBlendProbe.Active && !_textureProbeStopped)
                    {
                        _textureProbeStopped = true;
                        release_texture_blend_probe_resources();
                        diagnostic_event("texture_blend_probe_end", new { reason = "bounded capture complete" });
                    }
                    return;
                }
                ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[128];
                for (int stage = 0; stage < 2; stage++)
                {
                    new Span<nint>(views, 128).Clear();
                    try
                    {
                        if (stage == 0) context->PSGetShaderResources(0, 128, views);
                        else context->VSGetShaderResources(0, 128, views);
                        for (uint slot = 0; slot < 128; slot++)
                        {
                            if (views[slot] == null) continue;
                            ID3D11Resource* resource = null;
                            views[slot]->GetResource(&resource);
                            if (resource == null) continue;
                            try
                            {
                                if (!_textureBlendProbe.Record((nint)resource, stage, slot, out var source)) continue;
                                D3D11_SHADER_RESOURCE_VIEW_DESC view;
                                views[slot]->GetDesc(&view);
                                diagnostic_event("texture_blend_gpu_binding", new { stage = stage == 0 ? "PS" : "VS", slot,
                                    indexed, count, model = (long)source.Model, animation = (long)source.Animation,
                                    frame = source.Frame, native_name = source.Name, texture = (long)source.Texture,
                                    resource = (long)(nint)resource, srv = (long)(nint)views[slot],
                                    format = (uint)view.Format, dimension = (uint)view.ViewDimension,
                                    most_detailed_mip = view.ViewDimension == D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D ? (uint?)view.Texture2D.MostDetailedMip : null,
                                    mip_levels = view.ViewDimension == D3D_SRV_DIMENSION.D3D_SRV_DIMENSION_TEXTURE2D ? (uint?)view.Texture2D.MipLevels : null,
                                    width = source.Width, height = source.Height });
                            }
                            finally { resource->Release(); }
                        }
                    }
                    finally { for (int slot = 0; slot < 128; slot++) if (views[slot] != null) views[slot]->Release(); }
                }
            }
        }
        catch (Exception) { /* Observation must preserve the native call. */ }
    }

}
