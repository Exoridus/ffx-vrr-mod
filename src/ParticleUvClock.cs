namespace Fahrenheit.Mods.Vrr;

internal static class ParticleUvClock
{
    internal static void Prepare(Span<float> channels, float elapsed, bool nativePaused)
    {
        if (nativePaused) return;
        Prepare(ref channels[0], ref channels[1], channels[2], elapsed);
        Prepare(ref channels[3], ref channels[4], channels[5], elapsed);
    }

    internal static void Prepare(ref float position, ref float velocity, float acceleration, float elapsed)
    {
        if (elapsed == 1f) return;
        float offset = elapsed - 1f;
        // The native draw integrates velocity before position and applies authored keyframes afterward.
        position += velocity * offset + acceleration * elapsed * offset * 0.5f;
        velocity += acceleration * offset;
    }
}
