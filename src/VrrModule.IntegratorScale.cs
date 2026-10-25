namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Halves what the eight generic integrators add per call, instead of skipping every second
///     call. Same wall-clock rate as the hold, but every frame carries a valid intermediate value.
///
///     <para>
///     The hold gets the average right and the image wrong. A held kernel leaves its object
///     untouched for a whole frame, so particle motion advances on 30 of 60 frames while the camera
///     advances on all of them - visible frame by frame as the particles and the camera moving in
///     alternation. Worse at the edges of a life: the death test runs off the object age, which is
///     scaled exactly and therefore tested every frame, while colour and scale are only updated
///     every second frame. An object can die with the alpha of the previous update still in place,
///     which is a particle vanishing instead of fading out, and it can be drawn for one frame
///     before the kernel that seeds its start value has run, which is one appearing at full
///     opacity instead of fading in.
///     </para>
///
///     <para>
///     These eight cannot be retimed by patching authored data: each one integrates a constant per
///     call - value += rate for the Move kernels, rate += acceleration for the Accele ones, from a
///     slot pair the program data names - and the constants are shared authored values. What can be
///     done is to let the kernel run and then keep half of what it did. That needs no knowledge of
///     which slot means what, it is exact for the float kernels, and it composes correctly across
///     the double integrators: the rate an Accele kernel produces is the same field a Move kernel
///     reads, so halving both makes the position error scale with the square of the timestep the
///     way it should, rather than with its square root.
///     </para>
///
///     <para>
///     The keyframe branch is untouched by construction. On a keyframe tick a kernel adds the
///     authored delta to the <em>rate</em> slot, not to the integrated slot, and only the latter is
///     corrected here - so a one-shot authored value keeps its full size while the per-call
///     integration is halved.
///     </para>
/// </summary>
public unsafe sealed partial class VrrModule
{
    private enum IntegratorWidth
    {
        /// <summary>Four floats. Exactly halvable.</summary>
        Float32,
        /// <summary>Four 32-bit integers.</summary>
        Int32,
        /// <summary>Four 16-bit integers.</summary>
        Int16
    }

    /// <summary>
    ///     The eight kernels whose whole body is an integration, with the width of the value they
    ///     integrate. All of them address four components at <c>obj + slot + 0xa0</c>, where the
    ///     slot pair comes from the program data at <c>prog+0xc</c>; the first slot is the value
    ///     they integrate and the second is the rate they read.
    /// </summary>
    private static readonly (string Name, IntegratorWidth Width)[] Integrators =
    [
        ("pppMove",      IntegratorWidth.Float32), ("pppAccele",    IntegratorWidth.Float32),
        ("pppSclMove",   IntegratorWidth.Float32), ("pppSclAccele", IntegratorWidth.Float32),
        ("pppAngMove",   IntegratorWidth.Int32),   ("pppAngAccele", IntegratorWidth.Int32),
        ("pppColMove",   IntegratorWidth.Int16),   ("pppColAccele", IntegratorWidth.Int16)
    ];

    /// <summary>Where the four components of either slot start, relative to the object and slot.</summary>
    private const int IntegratorValueOffset = 0xA0;

    private long _int_scaled;
    private long _int_carried;

    private string integrator_counts() => $"int_scaled={_int_scaled} int_carried={_int_carried}";

    private static IntegratorWidth? integrator_width(string name)
    {
        foreach ((string n, IntegratorWidth w) in Integrators)
            if (string.Equals(n, name, StringComparison.Ordinal)) return w;

        return null;
    }

    /// <summary>
    ///     Runs the kernel and keeps <c>1/Scale</c> of what it added to the integrated slot.
    ///
    ///     <para>
    ///     Read the four components, call the original, read them again, write back the start value
    ///     plus the scaled difference. Nothing about which slot holds what needs to be known, and a
    ///     kernel that wrote nothing - the user stop flag is set, or the object is not on this
    ///     program step - produces a zero difference and is left exactly as it was.
    ///     </para>
    ///
    ///     <para>
    ///     The integer widths cannot carry a fraction, and rounding each call the same way would
    ///     either freeze a rate of one unit per call at zero or double it. The remainder is
    ///     therefore alternated by frame parity: half the calls round down and half round up, so two
    ///     consecutive frames add exactly what one vanilla frame did. Colour and angle are integers
    ///     in the data as well, so this is the same granularity the engine itself works in - what it
    ///     buys is that the value changes on every frame instead of standing still on half of them.
    ///     </para>
    /// </summary>
    private void run_integrator_scaled(nint rva, d_ke_update self, int obj, int data, int prog, IntegratorWidth width)
    {
        // The slot pair is program data, so it is resolved the same way the kernel resolves it.
        // A program without it is not something to guess at: run the kernel untouched.
        int* slots = *(int**)(prog + 0xc);
        var orig = new FhCallHandle<d_ke_update>(new FhCallLocation(rva, 0)).chain_from(self).fnptr;

        if (slots == null || orig is null)
        {
            orig?.Invoke(obj, data, prog);
            return;
        }

        nint value = obj + *slots + IntegratorValueOffset;

        // The same per-frame flag the hold gates on, reused as the parity of the remainder: it is
        // true on exactly the frames a 30 Hz sequence would have advanced, so at Scale 2 the two
        // halves of a pair land one on each side and their sum is what one vanilla frame added.
        bool round_up = advance_this_frame();

        switch (width)
        {
            case IntegratorWidth.Float32:
            {
                float* v = (float*)value;
                float b0 = v[0], b1 = v[1], b2 = v[2], b3 = v[3];

                orig(obj, data, prog);

                v[0] = b0 + (v[0] - b0) / Scale;
                v[1] = b1 + (v[1] - b1) / Scale;
                v[2] = b2 + (v[2] - b2) / Scale;
                v[3] = b3 + (v[3] - b3) / Scale;
                break;
            }

            case IntegratorWidth.Int32:
            {
                int* v = (int*)value;
                int b0 = v[0], b1 = v[1], b2 = v[2], b3 = v[3];

                orig(obj, data, prog);

                v[0] = b0 + scale_delta(v[0] - b0, round_up);
                v[1] = b1 + scale_delta(v[1] - b1, round_up);
                v[2] = b2 + scale_delta(v[2] - b2, round_up);
                v[3] = b3 + scale_delta(v[3] - b3, round_up);
                break;
            }

            default:
            {
                short* v = (short*)value;
                short b0 = v[0], b1 = v[1], b2 = v[2], b3 = v[3];

                orig(obj, data, prog);

                v[0] = (short)(b0 + scale_delta(v[0] - b0, round_up));
                v[1] = (short)(b1 + scale_delta(v[1] - b1, round_up));
                v[2] = (short)(b2 + scale_delta(v[2] - b2, round_up));
                v[3] = (short)(b3 + scale_delta(v[3] - b3, round_up));
                break;
            }
        }

        _int_scaled++;
    }

    /// <summary>
    ///     One call's share of an integer delta. The fraction the width cannot hold is alternated
    ///     rather than dropped, so it neither accumulates into drift nor rounds a one-unit rate to
    ///     nothing.
    /// </summary>
    private int scale_delta(int delta, bool round_up)
    {
        if (delta == 0) return 0;

        float exact = delta / Scale;
        int truncated = (int)exact;

        if (truncated == exact) return truncated;

        _int_carried++;
        int away = delta > 0 ? truncated + 1 : truncated - 1;
        return round_up ? away : truncated;
    }
}
