using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class ActorCompletionTests
{
    [Fact]
    public void Completion_with_a_callback_waiting_for_the_gate_refuses_and_invalidates_history()
    {
        using var f = new BridgeFixture();
        SeedPairedHistory(f.Module);
        Exception? callbackError = null;
        var callback = new Thread(() =>
        {
            try { f.Module.ActorCallback(); }
            catch (Exception error) { callbackError = error; }
        }) { IsBackground = true };
        try
        {
            lock (f.Module.ActorGate)
            {
                callback.Start();
                Assert.True(SpinWait.SpinUntil(() => f.Module.ActorCallbacks == 1, TimeSpan.FromSeconds(5)),
                    "Actor callback did not enter before the deadline.");
                Assert.Equal(0, BridgeNative.ActorInstances);
                f.Module.ActorComplete();
                Assert.NotNull(f.Module.Failure);
                Assert.False(f.Module.HasActorPair);
                Assert.False(f.Module.ActorSampling);
                Assert.False(f.Module.ActorActive);
            }
        }
        finally
        {
            Assert.True(callback.Join(TimeSpan.FromSeconds(5)), "Actor callback did not leave after releasing the gate.");
        }
        Assert.Null(callbackError);
        Assert.Equal(1, BridgeNative.ActorInstances);
        Assert.Equal(0, f.Module.ActorCallbacks);
        f.Module.ActorBegin();
        Assert.NotNull(f.Module.Failure);
    }

    [Fact]
    public void Completion_without_entered_callbacks_keeps_the_valid_history_pair()
    {
        using var f = new BridgeFixture();
        SeedPairedHistory(f.Module);
        f.Module.ActorComplete();
        Assert.Null(f.Module.Failure);
        Assert.True(f.Module.HasActorPair);
        Assert.False(f.Module.ActorSampling);
        Assert.True(f.Module.ActorActive);
        Assert.Equal(0, f.Module.ActorCallbacks);
    }

    [Fact]
    public void Throwing_original_still_leaves_the_callback_count_balanced()
    {
        using var f = new BridgeFixture();
        var error = new InvalidOperationException("original failed");
        BridgeNative.OnActorInstance = (instance, matrix) =>
        {
            Assert.Equal(0, instance);
            Assert.Equal(0, matrix);
            Assert.Equal(1, f.Module.ActorCallbacks);
            throw error;
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => f.Module.ActorCallback()));
        Assert.Equal(1, BridgeNative.ActorInstances);
        Assert.Equal(0, f.Module.ActorCallbacks);
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    public void Native_queued_or_invalid_free_count_refuses_even_without_entered_callbacks(int freeCount)
    {
        using var f = new BridgeFixture();
        SeedPairedHistory(f.Module);
        f.SetActorFreeCount(freeCount);
        Assert.Equal(0, f.Module.ActorCallbacks);
        f.Module.ActorComplete();
        Assert.Equal("actor_jobs_unconfirmed", f.Module.Failure);
        Assert.False(f.Module.HasActorPair);
        Assert.False(f.Module.ActorSampling);
        Assert.False(f.Module.ActorActive);
    }

    [Theory]
    [InlineData("null_scheduler")]
    [InlineData("unreadable_slot")]
    [InlineData("unreadable_count")]
    [InlineData("changed_scheduler")]
    public void Missing_or_changed_native_scheduler_refuses_history_completion(string condition)
    {
        using var f = new BridgeFixture();
        SeedPairedHistory(f.Module);
        if (condition == "null_scheduler") f.SetActorScheduler(0);
        if (condition == "unreadable_slot") MemoryProbe.CopyAllowed = at => at != f.ActorSchedulerSlot;
        if (condition == "unreadable_count") MemoryProbe.CopyAllowed = at => at != f.ActorFreeCountAddress;
        if (condition == "changed_scheduler") MemoryProbe.CopyAllowed = at =>
        {
            if (at == f.ActorFreeCountAddress) f.SetActorScheduler(f.ActorScheduler + 0x1000);
            return true;
        };
        Assert.Equal(0, f.Module.ActorCallbacks);
        f.Module.ActorComplete();
        Assert.Equal("actor_jobs_unconfirmed", f.Module.Failure);
        Assert.False(f.Module.HasActorPair);
        Assert.False(f.Module.ActorSampling);
        Assert.False(f.Module.ActorActive);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refusal_retains_real_native_loans_without_restoring_even_after_later_idle_state(bool enteredCallback)
    {
        using var f = new BridgeFixture();
        SeedPairedHistory(f.Module);
        f.Module.SeedActorLoan();
        byte[] world = f.Module.ActorLoanWorld;
        var bone = f.Module.ActorLoanBone;
        var callback = new Thread(() => f.Module.ActorCallback()) { IsBackground = true };
        try
        {
            lock (f.Module.ActorGate)
            {
                if (enteredCallback)
                {
                    callback.Start();
                    Assert.True(SpinWait.SpinUntil(() => f.Module.ActorCallbacks == 1, TimeSpan.FromSeconds(5)));
                }
                else f.SetActorFreeCount(7);
                f.Module.ActorComplete();
                Assert.NotNull(f.Module.Failure);
                Assert.Equal(world, f.Module.ActorLoanWorld);
                Assert.Equal(bone, f.Module.ActorLoanBone);
                Assert.Equal(1, f.Module.ActorLoans);
            }
        }
        finally
        {
            if (enteredCallback) Assert.True(callback.Join(TimeSpan.FromSeconds(5)));
        }
        f.SetActorFreeCount(8);
        f.Module.ActorBegin(); f.Module.ActorPresentation(); f.Module.ActorComplete();
        f.Module.ActorRestore(); f.Module.ActorReset(); f.Module.ActorReplay();
        Assert.False(f.Module.ActorSampling);
        Assert.False(f.Module.ActorActive);
        Assert.False(f.Module.HasActorPair);
        Assert.Equal(world, f.Module.ActorLoanWorld);
        Assert.Equal(bone, f.Module.ActorLoanBone);
        Assert.Equal(1, f.Module.ActorLoans);
        Assert.Equal(2, f.Module.RestoreActorLoanIntoCopies());
        Assert.Equal(world, f.Module.ActorLoanWorld);
        Assert.Equal(bone, f.Module.ActorLoanBone);
    }
    [Theory]
    [InlineData("initialize", "begin")]
    [InlineData("initialize", "presentation")]
    [InlineData("initialize", "restore")]
    [InlineData("initialize", "reset")]
    [InlineData("initialize", "replay")]
    [InlineData("initialize", "complete")]
    [InlineData("initialize", "capture")]
    [InlineData("finalize", "restore")]
    [InlineData("reset", "restore")]
    public void Observed_graphics_lifetime_change_retains_native_loans_even_at_the_same_scheduler(string lifecycle, string operation)
    {
        using var f = new BridgeFixture();
        f.Init();
        SeedPairedHistory(f.Module);
        f.Module.SeedActorLoan();
        byte[] world = f.Module.ActorLoanWorld;
        var bone = f.Module.ActorLoanBone;
        if (lifecycle == "initialize") Assert.Equal(0u, f.Initialize());
        if (lifecycle == "finalize") Assert.Equal(0u, f.FinalizeGraphics());
        if (lifecycle == "reset") f.ResetGraphics();
        switch (operation)
        {
            case "begin": f.Module.ActorBegin(); break;
            case "presentation": f.Module.ActorPresentation(); break;
            case "restore": f.Module.ActorRestore(); break;
            case "reset": f.Module.ActorReset(); break;
            case "replay": f.Module.ActorReplay(); break;
            case "complete": f.Module.ActorComplete(); break;
            case "capture": f.Module.ActorCaptureLoan(); Assert.Equal(1, BridgeNative.ActorInstances); break;
        }
        Assert.Equal("actor_graphics_lifetime_changed", f.Module.Failure);
        Assert.False(f.Module.ActorSampling);
        Assert.False(f.Module.ActorActive);
        Assert.False(f.Module.HasActorPair);
        Assert.Equal(world, f.Module.ActorLoanWorld);
        Assert.Equal(bone, f.Module.ActorLoanBone);
        Assert.Equal(1, f.Module.ActorLoans);
        f.SetActorFreeCount(8);
        f.Module.ActorBegin(); f.Module.ActorRestore(); f.Module.ActorReset();
        Assert.Equal(world, f.Module.ActorLoanWorld);
        Assert.Equal(bone, f.Module.ActorLoanBone);
        Assert.Equal(2, f.Module.RestoreActorLoanIntoCopies());
    }
    [Fact]
    public void Lifecycle_change_inside_native_actor_setter_refuses_post_return_capture()
    {
        using var f = new BridgeFixture();
        f.Init();
        SeedPairedHistory(f.Module);
        f.Module.SeedActorLoan();
        BridgeNative.OnActorInstance = (_, _) => Assert.Equal(0u, f.Initialize());
        f.Module.ActorCaptureLoan();
        Assert.Equal(1, BridgeNative.ActorInstances);
        Assert.Equal("actor_graphics_lifetime_changed", f.Module.Failure);
        Assert.False(f.Module.ActorSampling);
        Assert.False(f.Module.ActorActive);
        Assert.False(f.Module.HasActorPair);
        Assert.False(f.Module.ActorHasCanonicalWorld);
        Assert.Equal(1, f.Module.ActorLoans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_preinitialization_cleanup_does_not_bind_a_resource_epoch(bool reset)
    {
        using var f = new BridgeFixture();
        if (reset) f.Module.ActorReset();
        else f.Module.ActorRestore();
        f.Init();
        f.Module.ActorBegin();
        Assert.Null(f.Module.Failure);
        Assert.True(f.Module.ActorSampling);
        Assert.True(f.Module.ActorActive);
    }
    private static void SeedPairedHistory(VrrModule module)
    {
        module.ActorBegin();
        module.CaptureActor(0);
        module.ActorComplete();
        module.ActorBegin();
        module.CaptureActor(10);
        Assert.True(module.HasActorPair);
        Assert.True(module.ActorSampling);
        Assert.True(module.ActorActive);
    }
}
