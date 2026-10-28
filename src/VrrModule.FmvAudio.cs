namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint FmvTrackGetType = 0x27b10;
    private const nint FmvNativeStop = 0x628350;
    private sealed record FmvAudioScope(nint Player, nint Segment, int TrackNumber);
    private FmvAudioScope? _fmvAudioPending, _fmvAudioActive;
    private bool _fmvAudioHooksReady;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_fmv_track_type(nint track);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte d_fmv_native_stop(nint player);

    private bool init_fmv_audio_hooks()
    {
        _fmvAudioHooksReady = fmv_audio_guard(FmvTrackGetType, [0x8b, 0x41, 0x20, 0xc3, 0xcc])
            && fmv_audio_guard(FmvNativeStop, [0x55, 0x8b, 0xec, 0x51, 0x57, 0x8b, 0xf9])
            && hook_or_log(HookSiteId.FmvAudioMovieAudioTrackType, "movie audio track type", FmvTrackGetType,
                () => new FhCallHandle<d_fmv_track_type>(new FhCallLocation(FmvTrackGetType, 0)).hook(this, with_hook_continuation<d_fmv_track_type>(h_fmv_track_type)))
            && hook_or_log(HookSiteId.FmvAudioMovieAudioScopeStop, "movie audio scope stop", FmvNativeStop,
                () => new FhCallHandle<d_fmv_native_stop>(new FhCallLocation(FmvNativeStop, 0)).hook(this, with_hook_continuation<d_fmv_native_stop>(h_fmv_native_stop)));
        return _fmvAudioHooksReady;
    }

    private bool fmv_audio_guard(nint rva, byte[] expected)
    {
        Span<byte> actual = stackalloc byte[expected.Length];
        if (MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(rva), actual) && actual.SequenceEqual(expected)) return true;
        _logger.Error($"[Vrr] Movie audio hook refused: unexpected code at RVA 0x{rva:X}.");
        return false;
    }

    private void begin_fmv_audio_selection(nint player, int trackNumber)
        => System.Threading.Volatile.Write(ref _fmvAudioPending, new(player, 0, trackNumber));
    private void end_fmv_audio_selection()
        => System.Threading.Volatile.Write(ref _fmvAudioPending, null);
    private void clear_fmv_audio_selection(nint player)
    {
        if (System.Threading.Volatile.Read(ref _fmvAudioActive)?.Player == player)
            System.Threading.Volatile.Write(ref _fmvAudioActive, null);
    }

    private int filter_fmv_audio_type(nint track, int nativeType)
    {
        if (nativeType != 2 || !MemoryProbe.IsReadable(track, 0x28)) return nativeType;
        nint owner = *(nint*)(track + 8);
        var active = System.Threading.Volatile.Read(ref _fmvAudioActive);
        var pending = System.Threading.Volatile.Read(ref _fmvAudioPending);
        if (pending is not null && owner != 0 && MemoryProbe.IsReadable(pending.Player + 0x110, IntPtr.Size)
            && *(nint*)(pending.Player + 0x110) == owner && active?.Segment != owner)
        {
            active = pending with { Segment = owner };
            System.Threading.Volatile.Write(ref _fmvAudioActive, active);
            _logger.Info($"[Vrr] Movie audio selected: track {active.TrackNumber}, Segment 0x{owner:X}.");
        }
        // Native reading enqueues every type-2 block, so selecting only the Vorbis header is insufficient.
        return active is not null && active.Segment == owner && *(int*)(track + 0x24) != active.TrackNumber ? 0 : nativeType;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_fmv_track_type(nint track)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_fmv_track_type>(new FhCallLocation(FmvTrackGetType, 0)).chain_from(h_fmv_track_type).fnptr)(track);

        int type = new FhCallHandle<d_fmv_track_type>(new FhCallLocation(FmvTrackGetType, 0))
            .chain_from(h_fmv_track_type).fnptr!(track);
        return filter_fmv_audio_type(track, type);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private byte h_fmv_native_stop(nint player)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_fmv_native_stop>(new FhCallLocation(FmvNativeStop, 0)).chain_from(h_fmv_native_stop).fnptr)(player);

        byte result = new FhCallHandle<d_fmv_native_stop>(new FhCallLocation(FmvNativeStop, 0))
            .chain_from(h_fmv_native_stop).fnptr!(player);
        // Stop joins the reader first; pending selection belongs to a possible reinitialization.
        clear_fmv_audio_selection(player);
        return result;
    }
}
