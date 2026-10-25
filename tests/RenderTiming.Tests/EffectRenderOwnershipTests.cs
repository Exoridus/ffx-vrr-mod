using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class EffectRenderOwnershipTests
{
    [Fact]
    public void Independent_rendering_does_not_require_legacy_effect_holds()
    {
        Assert.True(new VrrModule().InitializeWithoutLegacyEffectHold());
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    public void Presentation_freezes_both_effect_lists_without_legacy_hold(bool presentation, bool advance, int expected)
    {
        RenderPassContext.IsPresentation = presentation;
        NativeClock.EffectCalls = NativeClock.EternalCalls = 0;
        try
        {
            new VrrModule().RunEffectsWithoutLegacyHold(advance);
            Assert.Equal(expected, NativeClock.EffectCalls);
            Assert.Equal(expected, NativeClock.EternalCalls);
        }
        finally { RenderPassContext.IsPresentation = false; }
    }

    [Theory]
    [InlineData(false, false, false, 0)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, true, true, 2)]
    [InlineData(true, false, false, 2)]
    public void Legacy_configuration_keeps_its_hook_selection(bool vrr, bool hold, bool eternal, int expected)
    {
        NativeClock.Hooks.Clear();
        Assert.True(new VrrModule().InitializeEffects(vrr, hold, eternal));
        Assert.Equal(expected, NativeClock.Hooks.Count);
    }

    [Theory]
    [InlineData(false, true, false, false, 1)]
    [InlineData(true, true, false, false, 0)]
    [InlineData(false, true, true, false, 0)]
    [InlineData(false, false, false, false, 1)]
    [InlineData(false, true, false, true, 1)]
    public void Eternal_simulation_respects_its_own_hold_switch(bool presentation, bool hold, bool eternal, bool advance, int expected)
    {
        RenderPassContext.IsPresentation = presentation;
        NativeClock.EternalCalls = 0;
        try
        {
            new VrrModule().RunEternal(hold, eternal, advance);
            Assert.Equal(expected, NativeClock.EternalCalls);
        }
        finally { RenderPassContext.IsPresentation = false; }
    }

    [Fact]
    public void Independent_rendering_installs_both_effect_owners_without_legacy_hold()
    {
        NativeClock.Hooks.Clear();
        Assert.True(new VrrModule().InitializeEffectsWithoutLegacyHold());
        Assert.Contains("d_effect_process", NativeClock.Hooks);
        Assert.Contains("d_eternal_run", NativeClock.Hooks);
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool InitializeWithoutLegacyEffectHold()
    {
        _config = new SchedulerConfig { EffectHold = false };
        return init_render_scheduler();
    }

    internal bool InitializeEffectsWithoutLegacyHold()
    {
        _config = new SchedulerConfig { EffectHold = false };
        return init_effect_hooks();
    }

    internal void RunEffectsWithoutLegacyHold(bool advance)
    {
        _config = new SchedulerConfig { EffectHold = false };
        _advance_frame = advance;
        h_effect_process(0);
        h_eternal_run_after();
    }

    internal bool InitializeEffects(bool vrr, bool hold, bool eternal)
    {
        _config = new SchedulerConfig { VrrEnabled = vrr, EffectHold = hold, EternalEffectHold = eternal };
        return init_effect_hooks();
    }

    internal void RunEternal(bool hold, bool eternal, bool advance)
    {
        _config = new SchedulerConfig { EffectHold = hold, EternalEffectHold = eternal };
        _advance_frame = advance;
        h_eternal_run_after();
    }

    private void init_gameover_probe() { }
    private void diagnostic_gameover_effect(int mode, bool held, string phase) { }
    private bool advance_this_frame() => _advance_frame;
}

internal sealed partial record SchedulerConfig
{
    public bool EffectProbe => false;
    public bool EternalEffectHold { get; init; } = true;
    public bool EffectHoldInBattle => true;
}

internal static partial class NativeClock
{
    internal static readonly List<string> Hooks = [];
    internal static int EffectCalls, EternalCalls;
    public static void Effect(int mode) => EffectCalls++;
    public static void Eternal() => EternalCalls++;
}

internal static partial class EngineAddresses
{
    public const nint MsEffectProcess = 0, EternalEffectRunAfter = 0, GlobalEffectOverlayState = 0;
    public const nint PlayerChrs = 0xd334d4;
}
