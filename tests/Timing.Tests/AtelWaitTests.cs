using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class AtelWaitTests
{
    [Theory]
    [InlineData(40, 60, 80)]
    [InlineData(1, 60, 1)]
    [InlineData(0, 60, 0)]
    [InlineData(-1, 60, -2)]
    [InlineData(2147483647, 120, 2147483647)]
    [InlineData(-2147483648, 120, -2147483648)]
    public void RetimesNativeInitializedValueAndForwardsArguments(int nativeFrames, float rate, int expected)
    {
        NativeCalls.InitializedFrames = nativeFrames;
        NativeCalls.InitCalls = 0;
        VrrModule.TestRate = rate;
        int storage = -123;
        new VrrModule().InitializeWaitForTest(42, &storage, 84);
        Assert.Equal(expected, storage);
        Assert.Equal(1, NativeCalls.InitCalls);
        Assert.Equal((nint)42, NativeCalls.LastWork);
        Assert.Equal((nint)84, NativeCalls.LastStack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoresPriorExecScopeAfterOriginalThrows(bool prior)
    {
        NativeCalls.ThrowInExec = true;
        VrrModule.SetWaitExecScopeForTest(prior);
        int storage = 0;
        bool threw = false;
        try { new VrrModule().ExecuteWaitForTest(0, &storage); }
        catch (InvalidOperationException) { threw = true; }
        finally { NativeCalls.ThrowInExec = false; }
        Assert.True(threw);
        Assert.Equal(prior, VrrModule.WaitExecScopeForTest);
        VrrModule.SetWaitExecScopeForTest(false);
    }
}

public unsafe sealed partial class VrrModule
{
    internal void InitializeWaitForTest(nint work, int* storage, nint stack) { _atel_wait_hooks_ready = true; h_atel_wait_init(work, storage, stack); }
    internal int ExecuteWaitForTest(nint work, int* storage) { _atel_wait_hooks_ready = true; return h_atel_wait_exec(work, storage); }
    internal static bool WaitExecScopeForTest => _inside_atel_wait_exec;
    internal static void SetWaitExecScopeForTest(bool value) => _inside_atel_wait_exec = value;
}
