using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class FixedSimulationEffectDispatchTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.EffectAction = null;
        NativeClock.EternalAction = null;
        NativeClock.StaticBeforeAction = null;
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void OwnedFixed60DeliversBothNativeWaitAndPreparationStepsRegardlessOfImagePartition(int imageHz)
    {
        var module = new VrrModule();
        module.SetBattlePause(0);
        var clock = new FixedSimulationClock(60);
        int firstWait = 90, secondWait = 90, firstPreparation = 0, secondPreparation = 0;
        NativeClock.EffectAction = mode =>
        {
            Assert.Equal(0, mode);
            if (!module.VmClockHeldForTest) firstWait--;
            firstPreparation++;
        };
        NativeClock.EternalAction = () =>
        {
            if (RenderPassContext.IsPresentation) return;
            if (!module.VmClockHeldForTest) secondWait--;
            secondPreparation++;
        };
        int tick = 0;
        for (int image = 0; image <= imageHz; image++)
        {
            var plan = clock.Plan((double)image / imageHz);
            for (int step = 0; step < plan.Steps; step++)
            {
                module.SetHookScope(true, true, true, false);
                module.RunEffectsWithLegacyHold((++tick & 1) == 0);
            }
            if (plan.Steps == 0)
            {
                module.SetHookScope(true, true, true, true);
                module.RunEffectsWithLegacyHold(false);
            }
        }
        Assert.Equal(61, tick);
        Assert.Equal(61, firstPreparation);
        Assert.Equal(61, secondPreparation);
        Assert.Equal(60, firstWait);
        Assert.Equal(60, secondWait);
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(true, false, true, 0)]
    [InlineData(true, true, false, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(true, true, true, 1)]
    public void LegacyCorrectionRemainsOnlyInItsNativeOwnedContext(bool scheduler, bool frame, bool inside, int expected)
    {
        var module = new VrrModule();
        module.SetHookScope(scheduler, frame, inside, false);
        if (scheduler && frame && inside) module.SetBattlePause(0);
        NativeClock.EffectCalls = NativeClock.EternalCalls = 0;
        module.RunEffectsWithLegacyHold(false);
        Assert.Equal(expected, NativeClock.EffectCalls);
        Assert.Equal(expected, NativeClock.EternalCalls);
    }
    [Fact]
    public void ExtraImageDispatchesOnlyFirstStaticEntryAndSecondListWithoutDynamicAdvance()
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, true);
        module.SetBattlePause(0);
        module.StaticOnlyVmForTest();
        int firstPreparation = 0, secondPreparation = 0;
        NativeClock.EffectCalls = 0;
        NativeClock.StaticBeforeAction = () => { firstPreparation++; Assert.True(RenderPassContext.IsPresentation); };
        NativeClock.EternalAction = () => { secondPreparation++; Assert.True(RenderPassContext.IsPresentation); };
        module.RunEffectsWithLegacyHold(false);
        Assert.Equal(1, firstPreparation);
        Assert.Equal(1, secondPreparation);
        Assert.Equal(0, NativeClock.EffectCalls);
    }
    [Fact]
    public void ExtraImageEternalAfterRunsFreshPreparationWithStaticBackendOnly()
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, true);
        module.SetBattlePause(0);
        module.StaticOnlyVmForTest();
        int preparation = 0;
        NativeClock.EternalAction = () => { preparation++; Assert.True(RenderPassContext.IsPresentation); };
        module.RunEternal(false, false, false);
        Assert.Equal(1, preparation);
    }
    [Fact]
    public void AuthoredWaitAndProgramCounterAdvanceOnlyOnTheThirtyHzVmClock()
    {
        var module = new VrrModule();
        module.SetHookScope(true, true, true, false);
        module.SetBattlePause(0);
        int wait = 4, pc = 0, preparation = 0;
        NativeClock.EffectAction = _ =>
        {
            preparation++;
            Assert.False(RenderPassContext.IsPresentation);
            Assert.Equal(0, module.BattleHold);
            if (!module.VmClockHeldForTest)
            {
                if (wait > 0) wait--;
                else pc += 4;
            }
        };
        for (int tick = 1; tick <= 10; tick++)
        {
            module.RunEffectsWithLegacyHold((tick & 1) == 0);
            Assert.Equal(Math.Max(0, 4 - tick / 2), wait);
            Assert.Equal(tick == 10 ? 4 : 0, pc);
            Assert.Equal(tick, preparation);
        }
        Assert.False(module.VmClockHeldForTest);
    }
}

internal static partial class NativeClock
{
    internal static Action<int>? EffectAction;
    internal static Action? StaticBeforeAction;
    public static void StaticBefore() => StaticBeforeAction?.Invoke();
}

public unsafe sealed partial class VrrModule
{
    internal void StaticOnlyVmForTest() { _staticEffectVmClockInstalled = true; _battleEffectPauseInstalled = false; }
    internal bool VmClockHeldForTest => IsOwnedEffectVmClockHold;
}
