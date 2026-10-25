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
