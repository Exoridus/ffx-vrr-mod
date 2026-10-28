namespace Fahrenheit.Mods.Vrr;

internal static class FarplaneDiagnosticPacket
{
    internal const int WordCount = 40;
    internal const string Layout = "kind(1=begin_scene,2=end_scene,3=fmv_draw,4=clear_color),phase(0=before,1=after),function_rva,object,renderer,argument,native_result,movie_id,movie_play,movie_picture,draw_characters,renderer_valid_mask,renderer_flags,shader,camera,target_flags,20_color_and_depth_descriptor_words,4_clear_color_argument_bits; valid_mask bit0=header bit1=targets; clear color is present only for kind4; timestamp/frame/loop/thread are in the record header";

    internal static void CaptureRenderer(Span<uint> words, ReadOnlySpan<byte> header, ReadOnlySpan<byte> targets)
    {
        words.Slice(11, 25).Clear();
        if (header.Length >= 0x60)
        {
            words[11] |= 1;
            words[12] = BitConverter.ToUInt32(header[4..]);
            words[13] = BitConverter.ToUInt32(header[0x48..]);
            words[14] = BitConverter.ToUInt32(header[0x5c..]);
        }
        if (targets.Length >= 0x54)
        {
            words[11] |= 2;
            words[15] = BitConverter.ToUInt32(targets[0x50..]);
            MemoryMarshal.Cast<byte, uint>(targets[..0x50]).CopyTo(words[16..36]);
        }
    }
}

public unsafe sealed partial class VrrModule
{
    private const nint FarplaneBeginScene = 0x1b5e30;
    private const nint FarplaneEndScene = 0x1b6950;
    private const nint FarplaneFmvDraw = 0x2d7570;
    private const nint FarplaneClearColor = 0x1bada0;
    private const nint FarplaneMovieId = 0x840e28;
    private const nint FarplaneMoviePlay = 0xd2a008;
    private int _farplaneProbeFailed;

