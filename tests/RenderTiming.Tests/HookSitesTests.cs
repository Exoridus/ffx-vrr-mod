namespace Fahrenheit.Mods.Vrr;

using Xunit;

public sealed class HookSitesTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Undeclared_site_never_calls_installer(int value)
    {
        int calls = 0;
        Assert.False(HookSites.IsDeclared((HookSiteId)value));
        Assert.False(HookSites.Install((HookSiteId)value, () => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declared_site_forwards_result_and_calls_once(bool result)
    {
        foreach (HookSiteId site in Enum.GetValues<HookSiteId>().Where(site => site != HookSiteId.Unknown))
        {
            Assert.True(HookSites.IsDeclared(site));
            int calls = 0;
            Assert.Equal(result, HookSites.Install(site, () => { calls++; return result; }));
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public void Declared_site_preserves_installer_exception()
    {
        var expected = new InvalidOperationException("native install failed");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            HookSites.Install(HookSiteId.DxgiPresent, () => throw expected)));
    }
}
