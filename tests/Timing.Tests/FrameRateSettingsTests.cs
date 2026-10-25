using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class FrameRateSettingsTests
{
    [Theory]
    [InlineData(1, 0, 30)]
    [InlineData(29, 30, 0)]
    [InlineData(143, 144, 143)]
    public void Render_cap_arrows_can_enter_and_leave_unlimited(int requested, int previous, int expected)
        => Assert.Equal(expected, VrrConfig.NormalizeMaxFramerate(requested, previous));
    [Theory]
    [InlineData(30, 60, 30)]
    [InlineData(60, 30, 60)]
    [InlineData(120, 60, 120)]
    [InlineData(90, 60, 120)]
    [InlineData(90, 120, 60)]
    [InlineData(0, 60, 30)]
    [InlineData(144, 60, 120)]
    public void Simulation_selector_cycles_only_through_supported_rates(int requested, int previous, int expected)
        => Assert.Equal(expected, VrrConfig.NormalizeSimulationFramerate(requested, previous));
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(144, 144)]
    public void Render_cap_is_unlimited_or_at_least_thirty(int rate, int expected)
        => Assert.Equal(expected, VrrConfig.NormalizeMaxFramerate(rate));
}
