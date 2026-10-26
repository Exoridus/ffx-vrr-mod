namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint DiagnosticPadPoll = 0x489a80;
    private long _padObservationSequence;
    private int _padObservationFailed;

    private void init_pad_observations()
    {
        if (_diagnostics?.IsCapturing != true) return;
        bool available = fx_code_matches(PadRecordBaseSite, "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3")
            && fx_operand_is(PadRecordBaseSite + 0xd, PadRecords);
        diagnostic_event("probe_binding", new { name = "pad_records", available });
        if (!available) return;
        diagnostic_hook(HookSiteId.DiagnosticBindingsPadPollObservation, "pad_poll", DiagnosticPadPoll,
            [0x56, 0x33, 0xf6, 0x6a, 0x00, 0x56, 0xe8, 0x85, 0xf3, 0xff, 0xff, 0x50, 0x6a, 0x00, 0x56,
             0xe8, 0xbc, 0xf5, 0xff, 0xff, 0x46, 0x83, 0xc4, 0x14, 0x83, 0xfe, 0x02, 0x7c, 0xe6, 0x5e, 0xc3],
            () => new FhCallHandle<d_render_void>(new FhCallLocation(DiagnosticPadPoll, 0)).hook(this, h_diag_pad_poll));
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_diag_pad_poll()
    {
        new FhCallHandle<d_render_void>(new FhCallLocation(DiagnosticPadPoll, 0)).chain_from(h_diag_pad_poll).fnptr!();
        if (_diagnostics?.IsCapturing != true || System.Threading.Volatile.Read(ref _padObservationFailed) != 0) return;
        try
        {
            long returned = Stopwatch.GetTimestamp();
            long sequence = System.Threading.Interlocked.Increment(ref _padObservationSequence);
            Span<byte> snapshot = stackalloc byte[32];
            for (int port = 0; port < 2; port++)
            {
                if (_diagnostics?.IsCapturing != true || System.Threading.Volatile.Read(ref _padObservationFailed) != 0) return;
                long start = Stopwatch.GetTimestamp();
                bool valid = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100 + 0x80), snapshot);
                long end = Stopwatch.GetTimestamp();
                uint[]? words = valid ? MemoryMarshal.Cast<byte, uint>(snapshot).ToArray() : null;
                diagnostic_event("pad_observation", new { poll_sequence = sequence, port, stage = "post_poll",
                    poll_returned_ticks = returned, observation_start_ticks = start, observation_end_ticks = end,
                    valid, raw_words = words });
            }
        }
        catch (Exception e)
        {
            if (System.Threading.Interlocked.Exchange(ref _padObservationFailed, 1) == 0)
                diagnostic_event("pad_observation_stopped", new { error = $"{e.GetType().Name}: {e.Message}" });
        }
    }
}