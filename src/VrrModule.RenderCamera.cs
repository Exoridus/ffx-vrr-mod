using System.Numerics;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint RenderCameraFinalize = 0x3bc110;
    private const nint RenderCameraViewCommit = 0x239860;
    private const nint RenderCameraFovCommit = 0x3bc980;
    private readonly RenderCameraHistory _renderCameraHistory = new();
    private readonly RenderCameraWrite _renderCameraWrite = new();
    private nint _renderCameraView, _renderCameraSource2, _renderCameraSource3, _renderCameraBlocks, _renderCameraCurrent;
    private bool _renderCameraBound, _renderCameraSampling, _renderCameraCommitted, _renderCameraActive;
    private int _renderCameraProjectionViewport = -1;
    private float _renderCameraAlpha = 1;

    private bool init_render_camera()
    {
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Camera binding refused: {error}");
            return false;
        }
        byte* finalize = FhUtil.ptr_at<byte>(RenderCameraFinalize);
        byte* commit = FhUtil.ptr_at<byte>(RenderCameraViewCommit);
        byte* fov = FhUtil.ptr_at<byte>(RenderCameraFovCommit);
        if (!MemoryProbe.IsReadable((nint)finalize, 0x600)
            || !MemoryProbe.IsReadable((nint)commit, 0x10)
            || !MemoryProbe.IsReadable((nint)fov, 0x47)
            || !new ReadOnlySpan<byte>(finalize, 8).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x83, 0xec, 0x20, 0xc7, 0x05])
            || !new ReadOnlySpan<byte>(commit, 9).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x81, 0xec, 0xc0, 0, 0, 0])
            || !new ReadOnlySpan<byte>(fov, 21).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x83, 0xec, 0x10, 0x56,
                0x8b, 0x75, 0x08, 0x57, 0x8b, 0xfe, 0x69, 0xff, 0xc0, 0x01, 0, 0, 0xd9, 0x87]))
            return render_camera_binding_failed();

        int matches = 0;
        for (int i = 0; i <= 0x600 - 0x4b; i++)
        {
            byte* p = finalize + i;
            if (p[0] != 0x39 || p[1] != 0x1d || p[6] != 0x74 || p[7] != 0x3a
                || p[8] != 0xff || p[9] != 0x75 || p[10] != 0xfc || p[11] != 0x68
                || p[16] != 0xe8 || p[0x30] != 0x68 || p[0x35] != 0xe8
                || p[0x42] != 0x83 || p[0x43] != 0x3d || p[0x48] != 0) continue;
            matches++;
            _renderCameraSource2 = (nint)(nuint)(*(uint*)(p + 2));
            _renderCameraView = (nint)(nuint)(*(uint*)(p + 0x31));
            _renderCameraSource3 = (nint)(nuint)(*(uint*)(p + 0x44));
        }
        // These are relocated operands in the validated current-build bodies.
        _renderCameraBlocks = (nint)(nuint)(*(uint*)(fov + 0x15)) - 0x10;
        if (finalize[0x39c] != 0x8b || finalize[0x39d] != 0x0d
            || fov[0x2e] != 0xd8 || fov[0x2f] != 0x8f || fov[0x3d] != 0xd9 || fov[0x3e] != 0x87
            || *(uint*)(fov + 0x30) != (uint)(nuint)(_renderCameraBlocks + 0x14)
            || *(uint*)(fov + 0x3f) != (uint)(nuint)(_renderCameraBlocks + 0x1c))
            return render_camera_binding_failed();
        _renderCameraCurrent = (nint)(nuint)(*(uint*)(finalize + 0x39e));
        if (matches != 1 || !MemoryProbe.IsReadable(_renderCameraSource2, 4)
            || !MemoryProbe.IsReadable(_renderCameraSource3, 4) || !MemoryProbe.IsReadable(_renderCameraView, 64)
            || !MemoryProbe.IsReadable(_renderCameraCurrent, 4) || !MemoryProbe.IsReadable(_renderCameraBlocks, 3 * 0x1c0))
            return render_camera_binding_failed();

        _renderCameraBound = hook_or_log(HookSiteId.RenderCameraRenderCameraFinalize, "render camera finalize", RenderCameraFinalize,
            () => new FhCallHandle<d_render_camera_finalize>(new FhCallLocation(RenderCameraFinalize, 0)).hook(this, h_render_camera_finalize));
        return _renderCameraBound;
    }

    private bool render_camera_binding_failed()
    {
        _logger.Error("[Vrr] Camera binding refused: finalized view/source/FOV instruction contract does not match.");
        return false;
    }

    private void render_camera_set_presentation_alpha(float alpha)
        => _renderCameraAlpha = float.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1;

    private void render_camera_begin_simulation()
    {
        restore_render_camera();
        _renderCameraActive = true;
        _renderCameraHistory.BeginSimulation();
        _renderCameraSampling = true;
    }

    private void render_camera_complete_simulation()
    {
        _renderCameraHistory.CompleteSimulation();
        _renderCameraSampling = false;
    }

    private void render_camera_begin_presentation()
    {
        restore_render_camera();
        _renderCameraActive = true;
        _renderCameraSampling = false;
    }

    private void reset_render_camera()
    {
        restore_render_camera();
        _renderCameraHistory.Reset();
        _renderCameraActive = false;
        _renderCameraSampling = false;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_render_camera_finalize(int viewport);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_render_camera_commit(float* view);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_render_camera_fov(int viewport);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_render_camera_finalize(int viewport)
    {
        // A later viewport can reuse the global view instead of rebuilding it.
        restore_render_camera();
        int result = new FhCallHandle<d_render_camera_finalize>(new FhCallLocation(RenderCameraFinalize, 0))
            .chain_from(h_render_camera_finalize).fnptr!(viewport);
        if (result != 0 || (uint)viewport > 2 || !_renderCameraActive || !_renderCameraBound) return result;

        uint source2 = *(uint*)_renderCameraSource2;
        uint source3 = *(uint*)_renderCameraSource3;
        int source = source2 != 0 ? 2 : source3 != 0 ? 3 : 1;
        float* block = (float*)(_renderCameraBlocks + viewport * 0x1c0);
        var sample = new RenderCameraSample(*(Matrix4x4*)_renderCameraView,
            new Vector3(block[4], block[5], block[7]),
            new RenderCameraIdentity(source, *(uint*)_renderCameraCurrent, source2, source3));
        if (_renderCameraSampling) _renderCameraHistory.Capture(viewport, sample);
        // Viewport 1 has special projection/view commit paths that must retain native ownership.
        if ((source == 1 && viewport == 1)
            || !_renderCameraHistory.TryInterpolate(viewport, sample, _renderCameraAlpha, out var rendered)) return result;

        *(Matrix4x4*)_renderCameraView = rendered.View;
        _renderCameraWrite.Record(sample.View, rendered.View);
        _renderCameraCommitted = source is 1 or 3;
        if (source == 1 && sample.Projection != rendered.Projection)
        {
            block[4] = rendered.Projection.X;
            block[5] = rendered.Projection.Y;
            block[7] = rendered.Projection.Z;
            try { commit_render_camera_projection(viewport); }
            finally
            {
                block[4] = sample.Projection.X;
                block[5] = sample.Projection.Y;
                block[7] = sample.Projection.Z;
            }
            _renderCameraProjectionViewport = viewport;
        }
        else if (_renderCameraCommitted) commit_render_camera_view();
        return result;
    }

    private void restore_render_camera()
    {
        if (!_renderCameraBound) return;
        if (_renderCameraWrite.TryRestore(*(Matrix4x4*)_renderCameraView, out var canonical))
        {
            *(Matrix4x4*)_renderCameraView = canonical;
            if (_renderCameraProjectionViewport >= 0) commit_render_camera_projection(_renderCameraProjectionViewport);
            else if (_renderCameraCommitted) commit_render_camera_view();
        }
        _renderCameraProjectionViewport = -1;
        _renderCameraCommitted = false;
    }

    private void commit_render_camera_view()
        => new FhCallHandle<d_render_camera_commit>(new FhCallLocation(RenderCameraViewCommit, 0)).fnptr!((float*)_renderCameraView);

    private void commit_render_camera_projection(int viewport)
        => new FhCallHandle<d_render_camera_fov>(new FhCallLocation(RenderCameraFovCommit, 0)).fnptr!(viewport);
}
