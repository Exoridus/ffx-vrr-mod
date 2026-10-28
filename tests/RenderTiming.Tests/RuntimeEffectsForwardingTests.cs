namespace Fahrenheit.Mods.Vrr;

using Xunit;

[Collection("Render scheduler")]
public sealed class RuntimeEffectsForwardingTests
{
    [Fact]
    public void Unarmed_diagnostic_loop_forwards_once_before_any_identity_or_capture_work()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.StopSyntheticRuntimeForTest();
        int calls = 0;
        NativeClock.LoopAction = delta =>
        {
            calls++;
            Assert.Equal(0.125f, delta);
            Assert.Equal(0, module.CurrentDiagnosticLoop);
        };
        try { module.CallDiagnosticLoop(0.125f); }
        finally { NativeClock.LoopAction = null; VrrModule.ClearDiagnosticLoop(); }
        Assert.Equal(1, calls);
        Assert.Empty(module.DiagnosticEvents);
    }
}
