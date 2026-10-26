using TerraFX.Interop.DirectX;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint TextureDeviceCreate = 0x195800;
    private readonly object _textureDeviceLock = new();
    private bool _textureDeviceObserverInstalled;
    private bool _textureDeviceObserved;
    private bool _textureDeviceRefused;
    private TextureFrameBank? _textureDeviceRetainedBank;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_texture_device_create(nint owner, uint width, uint height, uint samples,
        uint windowed, uint mode, nint window);

    private bool init_texture_device_observer()
    {
        if (!_config.TextureBlendProbe && !_config.TextureFrameInterpolation) return true;
        if (_textureDeviceObserverInstalled) return true;
        if (!NativeBuild.TryValidate(out _) || !texture_binding_prefix(TextureDeviceCreate,
            [0x55, 0x8b, 0xec, 0x83, 0xec, 0x40])) return false;
        _textureDeviceObserverInstalled = hook_or_log(HookSiteId.TextureDeviceTextureEngineDeviceObserver, "texture engine device observer", TextureDeviceCreate,
            () => new FhCallHandle<d_texture_device_create>(new FhCallLocation(TextureDeviceCreate, 0))
                .hook(this, h_texture_device_create));
        if (_textureDeviceObserverInstalled)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                lock (_textureFrameLock)
                {
                    _textureDeviceRetainedBank?.Dispose();
                    _textureDeviceRetainedBank = null;
                }
            };
        return _textureDeviceObserverInstalled;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_texture_device_create(nint owner, uint width, uint height, uint samples,
        uint windowed, uint mode, nint window)
    {
        var original = new FhCallHandle<d_texture_device_create>(new FhCallLocation(TextureDeviceCreate, 0))
            .chain_from(h_texture_device_create).fnptr!;
        int result = original(owner, width, height, samples, windowed, mode, window);
        // This engine status is not an HRESULT; later initialization failures can leave populated handles.
        if (result != 0 || owner == 0) return result;
        Span<byte> handles = stackalloc byte[12];
        if (!MemoryProbe.TryCopy(owner + 0x6c, handles)) return result;
        nint device = (nint)(nuint)BitConverter.ToUInt32(handles);
        nint context = (nint)(nuint)BitConverter.ToUInt32(handles[4..]);
        nint swapchain = (nint)(nuint)BitConverter.ToUInt32(handles[8..]);
        if (device == 0 || context == 0 || swapchain == 0) return result;
        lock (_textureDeviceLock)
        {
            if (_textureDeviceRefused) return result;
            if (_textureDeviceObserved)
            {
                _textureDeviceRefused = true;
                lock (_textureFrameLock)
                {
                    _textureFrameBound = false;
                    _textureFrameContext = 0;
                    // No native teardown/quiescence contract is established for another creation.
                    _textureDeviceRetainedBank = _textureFrameBank;
                    _textureFrameBank = null;
                }
                lock (_textureBlendProbeLock)
                {
                    _textureProbeReady = false;
                    _textureProbeStopped = true;
                }
                _logger.Warning("[Vrr] Repeated engine graphics creation disabled texture interpolation and probing. Owned resources are retained until process exit; restart to enable them again.");
                diagnostic_event("texture_device_recreation_refused", new { owner = (long)owner });
                return result;
            }
            _textureDeviceObserved = true;
            texture_blend_probe_device((ID3D11DeviceContext*)context);
            texture_frame_interpolation_device((ID3D11DeviceContext*)context);
            diagnostic_event("texture_device_observed", new { owner = (long)owner, device = (long)device,
                context = (long)context, swapchain = (long)swapchain });
        }
        return result;
    }
}
