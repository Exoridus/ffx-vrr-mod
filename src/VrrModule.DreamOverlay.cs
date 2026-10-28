namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The star field of the dream and recall overlay, the layer graphicDrawDream puts over a
///     flashback.
///
///     The three quads of the overlay itself are drawn on every call. The stars are not: they sit
///     behind a counter that the function increments on every call it does not draw them on, and it
///     draws them on the one call where the counter reads zero. The reset is an exact equality
///     against 3 and it writes 0, so the sequence of values the function sees is 0, 1, 2, 0, 1, 2 -
///     one star pass in three calls, which at 30 Hz is the authored 10 Hz. At 60 Hz it becomes 20 Hz.
///
///     The counter advances in the draw half of each main-loop pass, including extra
///     presentation images. Scaling its threshold by the simulation Scale is correct only
///     while draw calls and simulation passes have the same cadence. Independent presentation
///     needs a separate verified draw adapter; threshold scaling alone does not correct it.
///
///     Raising the threshold is safe because the counter only ever moves by one and is only ever
///     reset on exact equality, so every value up to the threshold is reached; the only value that
///     would break it is 1, which the counter skips on its way from the draw pass to 2 and would then
///     never meet again. It is a 16-bit compare with a sign-extended imm8, so the reachable range is
///     2 to 127.
///
///     The alternative was holding the whole function on skipped frames, and it is worse rather than
///     equivalent. graphicDrawDream ends by copying its four UI element blocks into statics, and the
///     path that replays them - the one taken while the menu pause loop is up - draws a single cached
///     star quad where the live path draws the whole loop. A hold would therefore alternate the full
///     star field with one quad at 30 Hz. What only the hold would also correct is the per-frame
///     rand() the three overlay quads take for their UV offset, and that is a shimmer whose rate is
///     not something an eye can pick out.
///
///     Nothing here calls srand(). rand() is the shared CRT state the battle RNG draws from, and
///     reseeding it to make an overlay deterministic would move every roll in the game.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     <c>66 83 F8 imm8</c>, <c>cmp ax, 3</c>. The three opcode bytes are checked before the
    ///     immediate is written.
    /// </summary>
    private static ReadOnlySpan<byte> DreamStarComparePrefix => [0x66, 0x83, 0xF8];
    private const int DreamStarThresholdOffset = 3;
    private const byte VanillaDreamStarThreshold = 3;

    private int _dream_star_threshold;

    /// <summary>
    ///     Called once per presented frame from <c>apply_rate_patches</c>, like the other two image
    ///     patches, and a compare and a return unless the threshold it derives has changed.
    /// </summary>
    private void patch_dream_overlay_stars()
    {
        if (!_config.DreamOverlayStars) return;

        int threshold = Math.Clamp((int)Math.Round(VanillaDreamStarThreshold * Scale), 2, 127);
        if (threshold == _dream_star_threshold) return;
        // A refused compare site must not emit an error on every frame at the same requested rate.
        _dream_star_threshold = threshold;


        byte* site = FhUtil.ptr_at<byte>(EngineAddresses.DreamStarCounterCompare);
        byte current = site[DreamStarThresholdOffset];

        // Either untouched or carrying a threshold this module wrote earlier.
        bool matches = new ReadOnlySpan<byte>(site, DreamStarComparePrefix.Length).SequenceEqual(DreamStarComparePrefix)
                    && current >= 2 && current <= 127;

        if (!matches)
        {
            _logger.Error($"[Vrr] Dream overlay star counter at RVA 0x{EngineAddresses.DreamStarCounterCompare:X} " +
                          $"does not carry the expected compare; nothing written. Found " +
                          $"{site[0]:X2} {site[1]:X2} {site[2]:X2} imm=0x{current:X2}.");
            return;
        }

        // Keep the live phase below the new equality threshold when the cycle shrinks.
        ushort counter = FhUtil.get_at<ushort>(EngineAddresses.DreamStarCounter);
        int phase = Math.Min(counter, current - 1);
        ushort rebased = (ushort)(phase * threshold / current);
        if (_patches.IsPreparing)
            _patches.Write(EngineAddresses.DreamStarCounter, BitConverter.GetBytes(rebased), skipRestoreIfChanged: true);
        else
            FhUtil.set_at(EngineAddresses.DreamStarCounter, rebased);

        _patches.Write(EngineAddresses.DreamStarCounterCompare + DreamStarThresholdOffset, [(byte)threshold]);
        _logger.Info($"[Vrr] Dream overlay star counter set to {threshold} presented frames per pass.");
    }
}
