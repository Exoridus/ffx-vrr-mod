namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Upstream FFX-60fps rows LOOP-004 and LOOP-005 for presentation passes.
///
///     LOOP-004: the camera finalize steps the viewport tweens and shakes of the three ms_screen blocks
///     (3 x 0x1C0, cam_tween_slots = block + 0xDC) and draws battle RNG stream 9 on every pass, so both
///     ran 2.4 times per simulation tick. The blocks and the stream 9 seed are snapshot when the main
///     loop is entered and put back after a presentation pass.
///
///     LOOP-005: Sg_MainLoop's exec == 0 fallback eases the field and battle camera again behind the gate
///     byte it tests; a presentation pass clears that byte and gets it back afterwards.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint ViewportBlocks = 0xD37000;
    private const int ViewportBlockBytes = 3 * 0x1C0;
    private const nint BtlRand = 0x3988F0, BtlRandStream9Seed = 0xD35F04;
    private const nint CameraTweenSlotsSite = 0x3BBFC6, CameraFallbackSite = 0x4210A2, CameraFallbackGate = 0xEFBBB0;

    private readonly byte[] _viewportSnapshot = new byte[ViewportBlockBytes];
    private uint _stream9Snapshot;
    private bool _viewportRollbackInstalled, _viewportSnapshotTaken, _cameraGateCleared;
    private byte _cameraGateSaved;
    private long _viewportRollbacks, _viewportChanged, _cameraGateSkips;

    private string viewport_rollback_counts()
        => $"vprb={_viewportRollbacks}/{_viewportChanged} camgate={_cameraGateSkips}";

    private bool init_viewport_rollback()
    {
        if (!IndependentPresentationEnabled) return true;
        _viewportRollbackInstalled =
            fx_code_matches(CameraTweenSlotsSite, "33 C9 83 F8 02 77 3F 8D 14 C5 00 00 00 00 2B D0 8B 45 0C 83 E0 01 03 D0 C1 E2 06 81 C2")
            && fx_operand_is(CameraTweenSlotsSite + 0x1D, ViewportBlocks + 0xDC)
            && fx_code_matches(BtlRand, "55 8B EC 8B 55 08 8B 0C 95 ?? ?? ?? ?? 0F AF 0C 95 ?? ?? ?? ?? 0F B7 04 55")
            && fx_operand_is(BtlRand + 0x11, BtlRandStream9Seed - 9 * 4)
            && fx_operand_is(BtlRand + 0x2C, BtlRandStream9Seed - 9 * 4)
            && fx_code_matches(CameraFallbackSite, "E8 ?? ?? ?? ?? 85 C0 75 1C 50 E8 ?? ?? ?? ?? 83 C4 04 80 3D ?? ?? ?? ?? 00 74 0A E8 ?? ?? ?? ?? E8")
            && fx_call_is(CameraFallbackSite, RenderGetExec)
            && fx_operand_is(CameraFallbackSite + 0x14, CameraFallbackGate);
        if (!_viewportRollbackInstalled)
            _logger.Error("[Vrr] Viewport tween, RNG stream 9 or camera fallback bytes differ from build 25501027; presentation passes step them.");
        return true;
    }

    // Before the clock decides the pass kind, so nothing that runs in the loop can precede it.
    private void viewport_snapshot()
    {
        sphere_probe_enter();
        _viewportSnapshotTaken = false;
        if (!_viewportRollbackInstalled) return;
        new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(ViewportBlocks), ViewportBlockBytes).CopyTo(_viewportSnapshot);
        _stream9Snapshot = FhUtil.get_at<uint>(BtlRandStream9Seed);
        _viewportSnapshotTaken = true;
    }

    // shortcut: LOOP-005 (clearing the camera fallback gate on presentation passes) is off. With it the
    // battle-entry transition flickered; the camera interpolation already owns presentation passes.
    private const bool CameraFallbackGateHold = false;

    private void viewport_presentation_begin()
    {
        _cameraGateCleared = false;
        if (!_viewportRollbackInstalled || !CameraFallbackGateHold) return;
        _cameraGateSaved = FhUtil.get_at<byte>(CameraFallbackGate);
        if (_cameraGateSaved == 0) return;
        FhUtil.set_at(CameraFallbackGate, (byte)0);
        _cameraGateCleared = true;
        _cameraGateSkips++;
    }

    private void viewport_restore(bool presentation)
    {
        sphere_probe_exit(presentation);
        if (_cameraGateCleared && FhUtil.get_at<byte>(CameraFallbackGate) == 0)
            FhUtil.set_at(CameraFallbackGate, _cameraGateSaved);
        _cameraGateCleared = false;
        if (!presentation || !_viewportSnapshotTaken) return;
        var live = new Span<byte>(FhUtil.ptr_at<byte>(ViewportBlocks), ViewportBlockBytes);
        if (!live.SequenceEqual(_viewportSnapshot)) _viewportChanged++;
        _viewportSnapshot.CopyTo(live);
        FhUtil.set_at(BtlRandStream9Seed, _stream9Snapshot);
        _viewportRollbacks++;
        _viewportSnapshotTaken = false;
    }
}
