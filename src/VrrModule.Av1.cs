namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private Av1Native? _av1Native;
    private nint _av1Player;
    private nint _av1FailedPlayer;
    private FmvReplacementMetadata? _av1Metadata;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void d_av1_worker(nint player);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_av1_queue_get(nint queue);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void d_av1_queue_step(nint queue);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_av1_queue_reserve(nint queue, uint bytes);

    private bool init_av1_worker() => hook_or_log(HookSiteId.Av1AV1MovieDecodingWorker, "AV1 movie decoding worker", 0x6279b0,
        () => new FhCallHandle<d_av1_worker>(new FhCallLocation(0x6279b0, 0)).hook(this, with_hook_continuation<d_av1_worker>(h_av1_worker)));

    private void prepare_av1_worker(nint manager)
    {
        System.Threading.Interlocked.Exchange(ref _av1FailedPlayer, 0);
        _av1Player = 0;
        _av1Metadata = null;
        if (_fmv_selection is not { Metadata: { VideoCodec: "av1" } metadata } selection
            || manager != _fmv_selection_manager
            || !string.Equals(Marshal.PtrToStringAnsi(manager + EngineAddresses.FmvPathOffset),
                selection.NativePath, StringComparison.OrdinalIgnoreCase)) return;
        _av1Metadata = metadata;
        _av1Player = manager + 0x38;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private void h_av1_worker(nint player)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_av1_worker>(new FhCallLocation(0x6279b0, 0)).chain_from(h_av1_worker).fnptr)(player);
            return;
        }

        if (player != _av1Player || _av1Metadata is not { } metadata || _av1Native is not { } api)
        {
            new FhCallHandle<d_av1_worker>(new FhCallLocation(0x6279b0, 0)).chain_from(h_av1_worker).fnptr!(player);
            return;
        }
        nint decoder = 0;
        uint frames = 0;
        try
        {
            configure_av1_clock(player, metadata);
            decoder = api.Open(metadata.Width, metadata.Height);
            if (decoder == 0) throw new InvalidOperationException("AV1 decoder initialization failed.");
            _logger.Info($"[Vrr] AV1 worker started: {metadata.Width}x{metadata.Height}, D3D11 preferred, dav1d fallback, x86.");
            var read = new FhCallHandle<d_av1_queue_get>(new FhCallLocation(0x63dd90, 0)).fnptr!;
            var consume = new FhCallHandle<d_av1_queue_step>(new FhCallLocation(0x63ddc0, 0)).fnptr!;
            while (*(int*)(player + 0xec) != 1)
            {
                nint packet = read(player);
                if (packet == 0) break;
                int result;
                do
                {
                    result = api.Decode(decoder, packet + 4, *(uint*)packet);
                    if (result != 0 && result != -11) throw new InvalidDataException($"AV1 decode failed: {result}.");
                    if (!publish_av1_frames(player, decoder, api, metadata, ref frames)) return;
                } while (result == -11 && *(int*)(player + 0xec) != 1);
                consume(player);
            }
            if (*(int*)(player + 0xec) != 1)
            {
                int result;
                do
                {
                    result = api.Flush(decoder);
                    if (result != 0 && result != -11) throw new InvalidDataException($"AV1 drain failed: {result}.");
                    if (!publish_av1_frames(player, decoder, api, metadata, ref frames)) return;
                } while (result == -11 && *(int*)(player + 0xec) != 1);
            }
        }
        catch (Exception error)
        {
            System.Threading.Interlocked.Exchange(ref _av1FailedPlayer, player);
            var cancel = new FhCallHandle<d_av1_queue_step>(new FhCallLocation(0x63dea0, 0)).fnptr!;
            cancel(player);
            cancel(player + 0x40);
            try { _logger.Error($"[Vrr] AV1 worker failed: {error.Message}"); } catch { }
        }
        finally
        {
            if (decoder != 0) api.Close(decoder);
            *(byte*)(player + 0x154) = 0;
            try { _logger.Info($"[Vrr] AV1 worker finished: {frames} images; stopped={*(int*)(player + 0xec) == 1}."); } catch { }
        }
    }

    private void configure_av1_clock(nint player, FmvReplacementMetadata metadata)
    {
        // Older libwebm can omit the AV1 track rate and fall back to 29.97 fps.
        *(double*)(player + 0x108) = 1000d / metadata.FrameRate;
    }
    private bool abort_failed_av1_movie()
    {
        nint failed = System.Threading.Interlocked.Exchange(ref _av1FailedPlayer, 0);
        if (failed == 0 || failed != _av1Player || _fmv_selection?.Metadata?.VideoCodec != "av1") return false;
        // Native stop joins the decoder worker, so it must run on the movie owner thread.
        FhCall.graphicDestroyFmv.fnptr!();
        return true;
    }
    private bool publish_av1_frames(nint player, nint decoder, Av1Native api,
        FmvReplacementMetadata metadata, ref uint frames)
    {
        var reserve = new FhCallHandle<d_av1_queue_reserve>(new FhCallLocation(0x63dec0, 0)).fnptr!;
        var commit = new FhCallHandle<d_av1_queue_step>(new FhCallLocation(0x63df00, 0)).fnptr!;
        uint bytes = checked((uint)(metadata.Width * metadata.Height * 3 / 2));
        while (*(int*)(player + 0xec) != 1)
        {
            nint image = api.GetFrame(decoder);
            if (image == 0)
            {
                int error = api.Error(decoder);
                if (error != 0) throw new InvalidDataException($"AV1 image retrieval failed: {error}.");
                return true;
            }
            if (frames == 0) _logger.Info($"[Vrr] AV1 backend selected: {api.Backend(decoder)}.");
            nint output = reserve(player + 0x40, bytes);
            if (output == 0) return false;
            // The native ring owns the copy before the decoder advances or releases its picture.
            Buffer.MemoryCopy((void*)*(nint*)(image + 0x24), (void*)(output + 4), bytes, bytes);
            commit(player + 0x40);
            frames++;
        }
        return false;
    }
}

