using ParryParticipant = Fahrenheit.Mods.Parry.ParryModule;
using FocusPolicy = Fahrenheit.Mods.Qol.FocusInputModule;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class NativeInputHandoverTests : IDisposable
{
    public NativeInputHandoverTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        NativeSnapshotFixture.Reset();
        NativeSnapshotBridge.Available = true;
        NativeClock.SnapshotCalls = NativeClock.PadCalls = 0;
        NativeClock.Advance = NativeClock.PadAction = NativeClock.SnapshotAction = null;
        NativeClock.LoopAction = null;
        VrrModule.ClearDiagnosticLoop();
        FhUtil.MenuReservation = -1;
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.Dummy = 0;
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData("qol", true)]
    [InlineData("qol", false)]
    [InlineData("sync", true)]
    [InlineData("movie", true)]
    [InlineData("reservation", true)]
    [InlineData("menu", true)]
    [InlineData("pause", true)]
    [InlineData("overdrive", true)]
    [InlineData("dummy", true)]
    public void Expected_native_owner_before_clock_hands_back_without_losing_pending_preupdate(string owner, bool pending)
    {
        var module = new VrrModule().EnableAdapter();
        Assert.True(module.InitHistory());
        module.PreparePass();
        var parry = new ParryParticipant { Initialized = true };
        var qol = new FocusPolicy();
        Assert.True(module.CoopBind(parry));
        Assert.True(module.HandoverBindInput(qol));
        module.CoopFrame();
        if (pending) Assert.True(parry.PreUpdate(.025f));
        SelectOwner(module, qol, owner);
        if (!pending) Assert.False(parry.PreUpdate(.025f));
        int clocks = 0;
        NativeClock.Advance = () => { clocks++; module.CallPad(); };
        NativeClock.LoopAction = _ =>
        {
            module.SnapshotClock(.04f);
            Assert.False(RenderPassContext.IsActive);
            Assert.False(RenderPassContext.IsPresentation);
            Assert.False(module.HandoverHasLoan);
            module.CallSnapshot();
            module.ReadExec();
        };
        module.CallDiagnosticLoop(.02f);
        Assert.Null(module.CoopFailure);
        Assert.Equal(1, clocks);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.Equal(pending ? 1 : 0, parry.Calls);
        if (pending) Assert.Equal(.025f, parry.Delta);
        Assert.True(module.CoopReady());
        Assert.False(module.HandoverHasLoan);
        Assert.Equal(1f, module.CameraAlpha);
        Assert.True(module.CameraResets > 0);
        Assert.True(module.ActorResets > 0);
    }

    [Theory]
    [InlineData("producer")]
    [InlineData("image")]
    [InlineData("binding")]
    public void Native_owner_does_not_hide_a_simultaneous_producer_or_lifecycle_failure(string failure)
    {
        var module = new VrrModule().EnableAdapter();
        Assert.True(module.InitHistory());
        module.PreparePass();
        if (failure == "producer") module.ClearSnapshotBaseline();
        if (failure == "image") module.ImageReady = false;
        if (failure == "binding") module.RevokeSnapshotReadiness();
        module.NativeSync = true;
        module.RunNativePass();
        Assert.NotNull(module.CoopFailure);
    }

    [Fact]
    public void Native_owner_between_loops_of_the_same_image_forwards_the_next_clock_once()
    {
        var module = new VrrModule().EnableAdapter();
        Assert.True(module.InitHistory());
        module.PreparePass();
        int clocks = 0;
        NativeClock.Advance = () => clocks++;
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.CallSnapshot(); module.ReadExec(); };
        module.CallDiagnosticLoop(1f / 60);
        Assert.Null(module.CoopFailure);
        module.NativeSync = true;
        module.CallDiagnosticLoop(1f / 60);
        Assert.Null(module.CoopFailure);
        Assert.Equal(2, clocks);
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(RenderPassContext.IsActive);
    }

    [Fact]
    public void Late_native_owner_does_not_unwind_an_entered_presentation_on_snapshot_readiness_loss()
    {
        var module = new VrrModule().EnableAdapter();
        Assert.True(module.InitHistory());
        module.PreparePresentation();
        module.RunClock();
        Assert.True(RenderPassContext.IsPresentation);
        int cameraResets = module.CameraResets, actorResets = module.ActorResets;
        module.ClearSnapshotBaseline();
        module.NativeSync = true;
        module.RunClock();
        Assert.NotNull(module.CoopFailure);
        Assert.Equal(cameraResets, module.CameraResets);
        Assert.Equal(actorResets, module.ActorResets);
        Assert.True(RenderPassContext.IsPresentation);
    }
    private static void SelectOwner(VrrModule module, FocusPolicy qol, string owner)
    {
        switch (owner)
        {
            case "qol": qol.Neutralize = true; qol.Generation++; break;
            case "sync": module.NativeSync = true; break;
            case "movie": module.NativeMovie = true; break;
            case "reservation": FhUtil.MenuReservation = 3; break;
            case "menu": FhUtil.MainMenu = 1; break;
            case "pause": FhUtil.MenuPause = 1; break;
            case "overdrive": FhUtil.Overdrive = 1; break;
            case "dummy": FhUtil.Dummy = 1; break;
        }
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool HandoverBindInput(params object[] modules) => bind_pad_input_policy(modules);
    internal bool HandoverHasLoan => _padReplayLoan is not null;
}
