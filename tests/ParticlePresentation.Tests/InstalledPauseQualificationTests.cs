using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public unsafe sealed class InstalledPauseQualificationTests : IDisposable
{
    public InstalledPauseQualificationTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    private static VrrModule Installed()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.EnterParticleLoop();
        module.EnableBattlePause();
        Assert.True(module.QualifyPilot());
        return module;
    }

    [Theory]
    [InlineData("battle", "battle_active")]
    [InlineData("actor", "battle_effect_state")]
    [InlineData("overlay4", "effect_overlay_state_4_5")]
    [InlineData("overlay5", "effect_overlay_state_4_5")]
    [InlineData("manager", "live_particles_not_idle")]
    [InlineData("native_list", "native_owner_list")]
    [InlineData("overlay_list", "overlay_owner_list")]
    public void Installation_does_not_qualify_a_new_or_unreadable_producer(string producer, string reason)
    {
        var module = Installed();
        switch (producer)
        {
            case "battle": FhUtil.set_at<byte>(EngineAddresses.BattleState, 1); break;
            case "actor": FhUtil.set_at<nint>(EngineAddresses.PlayerChrs, 1234); break;
            case "overlay4": FhUtil.set_at<byte>(EngineAddresses.GlobalEffectOverlayState, 4); break;
            case "overlay5": FhUtil.set_at<byte>(EngineAddresses.GlobalEffectOverlayState, 5); break;
            case "manager": module.LiveManagers = true; break;
            case "native_list": *(uint*)FhUtil.ptr_at<byte>(0xea40c0) = 1234; break;
            case "overlay_list": *(uint*)(FhUtil.ptr_at<byte>(0xea40c0) + 4) = 1234; break;
        }
        Assert.False(module.QualifyPilot());
        Assert.Equal(reason, module.QualificationRefusal);
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal(0, PilotNative.Runs);
    }

    [Fact]
    public void Third_overlay_list_owner_still_requires_a_proved_program()
    {
        using var memory = new PilotMemory();
        var module = Installed();
        *(uint*)(FhUtil.ptr_at<byte>(0xea40c0) + 4) = (uint)memory.Address;
        *(uint*)(memory.Address + 0x10) = (uint)(memory.Address + 0x8000);
        *(uint*)(memory.Address + 0x14) = (uint)(memory.Address + 0x8100);
        *(uint*)(memory.Address + 0x18) = (uint)(memory.Address + 0x8200);
        *(uint*)(memory.Address + 0x20) = (uint)(memory.Address + 0x1000);
        *(short*)(memory.Address + 0x8000) = -1;
        *(short*)(memory.Address + 0x8100) = -1;
        *(short*)(memory.Address + 0x8200) = 0;
        *(short*)(memory.Address + 0x8202) = -1;
        Assert.False(module.QualifyPilot());
        Assert.Equal("overlay_not_gameover_pilot", module.QualificationRefusal);
    }

    [Fact]
    public void Existing_field_pause_remains_eligible_but_live_battle_particles_do_not()
    {
        var module = Installed();
        module.EnableFieldPause();
        module.LiveManagers = true;
        Assert.True(module.QualifyPilot());
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 1);
        *(uint*)(FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers) + 0x30) = 1234;
        Assert.False(module.QualifyPilot());
        Assert.Equal("live_particles_not_idle", module.QualificationRefusal);
    }

    [Theory]
    [InlineData((ushort)0, (byte)0, true)]
    [InlineData((ushort)2, (byte)0, false)]
    [InlineData((ushort)0, (byte)2, false)]
    public void Installed_worker_rechecks_family_and_mode_before_native_work(ushort family, byte mode, bool allowed)
    {
        var module = Installed();
        RenderPassContext.IsPresentation = true;
        module.RunWorker(family, mode);
        Assert.Equal(allowed ? 1 : 0, PilotNative.Workers);
    }

    [Fact]
    public void Worker_rechecks_a_producer_that_appears_after_the_scene_check()
    {
        var module = Installed();
        FhUtil.set_at<byte>(EngineAddresses.BattleState, 1);
        RenderPassContext.IsPresentation = true;
        module.RunWorker(0, 0);
        Assert.Equal(0, PilotNative.Workers);
        Assert.False(module.QualifyPilot());
    }

    [Fact]
    public void Installed_pause_does_not_authorize_an_unowned_manager()
    {
        var module = Installed();
        RenderPassContext.IsPresentation = true;
        Assert.Equal(0u, module.RunManager(1234));
        Assert.Equal(0, PilotNative.Runs);
    }

    [Fact]
    public void Native_worker_and_manager_keep_their_calls_with_the_pause_installed()
    {
        var module = Installed();
        module.RunWorker(0, 0);
        Assert.Equal(1, PilotNative.Workers);
        Assert.Equal(77u, module.RunManager(1234));
        Assert.Equal(1, PilotNative.Runs);
    }
}
