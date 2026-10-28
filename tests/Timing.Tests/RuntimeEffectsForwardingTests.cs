namespace Fahrenheit.Mods.Vrr;

using Xunit;

public unsafe sealed class RuntimeEffectsForwardingTests
{
    [Fact]
    public void Unarmed_wait_preserves_native_output_and_arguments_without_retiming()
    {
        NativeCalls.InitializedFrames = 11;
        NativeCalls.InitCalls = 0;
        VrrModule.TestRate = 120;
        var module = new VrrModule();
        module.StopSyntheticRuntimeForTest();
        int storage = -123;
        module.InitializeWaitForTest(42, &storage, 84);
        Assert.Equal(11, storage);
        Assert.Equal(1, NativeCalls.InitCalls);
        Assert.Equal((nint)42, NativeCalls.LastWork);
        Assert.Equal((nint)84, NativeCalls.LastStack);
    }

    [Fact]
    public void Unarmed_replacement_timer_calls_the_real_native_entry_once()
    {
        NativeCalls.NativeLimitCalls = 0;
        var module = new VrrModule();
        module.StopSyntheticRuntimeForTest();
        module.NativeLimitForTest();
        Assert.Equal(1, NativeCalls.NativeLimitCalls);
        Assert.Equal(123.5f, FhUtil.get_at<float>(EngineAddresses.LimitTimerRaw));
    }
}

public unsafe sealed partial class VrrModule
{
    internal void NativeLimitForTest() => h_limit_timer();
}

internal unsafe static partial class NativeCalls
{
    internal static int NativeLimitCalls;
    public static void NativeLimitTimer()
    {
        NativeLimitCalls++;
        FhUtil.set_at(EngineAddresses.LimitTimerRaw, 123.5f);
    }
}
