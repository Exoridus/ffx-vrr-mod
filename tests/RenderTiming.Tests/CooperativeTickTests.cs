using Fahrenheit.Mods.Vrr;
using Parry = Fahrenheit.Mods.Parry.ParryModule;
using Xunit;

[Collection("Render scheduler")]
public sealed class CooperativeTickTests
{
    [Fact]
    public void AbsenceIsValidButDuplicateAndRefusedBindingFail()
    {
        var module = new VrrModule();
        Assert.True(module.CoopBind());
        Assert.True(module.CoopReady());
        Assert.False(module.CoopBind(new Parry(), new Parry()));
        Assert.False(module.CoopBind(new Parry { BindAccepted = false }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BindsBeforeOrAfterParticipantInitialization(bool initializeFirst)
    {
        var module = new VrrModule();
        var parry = new Parry { Initialized = initializeFirst };
        Assert.True(module.CoopBind(parry));
        Assert.Equal(initializeFirst, module.CoopReady());
        parry.Initialized = true;
        Assert.True(module.CoopReady());
    }

    [Fact]
    public void QualifiedSimulationDispatchesOnceAfterSnapshot()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        Assert.Equal(0, parry.Calls);
        module.CoopClock(true, true);
        Assert.Equal(0, parry.Calls);
        module.CoopSnapshot(true, true);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(1f / 60, parry.Delta);
        module.CoopExit();
        Assert.Null(module.CoopFailure);
    }

    [Fact]
    public void PresentationConsumesNoTickAndClearsGrant()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, false);
        module.CoopExit();
        Assert.Equal(0, parry.Calls);
        Assert.Null(module.CoopFailure);
        module.CoopFrame();
        Assert.True(parry.PreUpdate(.025f));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IndependentDispatchRequiresTrackedSnapshotAndAppliedPayload(bool tracked, bool applied)
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        module.CoopSnapshot(tracked, applied);
        module.CoopExit();
        Assert.Equal(0, parry.Calls);
        Assert.NotNull(module.CoopFailure);
    }

