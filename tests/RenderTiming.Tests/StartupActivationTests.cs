using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class StartupActivationTests
{
    [Fact]
    public void Owner_arms_only_after_native_verification_and_application()
    {
        var activation = new StartupActivation();
        var order = new List<string>();
        Assert.False(activation.IsArmed);
        activation.Prepare(17);
        Assert.False(activation.IsArmed);

        bool armed = activation.Invoke(17, true,
            () => { Assert.False(activation.IsArmed); order.Add("native"); },
            () => { Assert.False(activation.IsArmed); order.Add("verify"); return true; },
            () => { Assert.False(activation.IsArmed); order.Add("apply"); });

        Assert.True(armed);
        Assert.True(activation.IsArmed);
        Assert.Equal(["native", "verify", "apply"], order);
    }

    [Theory]
    [InlineData(false, 17u, true)]
    [InlineData(true, 18u, true)]
    [InlineData(true, 17u, false)]
    public void Unprepared_foreign_thread_or_wrong_caller_only_forwards_native(bool prepared, uint thread, bool caller)
    {
        var activation = new StartupActivation();
        if (prepared) activation.Prepare(17);
        int calls = 0;
        Assert.False(activation.Invoke(thread, caller, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
        Assert.False(activation.IsArmed);
        if (!prepared) activation.Prepare(17);
        Assert.True(activation.Invoke(17, true, () => calls++, () => true, () => { }));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Repeated_calls_after_arming_only_forward_native()
    {
        var activation = Prepared();
        Assert.True(activation.Invoke(17, true, () => { }, () => true, () => { }));
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
        Assert.True(activation.IsArmed);
    }

    [Fact]
    public void Failed_verification_permanently_refuses_activation()
    {
        var activation = Prepared();
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, () => false, UnexpectedApplication));
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(2, calls);
        Assert.False(activation.IsArmed);
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(17));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("verify")]
    [InlineData("apply")]
    public void Owner_exception_is_preserved_and_permanently_refuses_activation(string failure)
    {
        var activation = Prepared();
        var expected = new InvalidOperationException(failure);
        var order = new List<string>();
        var actual = Assert.Throws<InvalidOperationException>(() => activation.Invoke(17, true,
            () => { order.Add("native"); if (failure == "native") throw expected; },
            () => { order.Add("verify"); if (failure == "verify") throw expected; return true; },
            () => { order.Add("apply"); if (failure == "apply") throw expected; }));
        Assert.Same(expected, actual);
        Assert.Equal(failure == "native" ? ["native"] : failure == "verify" ? ["native", "verify"] : ["native", "verify", "apply"], order);
        Assert.False(activation.IsArmed);
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, 17u, true)]
    [InlineData(true, 18u, true)]
    [InlineData(true, 17u, false)]
    public void Forwarded_native_exception_is_preserved_without_consuming_activation(bool prepared, uint thread, bool caller)
    {
        var activation = new StartupActivation();
        if (prepared) activation.Prepare(17);
        var expected = new InvalidOperationException("native");
        int calls = 0;
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => activation.Invoke(thread, caller,
            () => { calls++; throw expected; }, UnexpectedVerification, UnexpectedApplication)));
        Assert.Equal(1, calls);
        if (!prepared) activation.Prepare(17);
        Assert.True(activation.Invoke(17, true, () => { }, () => true, () => { }));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("verify")]
    [InlineData("apply")]
    public void Matched_owner_reentry_forwards_native_and_refuses_the_outer_activation(string stage)
    {
        var activation = Prepared();
        var order = new List<string>();
        void Reenter()
        {
            Assert.False(activation.Invoke(17, true, () => order.Add("nested native"), UnexpectedVerification, UnexpectedApplication));
        }
        Assert.False(activation.Invoke(17, true,
            () => { order.Add("native"); if (stage == "native") Reenter(); },
            () => { order.Add("verify"); if (stage == "verify") Reenter(); return true; },
            () => { order.Add("apply"); if (stage == "apply") Reenter(); }));
        Assert.Equal(stage == "native" ? ["native", "nested native"] : stage == "verify" ? ["native", "verify", "nested native"] : ["native", "verify", "apply", "nested native"], order);
        Assert.False(activation.IsArmed);
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(18u, true)]
    [InlineData(17u, false)]
    public void Unmatched_reentry_does_not_refuse_the_owner(uint thread, bool caller)
    {
        var activation = Prepared();
        int calls = 0;
        Assert.True(activation.Invoke(17, true,
            () => Assert.False(activation.Invoke(thread, caller, () => calls++, UnexpectedVerification, UnexpectedApplication)),
            () => true, () => { }));
        Assert.Equal(1, calls);
        Assert.True(activation.IsArmed);
    }

    [Fact]
    public void Prepare_rejects_zero_without_consuming_the_one_preparation()
    {
        var activation = new StartupActivation();
        Assert.Throws<ArgumentOutOfRangeException>(() => activation.Prepare(0));
        activation.Prepare(17);
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(17));
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(18));
        Assert.True(activation.Invoke(17, true, () => { }, () => true, () => { }));
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(17));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_permanently_prevents_preparation_and_activation(bool prepared)
    {
        var activation = new StartupActivation();
        if (prepared) activation.Prepare(17);
        activation.Stop();
        activation.Stop();
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(17));
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
        Assert.False(activation.IsArmed);
    }

    [Fact]
    public void Stop_revokes_an_armed_activation()
    {
        var activation = Prepared();
        Assert.True(activation.Invoke(17, true, () => { }, () => true, () => { }));
        activation.Stop();
        Assert.False(activation.IsArmed);
        int calls = 0;
        Assert.False(activation.Invoke(17, true, () => calls++, UnexpectedVerification, UnexpectedApplication));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("verify")]
    [InlineData("apply")]
    public void Concurrent_stop_during_callbacks_cannot_publish_or_continue_activation(string stage)
    {
        var activation = Prepared();
        var order = new List<string>();
        void StopElsewhere()
        {
            var stopping = Task.Run(activation.Stop);
            Assert.True(stopping.Wait(TimeSpan.FromSeconds(5)), "Stop must not wait for a callback lock.");
        }
        Assert.False(activation.Invoke(17, true,
            () => { order.Add("native"); if (stage == "native") StopElsewhere(); },
            () => { order.Add("verify"); if (stage == "verify") StopElsewhere(); return true; },
            () => { order.Add("apply"); if (stage == "apply") StopElsewhere(); }));
        Assert.Equal(stage == "native" ? ["native"] : stage == "verify" ? ["native", "verify"] : ["native", "verify", "apply"], order);
        Assert.False(activation.IsArmed);
        Assert.Throws<InvalidOperationException>(() => activation.Prepare(17));
    }

    private static StartupActivation Prepared()
    {
        var activation = new StartupActivation();
        activation.Prepare(17);
        return activation;
    }

    private static bool UnexpectedVerification() => throw new InvalidOperationException("Verification must not run.");
    private static void UnexpectedApplication() => throw new InvalidOperationException("Application must not run.");
}
