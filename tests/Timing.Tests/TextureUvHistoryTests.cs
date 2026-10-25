using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class TextureUvHistoryTests
{
    private static TextureUvTarget Target(int set = 1) => new(0, 0, set, 10, 20, 30, 100);
    private static List<float> Render(TextureUvHistory history, long image, long simulation, float alpha, bool valid = true)
    {
        List<float> result = [];
        history.Present(image, simulation, alpha, _ => valid, (_, _, v) => result.Add(v));
        return result;
    }

    [Theory]
    [InlineData(0.2f, 0.4f, 0.2f, 0.3f)]
    [InlineData(0.9f, 0.1f, 0.2f, 1f)]
    [InlineData(0.1f, 0.9f, -0.2f, 1f)]
    public void Samples_interpolate_in_signed_unwrapped_phase(float first, float second, float delta, float expected)
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, first, delta);
        history.Capture(Target(), 2, 0, second, delta);
        Assert.Equal(expected, Assert.Single(Render(history, 1, 2, 0.5f)), 5);
    }

    [Fact]
    public void Repeated_outputs_in_one_tick_replace_current_without_shifting_previous()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.1f, 0.1f);
        history.Capture(Target(), 2, 0, 0.2f, 0.1f);
        history.Capture(Target(), 2, 0, 0.3f, 0.1f);
        Assert.Equal(0.2f, Assert.Single(Render(history, 1, 2, 0.5f)), 5);
        Assert.Empty(Render(history, 1, 2, 0.8f));
        Assert.Equal(0.25f, Assert.Single(Render(history, 2, 2, 0.75f)), 5);
    }

    [Fact]
    public void Catchup_retains_latest_adjacent_simulations_before_first_image()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.1f, 0.1f);
        history.Capture(Target(), 2, 0, 0.2f, 0.1f);
        history.Capture(Target(), 3, 0, 0.3f, 0.1f);
        Assert.Equal(0.25f, Assert.Single(Render(history, 1, 3, 0.5f)), 5);
    }

    [Fact]
    public void Authored_sets_keep_distinct_targets_and_share_record_phase()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.8f, 0.3f);
        history.Capture(Target(2), 1, 0, 0.1f, 0.3f);
        history.Capture(Target(), 2, 0, 0.4f, 0.3f);
        history.Capture(Target(2), 2, 0, 0.7f, 0.3f);
        var values = Render(history, 1, 2, 0.5f);
        Assert.Equal(2, values.Count);
        Assert.Equal(0.1f, values[0], 5);
        Assert.Equal(0.4f, values[1], 5);
    }

    [Fact]
    public void Missing_tick_or_invalid_identity_never_interpolates_stale_motion()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.1f, 0.1f);
        history.Capture(Target(), 2, 0, 0.2f, 0.1f);
        Assert.Equal(0.2f, Assert.Single(Render(history, 1, 3, 0.5f)));
        Assert.Empty(Render(history, 2, 3, 0.5f, false));
    }

    [Fact]
    public void Invalid_target_is_pruned_and_same_identity_reappearance_starts_fresh()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.2f, 0.2f);
        Assert.Empty(Render(history, 1, 1, 0.5f, false));
        history.Capture(Target(), 2, 0, 0.8f, 0.2f);
        Assert.Equal(0.8f, Assert.Single(Render(history, 2, 2, 0.5f)));
    }

    [Fact]
    public void Unsupported_native_wrap_output_is_left_to_native_instead_of_replaying_old_values()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.2f, 0.2f);
        history.Capture(Target(), 2, 0, 1.8f, 2f);
        Assert.Empty(Render(history, 1, 2, 0.5f));
    }

    [Fact]
    public void Lifecycle_invalidation_prevents_same_address_reuse_history()
    {
        var history = new TextureUvHistory();
        history.Capture(Target(), 1, 0, 0.1f, 0.1f);
        history.Capture(Target(), 2, 0, 0.2f, 0.1f);
        history.Invalidate(0);
        history.Capture(Target(), 3, 0, 0.8f, 0.1f);
        Assert.Equal(0.8f, Assert.Single(Render(history, 1, 3, 0.5f)));
        history.Reset();
        Assert.Empty(Render(history, 2, 3, 0.5f));
    }
}