    private void init_farplane_diagnostics()
    {
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Warning($"[Vrr] Farplane diagnostic hooks unavailable: {error}");
            return;
        }
        diagnostic_hook(HookSiteId.FarplaneDiagnosticsFarplaneBeginScene, "farplane_begin_scene", FarplaneBeginScene,
            [0x55, 0x8b, 0xec, 0x83, 0xec, 0x08, 0x56, 0x8b, 0xf1, 0x8b, 0x46, 0x04, 0xa8, 0x01],
            () => new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneBeginScene, 0)).hook(this, with_hook_continuation<d_farplane_scene>(h_farplane_begin_scene)));
        diagnostic_hook(HookSiteId.FarplaneDiagnosticsFarplaneEndScene, "farplane_end_scene", FarplaneEndScene,
            [0x55, 0x8b, 0xec, 0x83, 0xec, 0x08, 0x56, 0x8b, 0xf1, 0x8b, 0x4e, 0x04, 0x8b, 0xc1, 0xf7, 0xd0, 0xa8, 0x01],
            () => new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneEndScene, 0)).hook(this, with_hook_continuation<d_farplane_scene>(h_farplane_end_scene)));
        diagnostic_hook(HookSiteId.FarplaneDiagnosticsFarplaneFmvDraw, "farplane_fmv_draw", FarplaneFmvDraw,
            [0x55, 0x8b, 0xec, 0x53, 0x8b, 0xd9, 0x80, 0xbb, 0xd2, 0x06, 0x00, 0x00, 0x00],
            () => new FhCallHandle<d_farplane_fmv_draw>(new FhCallLocation(FarplaneFmvDraw, 0)).hook(this, with_hook_continuation<d_farplane_fmv_draw>(h_farplane_fmv_draw)));
        diagnostic_hook(HookSiteId.FarplaneDiagnosticsFarplaneClearColor, "farplane_clear_color", FarplaneClearColor,
            [0x55, 0x8b, 0xec, 0x83, 0xec, 0x08, 0x53, 0x56, 0x57, 0x8b, 0xf1],
            () => new FhCallHandle<d_farplane_clear_color>(new FhCallLocation(FarplaneClearColor, 0)).hook(this, with_hook_continuation<d_farplane_clear_color>(h_farplane_clear_color)));
    }

    private bool diagnostic_farplane(uint kind, bool after, nint function, nint instance,
        nint renderer, uint argument, uint result, bool paired = false,
        uint red = 0, uint green = 0, uint blue = 0, uint alpha = 0)
    {
        if (_binaryDiagnostics?.IsCapturing != true || System.Threading.Volatile.Read(ref _farplaneProbeFailed) != 0) return false;
        try
        {
            Span<byte> movie = stackalloc byte[8];
            Span<byte> flags = stackalloc byte[8];
            bool movieReadable = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(FarplaneMovieId), movie);
            bool flagsReadable = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(FarplaneMoviePlay), flags);
            // A failed background initializer leaves picture mode unset; active movies provide a native comparison.
            if (!movieReadable || !flagsReadable
                || (!paired && BitConverter.ToUInt32(movie) != 104 && BitConverter.ToUInt32(flags) == 0)) return false;
            Span<uint> packet = stackalloc uint[FarplaneDiagnosticPacket.WordCount];
            packet.Clear();
            packet[0] = kind;
            packet[1] = after ? 1u : 0u;
            packet[2] = unchecked((uint)function);
            packet[3] = unchecked((uint)(nuint)instance);
            packet[4] = unchecked((uint)(nuint)renderer);
            packet[5] = argument;
            packet[6] = result;
            packet[7] = BitConverter.ToUInt32(movie);
            packet[8] = BitConverter.ToUInt32(flags);
            packet[9] = BitConverter.ToUInt32(flags[4..]);
            packet[10] = BitConverter.ToUInt32(movie[4..]);
            Span<byte> header = stackalloc byte[0x60];
            Span<byte> targets = stackalloc byte[0x54];
            bool headerReadable = MemoryProbe.TryCopy(renderer, header);
            bool targetsReadable = renderer != 0 && MemoryProbe.TryCopy(renderer + 0xe8, targets);
            FarplaneDiagnosticPacket.CaptureRenderer(packet, headerReadable ? header : [], targetsReadable ? targets : []);
            packet[36] = red;
            packet[37] = green;
            packet[38] = blue;
            packet[39] = alpha;
            _binaryDiagnostics.TryWrite(18, _frames, System.Threading.Interlocked.Read(ref _diagnosticLoopCounter), packet);
            return true;
        }
        catch (Exception exception)
        {
            if (System.Threading.Interlocked.Exchange(ref _farplaneProbeFailed, 1) == 0)
            {
                try
                {
                    diagnostic_event("farplane_probe_failed", new { error = exception.Message });
                    _logger.Warning("[Vrr] Farplane diagnostic sampling stopped after a managed failure; native rendering continues.");
                }
                catch (Exception) { /* A logging failure must not escape the native callback. */ }
            }
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint d_farplane_scene(nint renderer, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint d_farplane_fmv_draw(nint manager, nint renderer);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint d_farplane_clear_color(nint renderer, uint red, uint green, uint blue, uint alpha);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_farplane_begin_scene(nint renderer, uint flags)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneBeginScene, 0)).chain_from(h_farplane_begin_scene).fnptr)(renderer, flags);

        bool capture = diagnostic_farplane(1, false, FarplaneBeginScene, renderer, renderer, flags, 0);
        uint result = new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneBeginScene, 0))
            .chain_from(h_farplane_begin_scene).fnptr!(renderer, flags);
        if (capture) diagnostic_farplane(1, true, FarplaneBeginScene, renderer, renderer, flags, result, true);
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_farplane_end_scene(nint renderer, uint flags)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneEndScene, 0)).chain_from(h_farplane_end_scene).fnptr)(renderer, flags);

        bool capture = diagnostic_farplane(2, false, FarplaneEndScene, renderer, renderer, flags, 0);
        uint result = new FhCallHandle<d_farplane_scene>(new FhCallLocation(FarplaneEndScene, 0))
            .chain_from(h_farplane_end_scene).fnptr!(renderer, flags);
        if (capture) diagnostic_farplane(2, true, FarplaneEndScene, renderer, renderer, flags, result, true);
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_farplane_fmv_draw(nint manager, nint renderer)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_farplane_fmv_draw>(new FhCallLocation(FarplaneFmvDraw, 0)).chain_from(h_farplane_fmv_draw).fnptr)(manager, renderer);

        bool capture = diagnostic_farplane(3, false, FarplaneFmvDraw, manager, renderer, 0, 0);
        uint result = new FhCallHandle<d_farplane_fmv_draw>(new FhCallLocation(FarplaneFmvDraw, 0))
            .chain_from(h_farplane_fmv_draw).fnptr!(manager, renderer);
        if (capture) diagnostic_farplane(3, true, FarplaneFmvDraw, manager, renderer, 0, result, true);
        return result;
    }

    // Four raw stack words preserve the native float payloads, including NaNs and signed zero.
    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_farplane_clear_color(nint renderer, uint red, uint green, uint blue, uint alpha)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_farplane_clear_color>(new FhCallLocation(FarplaneClearColor, 0)).chain_from(h_farplane_clear_color).fnptr)(renderer, red, green, blue, alpha);

        bool capture = diagnostic_farplane(4, false, FarplaneClearColor, renderer, renderer, 0, 0,
            red: red, green: green, blue: blue, alpha: alpha);
        uint result = new FhCallHandle<d_farplane_clear_color>(new FhCallLocation(FarplaneClearColor, 0))
            .chain_from(h_farplane_clear_color).fnptr!(renderer, red, green, blue, alpha);
        if (capture) diagnostic_farplane(4, true, FarplaneClearColor, renderer, renderer, 0, result, true, red, green, blue, alpha);
        return result;
    }
}
