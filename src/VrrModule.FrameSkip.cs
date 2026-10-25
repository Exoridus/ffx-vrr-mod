namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Reporting for the engine's own multi-pass counter, which is the one clock in this module's
///     reach that nothing else observes.
///
///     Every correction here assumes one Sg_MainLoop pass per presented frame, and that assumption is
///     the engine's default rather than its rule: updateFFX runs its whole body in a
///     <c>do { ... } while (gElapsedFrameCount_frameSkip != 0)</c>, so a frame that is behind runs
///     the simulation several times before it presents. During those extra passes the engine sets
///     gParticleDoNotRender and gFrameSkipDoNotRender, so they simulate without drawing.
///
///     The counter has to be read at the one instant it is meaningful. FUN_00821f90 computes it and
///     Sg_MainLoop consumes one pass of it immediately afterwards, and the loop only exits when it has
///     reached zero - so anything sampling from the present path reads zero every time, the way the
///     particle object counter does. It is sampled inside the vertical blank hook instead, right
///     after the original has run, which is between the write and the decrement.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private int  _frameskip_max;
    private long _frameskip_multi;
    private long _frameskip_passes;

    private void sample_frame_skip()
    {
        int count = FhUtil.get_at<int>(EngineAddresses.ElapsedFrameCountFrameSkip);

        _frameskip_passes++;
        if (count > 1) _frameskip_multi++;
        if (count > _frameskip_max) _frameskip_max = count;
    }

    /// <summary>
    ///     The peak and the number of passes that asked for catch-up, both per telemetry window
    ///     rather than cumulative. The instant value is almost always 1 and says nothing; what
    ///     matters is whether the engine is catching up at all and how deep it went, and a cumulative
    ///     maximum would report one boot-load stall for the rest of the run.
    ///
    ///     The pass count is there to separate "never above one" from "never sampled", which the
    ///     other two numbers cannot distinguish.
    /// </summary>
    private string frame_skip_counts()
    {
        string counts = $"frameskip={_frameskip_multi}/{_frameskip_passes} max{_frameskip_max}";

        _frameskip_max = 0;
        _frameskip_multi = 0;
        _frameskip_passes = 0;
        return counts;
    }
}
