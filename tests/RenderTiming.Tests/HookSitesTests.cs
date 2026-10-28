namespace Fahrenheit.Mods.Vrr;

using Xunit;

public sealed class HookSitesTests
{
    [Fact]
    public void Startup_publisher_is_appended_after_existing_hook_ids()
    {
        Assert.Equal(147, (int)HookSiteId.PadSnapshotProducer);
        Assert.Equal(148, (int)HookSiteId.ModuleStartupCookieInitialization);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Undeclared_site_never_calls_installer(int value)
    {
        int calls = 0;
        Assert.False(HookSites.IsDeclared((HookSiteId)value));
        Assert.False(HookSites.Install((HookSiteId)value, null, 0, 0, () => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_site_forwards_result_and_calls_once(bool result)
    {
        HookSiteId site = HookSiteId.ModuleFFXApplicationAnimate;
        Assert.True(HookRegistrationPlan.TryCreate([new(site)], [], out var plan, out _));
        int calls = 0;
        Assert.Equal(result, HookSites.Install(site, plan, 0x100, 0, () => { calls++; return result; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Declared_site_preserves_installer_exception()
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.ModuleFFXApplicationAnimate)], [], out var plan, out _));
        var expected = new InvalidOperationException("native install failed");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            HookSites.Install(HookSiteId.ModuleFFXApplicationAnimate, plan, 0x100, 0, () => throw expected)));
    }
}
