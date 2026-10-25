namespace Fahrenheit.Mods.Vrr;

internal readonly record struct GameOverVfxHeader(int DeclaredCount, uint Address)
{
    internal int CaptureCount => Math.Clamp(DeclaredCount, 0, 4);

    internal static GameOverVfxHeader Read(ReadOnlySpan<byte> bytes)
        => bytes.Length < 0x98 ? default
            : new(BitConverter.ToInt32(bytes[0x90..]), BitConverter.ToUInt32(bytes[0x94..]));
}

internal sealed class GameOverProbeWindow(int frameLimit, int eventLimit)
{
    private long? _firstFrame;
    private int _events;
    private bool _stopped;

    internal bool IsCapturing(long frame) => !_stopped && _firstFrame is { } first
        && frame >= first && frame - first < frameLimit && _events < eventLimit;

    internal bool TryRecord(int magicId, long frame)
    {
        if (_stopped) return false;
        if (magicId != 670)
        {
            if (_firstFrame.HasValue) _stopped = true;
            return false;
        }
        _firstFrame ??= frame;
        if (!IsCapturing(frame)) { _stopped = true; return false; }
        _events++;
        return true;
    }
}

public unsafe sealed partial class VrrModule
{
    private const nint GameOverMagicId = 0x864ca0;
    private const nint GameOverVfxUpdate = 0x2458a0;
    private const nint GameOverVfxDataUpdate = 0x318610;
    private const nint GameOverVfxGet = 0x2453d0;
    private const nint GameOverBufferId = 0x23ff50;
    private readonly GameOverProbeWindow _gameoverProbeWindow = new(180, 8192);
    private bool _gameoverProbeEnabled;
    private long _gameoverGpuDraws, _gameoverGpuElements, _gameoverSubmissions;

    private void init_gameover_probe()
    {
        if (!_config.Diagnostics || !_config.EffectProbe) return;
        _gameoverProbeEnabled = true;
        diagnostic_hook("gameover_vfx_update", GameOverVfxUpdate,
            [0x55, 0x8b, 0xec],
            () => new FhCallHandle<d_gameover_vfx_update>(new FhCallLocation(GameOverVfxUpdate, 0))
                .hook(this, h_gameover_vfx_update));
        diagnostic_hook("gameover_vfx_data_update", GameOverVfxDataUpdate,
            [0x55, 0x8b, 0xec, 0x81, 0xec, 0xfc, 0x00, 0x00, 0x00],
            () => new FhCallHandle<d_gameover_vfx_data>(new FhCallLocation(GameOverVfxDataUpdate, 0))
                .hook(this, h_gameover_vfx_data));
        diagnostic_event("gameover_probe_armed", new { frame_limit = 180, event_limit = 8192,
            effect_hold = _config.EffectHold, effect_hold_in_battle = _config.EffectHoldInBattle,
            fades = _config.Fades, cross_fade_hold = _config.CrossFadeHold,
            coverage = "first magic670 interval; native VFX update calls/input RGBA, buffer id, first four 0x6c-byte descriptors; snapshots can precede async fill completion; GPU counts cover all scene ordinary/indexed draws, not only the overlay" });
    }

    private bool gameover_probe_record()
        => _gameoverProbeEnabled && _gameoverProbeWindow.TryRecord(FhUtil.get_at<int>(GameOverMagicId), _frames);

    private static string? gameover_snapshot(nint address, int size)
    {
        Span<byte> bytes = stackalloc byte[size];
        return MemoryProbe.TryCopy(address, bytes) ? Convert.ToHexStringLower(bytes) : null;
    }

