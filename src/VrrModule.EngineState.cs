namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Engine state that decides open questions and costs nothing to read. No hooks: every value
///     here is a global the telemetry line samples, which is why these questions can be answered
///     without a probe that could itself end the process.
///
///     Each entry exists because a static reading left exactly one thing unmeasured.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     Character texture animation slots. 64 of them at tex_anim_wk, stride 0x30. Byte +0x0D is
    ///     the format the advance dispatches on - 0 is the old path, 1 the new - and byte +0x0E is
    ///     the step the new path adds to its sequence accumulator.
    /// </summary>
    private const nint TexAnimWorkBase = 0x1F10000;
    private const int TexAnimSlotStride = 0x30;
    private const int TexAnimSlotCount = 64;
    private const int TexAnimFormatOffset = 0x0D;
    private const int TexAnimTimerOffset = 0x0E;

    /// <summary>
    ///     Reports what the four unresolved questions need, in one line:
    ///
    ///     <c>texanim_enable</c> is EnableGameTextureAnimation. The image ships it as 1, but
    ///     FUN_006bb810 sets it back to 0 on one branch, and a 0 here means the whole character
    ///     texture animation path is dead on PC and the timer question is moot.
    ///
    ///     <c>texanim_fmt</c> is how the live slots split across the two formats. The 427-of-872
    ///     asset count is only a proxy for it; this is the byte the dispatcher actually reads.
    ///
    ///     <c>need_sync</c> is g_isNeedSync. While it is 1, Sg_MainCalcRate overwrites sg_rate from
    ///     the syncdata table and the vertical blank correction this module makes is discarded.
    ///     Nothing has ever checked whether it is set in this build.
    ///
    ///     <c>blur_wait</c> is force_wait_blur_frame_count, the 90-frame hold before a battle starts.
    ///     It counts presented frames, so at 60 Hz the battle opens in half the time - the part of
    ///     "the intro camera comes in too fast" that no camera hook touches.
    /// </summary>
    private string engine_state_counts()
    {
        uint enable = FhUtil.get_at<uint>(EngineAddresses.EnableGameTextureAnimation);
        uint sync = FhUtil.get_at<uint>(EngineAddresses.IsNeedSync);
        uint blur = FhUtil.get_at<uint>(EngineAddresses.ForceWaitBlurFrameCount);

        int old_format = 0, new_format = 0, other = 0;

        for (int slot = 0; slot < TexAnimSlotCount; slot++)
        {
            byte format = FhUtil.get_at<byte>(TexAnimWorkBase + slot * TexAnimSlotStride + TexAnimFormatOffset);

            if      (format == 0) old_format++;
            else if (format == 1) new_format++;
            else                  other++;
        }

        // The timer of the first slot on the new format, because that is the value the retiming
        // argument was about and a byte the engine rewrites per motion speed zero crossing.
        int timer = -1;
        for (int slot = 0; slot < TexAnimSlotCount && timer < 0; slot++)
        {
            if (FhUtil.get_at<byte>(TexAnimWorkBase + slot * TexAnimSlotStride + TexAnimFormatOffset) == 1)
                timer = FhUtil.get_at<sbyte>(TexAnimWorkBase + slot * TexAnimSlotStride + TexAnimTimerOffset);
        }

        return $"texanim_enable={enable} texanim_fmt=old:{old_format}/new:{new_format}/other:{other} " +
               $"texanim_timer={timer} need_sync={sync} blur_wait={blur}";
    }
}
