namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The ten-pass lead-in that move type 9 waits out before it starts interpolating.
///
///     AtelWorkerMoveProc gates case 9 on the move record's own counter at +0x06 being below
///     worker+0xb16, and that counter advances once per pass. The threshold has exactly one writer in
///     the whole image, AtelInitBasicWork loading 10 into eax and storing it as a word, so it is an
///     engine constant with no ATEL opcode behind it: none of the four deadline setters this module
///     already corrects can reach it. At 60 Hz the wind-up therefore elapses in about 166 ms against
///     an authored 333 ms, and the object it belongs to sits still for the first half of it while
///     everything around it has already moved - which is a stutter immediately before the motion
///     rather than during it.
///
///     It is corrected as an image patch on the immediate rather than as a hook on the initialiser,
///     for two reasons. The catalog's calling convention for AtelInitBasicWork is Ghidra's default
///     and not a measurement, and a detour with the wrong convention on an init path is the failure
///     that cost this project four wrong hooks already; and the immediate is loaded once per worker
///     init, so patching it costs nothing per frame. The store is register-relative, so nothing in
///     the signature moves with the image base - the lesson of the battle cursor patch, whose first
///     version embedded a relocated address and silently refused on most runs.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary><c>B8 0A 00 00 00</c>, <c>mov eax, 0xA</c>. The immediate is the last four bytes.</summary>
    private static ReadOnlySpan<byte> MoveLeadInLoadPrefix => [0xB8];
    private const int MoveLeadInImmediateOffset = 1;
    private const byte VanillaMoveLeadIn = 10;

    /// <summary>
    ///     <c>66 89 86 16 0B 00 00</c>, <c>mov word ptr [esi+0xB16], ax</c>, the store the load above
    ///     feeds. Checked as well as the load, because a bare <c>mov eax, 0xA</c> is not distinctive
    ///     and the pair is.
    /// </summary>
    private static ReadOnlySpan<byte> MoveLeadInStore =>
        [0x66, 0x89, 0x86, 0x16, 0x0B, 0x00, 0x00];

    private const int MoveLeadInStoreOffset = 0x25;

    private int _move_lead_in;

    /// <summary>
    ///     The last value that was worth a log line.
    ///
    ///     10 * Scale is sensitive to a measurement that has not settled: the first seconds of a run
    ///     measured 54.1 fps and the patch wrote 18, then 17, a line per telemetry sample. Rewriting
    ///     is correct - the value should track the rate, and the journal restores the earliest
    ///     original because it unwinds in reverse - but a pass either way is not news.
    /// </summary>
    private int _move_lead_in_logged;

    /// <summary>
    ///     Called once per presented frame from <c>apply_rate_patches</c>, and a compare and a return
    ///     unless the threshold it derives has changed.
    /// </summary>
    private void patch_move_lead_in()
    {
        if (!_config.AtelWorkerLeadIn) return;

        // The reader compares the field as a signed short, so the product has to stay inside one.
        int passes = Math.Clamp((int)Math.Round(VanillaMoveLeadIn * Scale), 1, 255);
        if (passes == _move_lead_in) return;

        // Recorded before the verification, like the other two image patches: this field is what
        // stops the check running once per presented frame, so a mismatch that left it unset would
        // log an error every frame.
        _move_lead_in = passes;

        byte* load  = FhUtil.ptr_at<byte>(EngineAddresses.AtelMoveLeadInLoad);
        byte* store = load + MoveLeadInStoreOffset;
        byte current = load[MoveLeadInImmediateOffset];

        bool matches = load[0] == MoveLeadInLoadPrefix[0]
                    && load[2] == 0 && load[3] == 0 && load[4] == 0
                    && current >= 1 && current <= 255
                    && new ReadOnlySpan<byte>(store, MoveLeadInStore.Length).SequenceEqual(MoveLeadInStore);

        if (!matches)
        {
            _logger.Error($"[Vrr] Move lead-in load at RVA 0x{EngineAddresses.AtelMoveLeadInLoad:X} does not " +
                          $"carry the expected mov eax, imm32 followed by the +0xB16 store; nothing written. Found " +
                          $"{load[0]:X2} imm=0x{current:X2} and store {store[0]:X2} {store[1]:X2} {store[2]:X2}.");
            return;
        }

        _patches.Write(EngineAddresses.AtelMoveLeadInLoad + MoveLeadInImmediateOffset, [(byte)passes]);

        if (Math.Abs(passes - _move_lead_in_logged) < 2) return;

        _move_lead_in_logged = passes;
        _logger.Info($"[Vrr] Move lead-in set to {passes} passes, from {VanillaMoveLeadIn}.");
    }
}