    private static object gameover_render_snapshot(nint data)
    {
        Span<byte> bytes = stackalloc byte[0xc8];
        bool readable = MemoryProbe.TryCopy(data, bytes);
        var header = readable ? GameOverVfxHeader.Read(bytes) : default;
        var descriptors = new string?[header.CaptureCount];
        for (int i = 0; i < descriptors.Length; i++)
        {
            ulong address = (ulong)header.Address + (uint)i * 0x6c;
            // A wrapped 32-bit pointer must not turn into an unrelated readable allocation.
            if (header.Address != 0 && address <= uint.MaxValue - 0x6b)
                descriptors[i] = gameover_snapshot(unchecked((nint)(uint)address), 0x6c);
        }
        return new { readable, header = readable ? Convert.ToHexStringLower(bytes) : null,
            declared_count = header.DeclaredCount, descriptor_address = header.Address, descriptors };
    }

    private static int gameover_buffer_id()
        => new FhCallHandle<d_gameover_buffer_id>(new FhCallLocation(GameOverBufferId, 0)).fnptr!();

    private void diagnostic_gameover_effect(int mode, bool held, string phase)
    {
        if (!gameover_probe_record()) return;
        try
        {
            diagnostic_event("gameover_effect", new { mode, held, phase,
                submissions = _gameoverSubmissions, gpu_draws = _gameoverGpuDraws,
                gpu_elements = _gameoverGpuElements,
                buffer_id = gameover_buffer_id(),
                filter_slot0_counter_alpha = gameover_snapshot((nint)FhUtil.ptr_at<byte>(0xf0070c), 4),
                particle_do_not_render = FhUtil.get_at<int>(0xefb7d0) });
        }
        catch (Exception) { /* Observation must not interrupt effect dispatch. */ }
    }

    private void diagnostic_gameover_gpu_draw(uint count)
    {
        if (!_gameoverProbeEnabled || !_gameoverProbeWindow.IsCapturing(_frames)) return;
        _gameoverGpuDraws++;
        _gameoverGpuElements += count;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_gameover_vfx_update(uint identityLow, uint identityHigh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint d_gameover_vfx_data(uint identityLow, uint identityHigh, nint geometry, nint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint d_gameover_get_vfx(uint identityLow, uint identityHigh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_gameover_buffer_id();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_gameover_vfx_update(uint identityLow, uint identityHigh)
    {
        new FhCallHandle<d_gameover_vfx_update>(new FhCallLocation(GameOverVfxUpdate, 0))
            .chain_from(h_gameover_vfx_update).fnptr!(identityLow, identityHigh);
        if (!gameover_probe_record()) return;
        try
        {
            _gameoverSubmissions++;
            nint data = new FhCallHandle<d_gameover_get_vfx>(new FhCallLocation(GameOverVfxGet, 0))
                .fnptr!(identityLow, identityHigh);
            diagnostic_event("gameover_vfx_submit", new { identity_low = identityLow, identity_high = identityHigh,
                render_data = (long)data, buffer_id = gameover_buffer_id(), render = gameover_render_snapshot(data),
                submissions = _gameoverSubmissions, gpu_draws = _gameoverGpuDraws });
        }
        catch (Exception) { /* Failed capture must retain the native submission. */ }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_gameover_vfx_data(uint identityLow, uint identityHigh, nint geometry, nint state)
    {
        bool capture = gameover_probe_record();
        if (capture)
        {
            try
            {
                diagnostic_event("gameover_vfx_input", new { identity_low = identityLow, identity_high = identityHigh,
                    geometry = (long)geometry, state = (long)state,
                    rgba = gameover_snapshot(state + 0x18, 4), flags = gameover_snapshot(state, 0x48) });
            }
            catch (Exception) { /* Observation must preserve the native update. */ }
        }
        uint result = new FhCallHandle<d_gameover_vfx_data>(new FhCallLocation(GameOverVfxDataUpdate, 0))
            .chain_from(h_gameover_vfx_data).fnptr!(identityLow, identityHigh, geometry, state);
        if (capture) diagnostic_event("gameover_vfx_result", new { identity_low = identityLow, identity_high = identityHigh, result });
        return result;
    }
}
