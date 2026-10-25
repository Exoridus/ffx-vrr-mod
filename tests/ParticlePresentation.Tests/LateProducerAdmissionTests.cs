using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public sealed class LateProducerAdmissionTests : IDisposable
{
    public LateProducerAdmissionTests() => PilotNative.Reset();
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
    [InlineData(false, "unexpected_worker_scope")]
    [InlineData(true, "manager_scope_changed")]
    public void Late_failure_survives_history_reset_and_native_work(bool manager, string reason)
    {
        var module = Installed();
        RenderPassContext.IsPresentation = true;
        if (manager) Assert.Equal(0u, module.RunManager(1234));
        else module.RunWorker(2, 0);
        Assert.Equal(reason, module.PresentationFailure);
        Assert.True(RenderPassContext.IsPresentation);
        RenderPassContext.IsPresentation = false;
        RenderPassContext.BeginImage(1d / 60);
        RenderPassContext.ResetHistory();
        module.ResetPilot();
        module.EnterParticleLoop();
        module.RunWorker(0, 0);
        Assert.Equal(77u, module.RunManager(1234));
        Assert.False(module.QualifyPilot());
        Assert.Equal(reason, module.QualificationRefusal);
        Assert.Equal(1, PilotNative.Workers);
        Assert.Equal(1, PilotNative.Runs);
        Assert.Single(module.PresentationErrors);
        var replacement = Installed();
        Assert.Null(replacement.PresentationFailure);
    }

    [Fact]
    public void Ordinary_pre_admission_ineligibility_can_recover()
    {
        var module = Installed();
        FhUtil.set_at<byte>(EngineAddresses.BattleState, 1);
        Assert.False(module.QualifyPilot());
        FhUtil.set_at<byte>(EngineAddresses.BattleState, 0);
        module.ResetPilot();
        Assert.True(module.QualifyPilot());
        Assert.Null(module.PresentationFailure);
        Assert.Empty(module.PresentationErrors);
    }

    [Fact]
    public void Repeated_late_failures_do_not_replace_the_first_reason_or_repeat_the_log()
    {
        var module = Installed();
        RenderPassContext.IsPresentation = true;
        module.RunManager(1234);
        module.RunWorker(2, 0);
        module.RunManager(5678);
        Assert.Equal("manager_scope_changed", module.PresentationFailure);
        Assert.Single(module.PresentationErrors);
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal(0, PilotNative.Runs);
    }

    [Fact]
    public void Foreign_worker_refusal_is_visible_after_the_owner_resets_history()
    {
        var module = Installed();
        RenderPassContext.IsPresentation = true;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try { module.RunWorker(0, 0); }
            catch (Exception error) { failure = error; }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        RenderPassContext.IsPresentation = false;
        module.ResetPilot();
        Assert.False(module.QualifyPilot());
        Assert.Equal("worker_thread_changed", module.PresentationFailure);
        Assert.Single(module.PresentationErrors);
    }
}
