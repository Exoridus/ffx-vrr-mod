using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint RenderImageOrdinary = 0x242bb0;
    private const nint GraphicsInitialize = 0x2419d0, GraphicsFinalize = 0x2403e0, GraphicsReset = 0x2fa70;
    private readonly object _graphicsLifetimeGate = new();
    private long _graphicsEpoch;
    private int _graphicsActive, _graphicsReadyThread;
    private bool _graphicsReady, _graphicsClosed;
    private readonly ThreadLocal<int> _renderImageOrdinaryDepth = new();
    private readonly record struct GraphicsObservation(long Epoch, bool Initialize, bool Armed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint d_graphics_result();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_graphics_reset();

    private static bool render_image_relocated(nint rva, ReadOnlySpan<byte> expected, int operandOffset, nint target)
    {
        Span<byte> relocated = stackalloc byte[expected.Length];
        expected.CopyTo(relocated);
        nint address = (nint)FhUtil.ptr_at<byte>(target);
        MemoryMarshal.Write(relocated[operandOffset..], in address);
        return render_image_prefix(rva, relocated);
    }

    private static bool graphics_lifetime_bindings_valid()
        => render_image_relocated(RenderImageOrdinary, [0x8b,0x0d,0,0,0,0], 2, 0x8cb9d8)
        && render_image_prefix(0x242bb6, [0x56,0xe8,0x74,0x5d,2,0,0x8b,0xf0,0xe8,0x4d,0xbc,0x0d,0])
        && render_image_relocated(0x242bc3, [0x8b,0x0d,0,0,0,0,0xe8,0x52,0x7a,1,0,0x8b,0xc6,0x5e,0xc3], 2, 0x8cb9d8)
        && render_image_prefix(0x2f963, [0xe8,0x48,0x32,0x21,0])
        && render_image_relocated(GraphicsInitialize, [0x68,0,0,0,0,0x6a,0x0a,0xe8,0x74,0x6e,0,0,0x83,0xc4,8], 1, 0x7415c4)
        && render_image_relocated(GraphicsFinalize, [0x68,0,0,0,0,0x6a,0x0a,0xe8,0x64,0x84,0,0,0x83,0xc4,8], 1, 0x741604)
        && render_image_relocated(GraphicsReset, [0x6a,0,0x6a,0,0x6a,0x10,0xff,0x35,0,0,0,0], 8, 0x8c9ce8)
        && render_image_relocated(0x2419e4, [0xa1,0,0,0,0], 1, 0x8dede4)
        && render_image_relocated(0x241a09, [0xa3,0,0,0,0], 1, 0x8dede4)
        && render_image_relocated(0x241b79, [0xc7,5,0,0,0,0,1,0,0,0], 2, 0x8cb45c)
        && render_image_prefix(0x241b85, [0xc3])
        && render_image_relocated(0x2403ef, [0x83,0x3d,0,0,0,0,0], 2, 0x8cb45c)
        && render_image_relocated(0x2403fc, [0x8b,0x0d,0,0,0,0], 2, 0x8dede4)
        && render_image_relocated(0x240403, [0xc7,5,0,0,0,0,0,0,0,0], 2, 0x8cb45c)
        && render_image_relocated(0x240412, [0x8b,0x35,0,0,0,0], 2, 0x8dede4)
        && render_image_relocated(0x24042c, [0xc7,5,0,0,0,0,0,0,0,0], 2, 0x8dede4)
        && render_image_prefix(0x240634, [0xc3]) && render_image_prefix(0x240646, [0xc3])
        && render_image_relocated(0x2fa7c, [0xc7,5,0,0,0,0,0,0,0,0], 2, 0x8cb45c)
        && render_image_prefix(0x2fa8c, [0xc3]);

    private bool init_graphics_lifetime_hooks()
    {
        bool ok = hook_or_log(HookSiteId.GraphicsLifetimeOrdinaryRenderCompletion, "ordinary render completion", RenderImageOrdinary,
            () => new FhCallHandle<d_graphics_result>(new FhCallLocation(RenderImageOrdinary, 0)).hook(this, with_hook_continuation<d_graphics_result>(h_render_image_ordinary)));
        ok &= hook_or_log(HookSiteId.GraphicsLifetimeGraphicsInitializeObservation, "graphics initialize observation", GraphicsInitialize,
            () => new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsInitialize, 0)).hook(this, with_hook_continuation<d_graphics_result>(h_graphics_initialize)));
        ok &= hook_or_log(HookSiteId.GraphicsLifetimeGraphicsFinalizeObservation, "graphics finalize observation", GraphicsFinalize,
            () => new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsFinalize, 0)).hook(this, with_hook_continuation<d_graphics_result>(h_graphics_finalize)));
        ok &= hook_or_log(HookSiteId.GraphicsLifetimeGraphicsResetObservation, "graphics reset observation", GraphicsReset,
            () => new FhCallHandle<d_graphics_reset>(new FhCallLocation(GraphicsReset, 0)).hook(this, with_hook_continuation<d_graphics_reset>(h_graphics_reset)));
        return ok;
    }

    private static bool graphics_initialized()
    {
        Span<byte> initialized = stackalloc byte[sizeof(uint)];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(0x8cb45c), initialized)
            && MemoryMarshal.Read<uint>(initialized) == 1;
    }

    private bool graphics_epoch_ready(long epoch)
        => _graphicsReady && !_graphicsClosed && _graphicsActive == 0 && _graphicsEpoch == epoch;

    private GraphicsObservation graphics_lifecycle_enter(bool initialize)
    {
        lock (_graphicsLifetimeGate)
        {
            _graphicsEpoch++;
            _graphicsReady = false;
            if (!initialize || _graphicsActive != 0) _graphicsClosed = true;
            _graphicsActive++;
            if (Volatile.Read(ref _renderImageEverAdmitted) != 0)
            {
                _graphicsClosed = true;
                refuse_independent_presentation("render_image_graphics_lifecycle");
            }
            return new(_graphicsEpoch, initialize, _renderImagesInstalled && RenderSchedulerReady);
        }
    }

    private void graphics_lifecycle_complete(GraphicsObservation observation, bool initialized)
    {
        lock (_graphicsLifetimeGate)
        {
            _graphicsActive--;
            if (observation.Initialize && !initialized) _graphicsClosed = true;
            if (observation.Initialize && observation.Armed && initialized && !_graphicsClosed
                && _graphicsActive == 0 && observation.Epoch == _graphicsEpoch)
            {
                _graphicsReadyThread = Environment.CurrentManagedThreadId;
                _graphicsReady = true;
            }
        }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_graphics_initialize()
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsInitialize, 0)).chain_from(h_graphics_initialize).fnptr)();

        GraphicsObservation observation = graphics_lifecycle_enter(true);
        bool normal = false;
        uint result = 0;
        try
        {
            result = new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsInitialize, 0))
                .chain_from(h_graphics_initialize).fnptr!();
            normal = true;
            return result;
        }
        finally { graphics_lifecycle_complete(observation, normal && result == 0 && graphics_initialized()); }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_graphics_finalize()
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsFinalize, 0)).chain_from(h_graphics_finalize).fnptr)();

        GraphicsObservation observation = graphics_lifecycle_enter(false);
        try
        {
            return new FhCallHandle<d_graphics_result>(new FhCallLocation(GraphicsFinalize, 0))
                .chain_from(h_graphics_finalize).fnptr!();
        }
        finally { graphics_lifecycle_complete(observation, false); }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_graphics_reset()
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_graphics_reset>(new FhCallLocation(GraphicsReset, 0)).chain_from(h_graphics_reset).fnptr)();
            return;
        }

        GraphicsObservation observation = graphics_lifecycle_enter(false);
        try
        {
            new FhCallHandle<d_graphics_reset>(new FhCallLocation(GraphicsReset, 0))
                .chain_from(h_graphics_reset).fnptr!();
        }
        finally { graphics_lifecycle_complete(observation, false); }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_render_image_ordinary()
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_graphics_result>(new FhCallLocation(RenderImageOrdinary, 0)).chain_from(h_render_image_ordinary).fnptr)();

        RenderImageFrameScope? frame = _renderImageFrame.Value;
        int version = 0;
        if (frame is not null)
        {
            frame.OrdinaryComplete = false;
            version = ++frame.OrdinaryVersion;
        }
        long epoch = frame?.AdmissionEpoch ?? 0;
        bool qualifiedAtEntry;
        lock (_graphicsLifetimeGate)
            qualifiedAtEntry = frame is { Valid: true, Admitted: true, AnimationComplete: true }
                && graphics_epoch_ready(epoch);
        bool nested = _renderImageOrdinaryDepth.Value++ != 0;
        if (nested && _presenter_active && RenderSchedulerReady)
            refuse_independent_presentation("render_image_nested_ordinary");
        try
        {
            uint result = new FhCallHandle<d_graphics_result>(new FhCallLocation(RenderImageOrdinary, 0))
                .chain_from(h_render_image_ordinary).fnptr!();
            lock (_graphicsLifetimeGate)
                if (qualifiedAtEntry && !nested && frame is { Valid: true, Admitted: true, AnimationComplete: true }
                    && ReferenceEquals(frame, _renderImageFrame.Value) && frame.OrdinaryVersion == version
                    && frame.AdmissionEpoch == epoch && graphics_epoch_ready(epoch)) frame.OrdinaryComplete = true;
            return result;
        }
        finally { _renderImageOrdinaryDepth.Value--; }
    }
}