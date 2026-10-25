using System.Numerics;
using Fahrenheit.Mods.Vrr;
using NUnit.Framework;

namespace Fahrenheit.Mods.Vrr.Tests;

public class RenderInterpolatorTests
{
    static Transform At(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity);

    [Test]
    public void first_tick_has_no_previous_state_so_current_is_returned_directly()
    {
        var interp = new RenderInterpolator();
        interp.SetTickPeriod(1.0 / 60.0);

        interp.Snapshot("camera", At(5f));

        bool blended = interp.TryGetInterpolated("camera", timeSinceLastTickSeconds: 0.005, out var result);

        Assert.That(blended, Is.False);
        Assert.That(result.Position.X, Is.EqualTo(5f));
    }

    [Test]
    public void halfway_through_a_tick_period_blends_exactly_halfway()
    {
        var interp = new RenderInterpolator();
        interp.SetTickPeriod(1.0 / 60.0); // ~0.01667s

        interp.Snapshot("camera", At(0f));
        interp.Snapshot("camera", At(10f)); // now has both a previous (0) and current (10) state

        bool blended = interp.TryGetInterpolated("camera", timeSinceLastTickSeconds: (1.0 / 60.0) / 2.0, out var result);

        Assert.That(blended, Is.True);
        Assert.That(result.Position.X, Is.EqualTo(5f).Within(0.001f));
    }

    [Test]
    public void time_since_tick_beyond_one_full_period_clamps_to_the_current_tick_value()
    {
        // Present being called late (slow display) must not overshoot past the current tick's state.
        var interp = new RenderInterpolator();
        interp.SetTickPeriod(1.0 / 60.0);

        interp.Snapshot("camera", At(0f));
        interp.Snapshot("camera", At(10f));

        bool blended = interp.TryGetInterpolated("camera", timeSinceLastTickSeconds: 5.0, out var result);

        Assert.That(blended, Is.True);
        Assert.That(result.Position.X, Is.EqualTo(10f).Within(0.001f));
    }

    [Test]
    public void a_gap_longer_than_one_tick_period_between_snapshots_is_treated_as_a_scene_change()
    {
        // Spec 8: teleport / scene load must not glide. A caller-observed gap this large means the
        // two snapshots are not the same continuous motion, so no blend should be offered.
        var interp = new RenderInterpolator();
        interp.SetTickPeriod(1.0 / 60.0);

        interp.Snapshot("camera", At(0f));
        System.Threading.Thread.Sleep(50); // exceeds one 60Hz tick period (~16.7ms) by design
        interp.Snapshot("camera", At(500f)); // a teleport, not a continuous 60Hz step

        bool blended = interp.TryGetInterpolated("camera", timeSinceLastTickSeconds: 0.001, out var result);

        Assert.That(blended, Is.False);
        Assert.That(result.Position.X, Is.EqualTo(500f));
    }

    [Test]
    public void unknown_key_returns_false_without_throwing()
    {
        var interp = new RenderInterpolator();
        interp.SetTickPeriod(1.0 / 60.0);

        bool blended = interp.TryGetInterpolated("never-snapshotted", 0.0, out var result);

        Assert.That(blended, Is.False);
    }
}