internal unsafe sealed class Av1Native
{
    private readonly delegate* unmanaged[Cdecl]<int, int, int, int, nint> _open;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, uint, int> _decode;
    private readonly delegate* unmanaged[Cdecl]<nint, nint> _picture;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _error;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _close;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _flush;
    private readonly delegate* unmanaged[Cdecl]<nint, uint> _backend;

    internal Av1Native(string path)
    {
        if (IntPtr.Size != 4) throw new PlatformNotSupportedException("AV1 movie bridge requires x86.");
        nint library = NativeLibrary.Load(path, typeof(Av1Native).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try
        {
            var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(library, "fh_av1_abi");
            if (abi() != 2) throw new InvalidDataException("Unsupported AV1 bridge ABI.");
            _open = (delegate* unmanaged[Cdecl]<int, int, int, int, nint>)NativeLibrary.GetExport(library, "fh_av1_open");
            _decode = (delegate* unmanaged[Cdecl]<nint, nint, uint, int>)NativeLibrary.GetExport(library, "fh_av1_decode");
            _picture = (delegate* unmanaged[Cdecl]<nint, nint>)NativeLibrary.GetExport(library, "fh_av1_get_frame");
            _error = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "fh_av1_error");
            _close = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "fh_av1_close");
            _flush = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(library, "fh_av1_flush");
            _backend = (delegate* unmanaged[Cdecl]<nint, uint>)NativeLibrary.GetExport(library, "fh_av1_backend");
            // Native workers can retain callbacks until process exit; keep this library loaded.
        }
        catch { NativeLibrary.Free(library); throw; }
    }
    internal nint Open(int width, int height) => _open(width, height, Math.Min(Environment.ProcessorCount, 8), 0);
    internal int Decode(nint decoder, nint bytes, uint size) => _decode(decoder, bytes, size);
    internal nint GetFrame(nint decoder) => _picture(decoder);
    internal int Error(nint decoder) => _error(decoder);
    internal int Flush(nint decoder) => _flush(decoder);
    internal string Backend(nint decoder) => _backend(decoder) == 1 ? "D3D11VA" : "dav1d";
    internal void Close(nint decoder) => _close(decoder);
}
