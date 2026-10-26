namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    [ThreadStatic] private static long _diagnosticLoopId;
    private long _diagnosticLoopCounter;
    private bool _diagnosticExecAvailable;
    private nint _diagnosticSource2, _diagnosticSource3, _diagnosticView;
    private const nint DiagnosticMainLoop = 0x420AE0;
    private const nint DiagnosticGetExec = 0x42D7E0;

    private void init_diagnostic_bindings()
    {
        if (!IndependentPresentationEnabled)
            diagnostic_hook(HookSiteId.DiagnosticBindingsMainLoop, "main_loop", DiagnosticMainLoop, [0x55, 0x8b, 0xec, 0x81, 0xec, 0xa4, 0, 0, 0],
                () => new FhCallHandle<d_diag_loop>(new FhCallLocation(DiagnosticMainLoop, 0)).hook(this, h_diag_loop));
        byte* exec = FhUtil.ptr_at<byte>(DiagnosticGetExec);
        _diagnosticExecAvailable = MemoryProbe.IsReadable((nint)exec, 15)
            && exec[0] == 0x80 && exec[1] == 0x3d && exec[6] == 0 && exec[7] == 0x56
            && exec[8] == 0x0f && exec[9] == 0xbe && exec[10] == 0x35;
        diagnostic_event("probe_binding", new { name = "effective_exec", available = _diagnosticExecAvailable });

        // Relocated absolute operands are read from the validated function, never treated as RVAs.
        byte* fin = FhUtil.ptr_at<byte>(DiagnosticFinalize);
        int matches = 0;
        if (MemoryProbe.IsReadable((nint)fin, 0x600))
            for (int i = 0; i <= 0x600 - 0x4b; i++)
            {
                byte* p = fin + i;
                if (p[0] != 0x39 || p[1] != 0x1d || p[6] != 0x74 || p[7] != 0x3a
                    || p[8] != 0xff || p[9] != 0x75 || p[10] != 0xfc || p[11] != 0x68
                    || p[16] != 0xe8 || p[0x42] != 0x83 || p[0x43] != 0x3d || p[0x48] != 0) continue;
                matches++;
                _diagnosticSource2 = (nint)(nuint)(*(uint*)(p + 2));
                _diagnosticView = (nint)(nuint)(*(uint*)(p + 0x31));
                _diagnosticSource3 = (nint)(nuint)(*(uint*)(p + 0x44));
            }
        bool available = matches == 1 && MemoryProbe.IsReadable(_diagnosticSource2, 4)
            && MemoryProbe.IsReadable(_diagnosticSource3, 4) && MemoryProbe.IsReadable(_diagnosticView, 64);
        if (!available) _diagnosticSource2 = _diagnosticSource3 = _diagnosticView = 0;
        diagnostic_event("probe_binding", new { name = "camera_sources", matches, available });
    }

    private uint? diagnostic_exec() => _diagnosticExecAvailable
        ? new FhCallHandle<d_diag_exec>(new FhCallLocation(DiagnosticGetExec, 0)).fnptr!() : null;

    private object diagnostic_view(long? nativeStart = null, long? nativeEnd = null, bool? nativeReturned = null)
    {
        uint[]? bits = null;
        if (_diagnosticView != 0 && MemoryProbe.IsReadable(_diagnosticView, 64))
        {
            bits = new uint[16];
            new ReadOnlySpan<uint>((void*)_diagnosticView, 16).CopyTo(bits);
        }
        return new { source2 = MemoryProbe.IsReadable(_diagnosticSource2, 4) ? *(uint*)_diagnosticSource2 : (uint?)null,
            source3 = MemoryProbe.IsReadable(_diagnosticSource3, 4) ? *(uint*)_diagnosticSource3 : (uint?)null,
            view_bits = bits, effective_exec = diagnostic_exec(), backlog = FhUtil.get_at<int>(EngineAddresses.ElapsedFrameCountFrameSkip),
            native_start_ticks = nativeStart, native_end_ticks = nativeEnd, native_normal_return = nativeReturned };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_diag_loop(float delta);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint d_diag_exec();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_diag_loop(float delta)
    {
        long previous = _diagnosticLoopId;
        _diagnosticLoopId = System.Threading.Interlocked.Increment(ref _diagnosticLoopCounter);
        _load_passes++;
        render_scheduler_enter_loop(delta);
        diagnostic_raw(3, delta);
        bool measure = _diagnostics?.IsCapturing == true;
        if (measure) diagnostic_event("loop_enter", new { delta_bits = BitConverter.SingleToInt32Bits(delta), view = diagnostic_view() });
        long? nativeStart = null;
        bool nativeReturned = false;
        try
        {
            var original = new FhCallHandle<d_diag_loop>(new FhCallLocation(DiagnosticMainLoop, 0)).chain_from(h_diag_loop).fnptr!;
            if (measure) nativeStart = Stopwatch.GetTimestamp();
            original(delta);
            nativeReturned = true;
        }
        finally
        {
            long? nativeEnd = nativeStart.HasValue ? Stopwatch.GetTimestamp() : null;
            render_scheduler_exit_loop();
            diagnostic_raw(4, delta);
            if (_diagnostics?.IsCapturing == true)
                diagnostic_event("loop_exit", diagnostic_view(nativeStart, nativeEnd, nativeStart.HasValue ? nativeReturned : null));
            _diagnosticLoopId = previous;
        }
    }
}
