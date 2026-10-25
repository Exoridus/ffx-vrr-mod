using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class RenderActivationTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.EternalAction = null;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
        FhUtil.Writes.Clear();
    }

    [Fact]
    public void Scheduler_installation_does_not_arm_independent_presentation()
    {
        var module = new VrrModule();
        Assert.True(module.InitScheduler());
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        long serial = RenderPassContext.SimulationSerial;
        module.RunNativePass();
        Assert.Equal(serial + 1, RenderPassContext.SimulationSerial);
        module.ArmScheduler();
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
    }

    [Fact]
    public void Producer_change_after_image_admission_returns_to_native_before_the_pass()
    {
        var module = new VrrModule();
        module.PreparePass();
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
        module.ProducerQualified = false;
        long serial = RenderPassContext.SimulationSerial;
        int calls = 0;
        NativeClock.Advance = () => calls++;
        try
        {
            module.RunNativePass();
            Assert.Equal(1, calls);
            Assert.Equal(serial + 1, RenderPassContext.SimulationSerial);
            Assert.False(RenderPassContext.IsActive);
            Assert.False(RenderPassContext.IsPresentation);
            Assert.Equal(60, module.ReportedRate);
            Assert.True(module.CameraResets > 0);
            Assert.True(module.ActorResets > 0);
        }
        finally { NativeClock.Advance = null; }
    }

    [Theory]
    [InlineData("render main loop")]
    [InlineData("render exec count")]
    [InlineData("render calculation rate")]
    [InlineData("render particle emission")]
    public void Incomplete_hook_group_cannot_be_armed(string rejected)
    {
        var module = new VrrModule { RejectedHook = rejected };
        Assert.False(module.InitScheduler());
        module.ArmScheduler();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
        long serial = RenderPassContext.SimulationSerial;
        module.RunNativePass();
        Assert.Equal(serial + 1, RenderPassContext.SimulationSerial);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worker_callbacks_keep_native_exec_rate_and_emission(bool legacyHold)
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, true);
        FhUtil.Writes.Clear();
        NativeClock.CalculateCalls = NativeClock.EmitCalls = NativeClock.EffectCalls = NativeClock.EternalCalls = 0;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Assert.Equal(0xfedcba98u, module.ReadExec());
                module.CalculateRate();
                module.Emit();
                if (legacyHold) module.RunEffectsWithLegacyHold(false);
                else module.RunEffectsWithoutLegacyHold(true);
            }
            catch (Exception error) { failure = error; }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.Empty(FhUtil.Writes);
        Assert.Equal(1, NativeClock.CalculateCalls);
        Assert.Equal(1, NativeClock.EmitCalls);
        Assert.Equal(1, NativeClock.EffectCalls);
        Assert.Equal(1, NativeClock.EternalCalls);
    }

    [Fact]
    public void Legacy_effect_holds_do_not_escape_the_active_schedulers_loop()
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, false, true);
        NativeClock.EffectCalls = NativeClock.EternalCalls = 0;
        module.RunEffectsWithLegacyHold(false);
        Assert.Equal(1, NativeClock.EffectCalls);
        Assert.Equal(1, NativeClock.EternalCalls);
    }

    [Theory]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, true, true, true)]
    public void Clock_and_effect_adapters_only_hold_the_owned_presentation_pass(
        bool armed, bool frame, bool inside, bool presentation, bool held)
    {
        var module = new VrrModule();
        module.SetHookScope(armed, frame, inside, presentation);
        FhUtil.Writes.Clear();
        NativeClock.CalculateCalls = NativeClock.EmitCalls = NativeClock.EffectCalls = 0;
        Assert.Equal(held ? 0u : armed && frame && inside ? 1u : 0xfedcba98u, module.ReadExec());
        module.CalculateRate();
        Assert.Equal(1, NativeClock.CalculateCalls);
        Assert.Equal(held ? 2 : 0, FhUtil.Writes.Count);
        module.Emit();
        Assert.Equal(held ? 0 : 1, NativeClock.EmitCalls);
        module.RunEffectsWithoutLegacyHold(true);
        Assert.Equal(held ? 0 : 1, NativeClock.EffectCalls);
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, false)]
    [InlineData((byte)0, true)]
    [InlineData((byte)1, true)]
    public void Held_simulation_walk_draws_once_and_restores_prior_pause(byte previous, bool throws)
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, false);
        module.SetBattlePause(previous);
        int draws = 0;
        NativeClock.EternalAction = () =>
        {
            Assert.Equal(1, module.BattleHold);
            draws++;
            if (throws) throw new InvalidOperationException("native walk probe");
        };
        if (throws) Assert.Throws<InvalidOperationException>(() => module.RunEternal(true, true, false));
        else module.RunEternal(true, true, false);
        Assert.Equal(1, draws);
        Assert.Equal(previous, module.BattleHold);
    }

    [Fact]
    public void Owned_presentation_keeps_the_mixed_effect_draw()
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, true);
        module.SetBattlePause(1);
        int draws = 0;
        NativeClock.EternalAction = () => { Assert.Equal(1, module.BattleHold); draws++; };
        module.RunEternal(false, false, false);
        Assert.Equal(1, draws);
        Assert.Equal(1, module.BattleHold);
    }
}

internal static partial class NativeClock
{
    internal static int CalculateCalls, EmitCalls;
    internal static Action? EternalAction;
    public static uint Exec() => 0xfedcba98u;
    public static void Calculate() => CalculateCalls++;
    public static void Emit(nint manager) => EmitCalls++;
}
