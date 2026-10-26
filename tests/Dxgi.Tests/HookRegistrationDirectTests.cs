namespace Fahrenheit.Mods.Vrr;

using Xunit;

[Collection("Texture hooks")]
public sealed class HookRegistrationDirectTests
{
    [Fact]
    public void Actual_DXGI_leaf_requires_the_selected_owner_plan()
    {
        TextureDrawBridgeTests.Callbacks.Clear();
        var missing = new VrrModule();
        Assert.False(missing.RegisterDxgiForTest(0x1234));
        var disabled = new VrrModule();
        disabled.SelectDxgiForTest(false);
        Assert.False(disabled.RegisterDxgiForTest(0x1234));
        Assert.Empty(TextureDrawBridgeTests.Callbacks);
    }

    [Fact]
    public void Actual_DXGI_leaf_queues_into_the_same_owner_and_refuses_after_disposal()
    {
        TextureDrawBridgeTests.Callbacks.Clear();
        var owner = new VrrModule();
        owner.SelectDxgiForTest(true);
        Assert.True(owner.RegisterDxgiForTest(0x1234));
        Assert.Single(TextureDrawBridgeTests.Callbacks);
        owner.DisposeDxgiPlanForTest();
        Assert.False(owner.RegisterDxgiForTest(0x5678));
        Assert.Single(TextureDrawBridgeTests.Callbacks);
        TextureDrawBridgeTests.Callbacks.Clear();
    }
}

public unsafe sealed partial class VrrModule
{
    internal void SelectDxgiForTest(bool enabled)
    {
        if (!HookRegistrationPlan.TryCreate(
            [new(HookSiteId.DxgiPresent, enabled, Deferred: true)], [], out _hookRegistration, out _))
            throw new InvalidOperationException("Fixture selection failed");
    }
    internal bool RegisterDxgiForTest(nint target) => new DxgiHooks(this, target).Install(0);
    internal void DisposeDxgiPlanForTest() => dispose_hook_registration();
}
