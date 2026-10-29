using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class BattleAdapterInitializationTests : IDisposable
{
    public BattleAdapterInitializationTests() => NativeClock.Hooks.Clear();

    public void Dispose()
    {
        NativeClock.Hooks.Clear();
        RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Independent_scheduler_keeps_unqualified_battle_adapter_dormant(bool fieldPauseAccepted)
    {
        var module = new VrrModule { ProducerQualified = fieldPauseAccepted };
        Assert.True(module.Requested);
        Assert.True(module.EnableAdapter().InitScheduler());
        Assert.Equal(1, module.FieldPauseInstalls);
        Assert.Equal(1, module.CameraInitCalls);
        Assert.Equal(1, module.ActorInitCalls);
        Assert.Equal(new[] { "d_diag_loop", "d_render_exec", "d_render_void", "d_render_emit" }, NativeClock.Hooks);
        Assert.False(module.BattlePauseInstalled);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        module.ArmScheduler();
        module.PrimeMappedClockForAdmission();
        module.BeginFrame();
        Assert.Equal(fieldPauseAccepted, RenderPassContext.IsActive);
        Assert.Equal(0, module.BattlePauseInitCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_startup_keeps_all_independent_adapters_dormant(bool requested)
    {
        var module = new VrrModule();
        module.RequestIndependent(requested);
        Assert.True(module.Preflight());
        Assert.Equal(requested, module.Requested);
        Assert.False(module.Effective);
        Assert.True(module.InitScheduler());
        Assert.Empty(NativeClock.Hooks);
        Assert.Equal(0, module.FieldPauseInstalls);
        Assert.Equal(0, module.CameraInitCalls);
        Assert.Equal(0, module.ActorInitCalls);
        Assert.Equal(0, module.BattlePauseInitCalls);
        Assert.False(module.BattlePauseInstalled);
        module.ArmScheduler();
        module.PrimeMappedClockForAdmission();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
    }
}