    [Fact]
    public void NativeHandoverUsesDeferredDeltaAfterOrdinaryProducer()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopHandover();
        module.CoopClock(true, true);
        module.CoopSnapshot(false, false);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(.025f, parry.Delta);
        module.CoopExit();
    }

    [Fact]
    public void MissedClockOrSimulationSnapshotRefusesAtExit()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopExit();
        Assert.NotNull(module.CoopFailure);
        module = Setup(out parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        module.CoopExit();
        Assert.NotNull(module.CoopFailure);
    }

    [Fact]
    public void WrongCallerImageChangeAndNestedEntryRefuseWithoutDispatch()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        module.CoopSnapshot(true, true, false);
        Assert.Equal(0, parry.Calls);
        Assert.NotNull(module.CoopFailure);
        module = Setup(out parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopFrame();
        module.CoopEnter();
        Assert.NotNull(module.CoopFailure);
        module = Setup(out parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopNested();
        Assert.NotNull(module.CoopFailure);
    }

    [Fact]
    public void ParticipantFailureIsTerminalAndNeverRetried()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        var failure = new InvalidOperationException("participant");
        parry.DuringTick = () => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => module.CoopSnapshot(true, true)));
        Assert.Equal(1, parry.Calls);
        Assert.NotNull(module.CoopFailure);
        module.CoopSnapshot(true, true);
        Assert.Equal(1, parry.Calls);
        Assert.False(module.CoopReady());
    }

    [Fact]
    public void DuplicateSnapshotAndReentryRefuse()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        module.CoopSnapshot(true, true);
        module.CoopSnapshot(true, true);
        Assert.Equal(1, parry.Calls);
        Assert.NotNull(module.CoopFailure);
        module = Setup(out parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        parry.DuringTick = () => module.CoopSnapshot(true, true);
        module.CoopSnapshot(true, true);
        Assert.Equal(1, parry.Calls);
        Assert.NotNull(module.CoopFailure);
    }

    [Fact]
    public void OptionalTelemetryCannotFailAnAlreadyDispatchedTick()
    {
        var module = Setup(out var parry);
        Assert.True(parry.PreUpdate(.025f));
        module.CoopEnter();
        module.CoopClock(true, true);
        module.DiagnosticEventAction = kind =>
        {
            if (kind == "cooperative_simulation_tick") throw new InvalidOperationException("telemetry");
        };
        module.CoopSnapshot(true, true);
        Assert.Equal(1, parry.Calls);
        Assert.Null(module.CoopFailure);
        module.CoopExit();
        Assert.Null(module.CoopFailure);
    }

    [Fact]
    public void StockHostPreUpdateInsideOwnedRootRelinquishesBeforeClock()
    {
        var module = Setup(out var parry);
        module.CoopEnter();
        Assert.True(parry.PreUpdate(.025f));
        module.CoopClock(true, true);
        module.CoopSnapshot(true, true);
        module.CoopExit();
        Assert.Equal(1, parry.Calls);
        Assert.Null(module.CoopFailure);
    }

    [Fact]
    public void InsideRootRejectsLateAndRepeatedPreUpdate()
    {
        var module = Setup(out var parry);
        module.CoopEnter();
        module.CoopClock(true, true);
        Assert.False(parry.PreUpdate(.025f));
        Assert.NotNull(module.CoopFailure);
        module = Setup(out parry);
        module.CoopEnter();
        Assert.True(parry.PreUpdate(.025f));
        Assert.False(parry.PreUpdate(.025f));
        Assert.NotNull(module.CoopFailure);
    }

    private static VrrModule Setup(out Parry parry)
    {
        NativeSnapshotFixture.Reset();
        var module = new VrrModule();
        parry = new Parry { Initialized = true };
        Assert.True(module.CoopBind(parry));
        module.CoopFrame();
        return module;
    }
}

namespace Fahrenheit.Mods.Vrr
{
    public unsafe sealed partial class VrrModule
    {
        internal bool CoopBind(params object[] modules)
        {
            _cooperativeBound = false;
            _cooperativeReady = null;
            _cooperativeTick = null;
            return bind_cooperative_ticks(modules);
        }
        internal bool CoopReady() => cooperative_ticks_ready();
        internal string? CoopFailure => _presentationAdmission.Failure;
        internal void CoopFrame()
        {
            IndependentPresentationEnabled = true;
            SetHookScope(true, true, false, false);
            RenderPassContext.BeginImage(.02);
        }
        internal void CoopEnter()
        {
            _padSnapshotInvocation++;
            _padSnapshotLoopDepth = 1;
            _padSnapshotOwner = Environment.CurrentManagedThreadId;
            _padSnapshotEpoch = RenderPassContext.HistoryEpoch;
            cooperative_tick_enter_loop(new(_padSnapshotInvocation, true, true));
            _render_inside_loop = true;
        }
        internal void CoopNested() => cooperative_tick_enter_loop(new(_padSnapshotInvocation, true, false));
        internal void CoopClock(bool success, bool simulation)
        {
            _render_clock_seen = true;
            _render_simulation_pass = simulation;
            if (simulation) RenderPassContext.BeginSimulation();
            cooperative_tick_clock_completed(success);
        }
        internal void CoopHandover()
        {
            _render_frame_enabled = false;
            NativeSync = true;
            RenderPassContext.ResetHistory();
        }
        internal void CoopSnapshot(bool tracked, bool applied, bool caller = true)
        {
            _padReplayApplied = applied;
            cooperative_tick_snapshot_completed(caller ? (nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation) : 1, tracked);
        }
        internal void CoopExit()
        {
            cooperative_tick_exit_loop(new(_padSnapshotInvocation, true, true));
            _render_inside_loop = false;
        }
    }
}
