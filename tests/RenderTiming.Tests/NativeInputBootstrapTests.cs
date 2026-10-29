using Xunit;
using ParryPeer = Fahrenheit.Mods.Parry.ParryModule;
using QolPeer = Fahrenheit.Mods.Qol.FocusInputModule;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class NativeInputBootstrapTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private readonly VrrModule _module = new VrrModule().EnableAdapter();
    private readonly ParryPeer _parry = new() { Initialized = true };
    private readonly QolPeer _qol = new();

    public NativeInputBootstrapTests()
    {
        NativeSnapshotFixture.Reset();
        NativePadObservation.Reset();
        FhUtil.MenuReservation = -1;
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.Dummy = 0;
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        for (int port = 0; port < 2; port++)
        {
            _records[port * 0x100 + 0x80] = 7;
            _records[port * 0x100 + 0x81] = 1;
            _records[port * 0x100 + 0x9e] = 1;
            _records[port * 0x100 + 0x84] = 64;
        }
        _module.BeginRealSnapshotLifecycle();
        _module.PreparePass();
        Assert.True(_module.CoopBind(_parry));
        Assert.True(_module.BindBootstrapInputForTest(_qol));
    }

    public void Dispose()
    {
        NativeClock.LoopAction = null;
        NativeClock.PollAction = NativeClock.Advance = NativeClock.PadAction = NativeClock.SnapshotAction = null;
        FhUtil.PadRecordPointer = 0;
        NativePadObservation.Reset();
        VrrModule.ClearDiagnosticLoop();
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }

    [Fact]
    public void RefocusAtPublicationUsesNativeOnceThenRecoversQualifiedInput()
    {
        _qol.Neutralize = true;
        int nativeClocks = 0, standalone = 0, physical = 64;
        NativeClock.PollAction = () => SetPhysical(physical);
        NativeClock.Advance = () =>
        {
            if (++nativeClocks == 2) { _qol.Generation++; _qol.Neutralize = false; }
            if (_qol.Neutralize) Neutralize();
            _qol.Publish(_module.PublishBootstrapInputForTest, _qol.Neutralize);
        };
        NativeClock.LoopAction = _ =>
        {
            if (!_parry.PreUpdate(.025f)) standalone++;
            _module.CallPadPoll();
            _module.RunNativePass();
            _module.CallSnapshotCore();
            _module.ReadExec();
        };
        _module.RealSnapshotImage(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _module.CallDiagnosticLoop(1f / 60);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.True(_module.SnapshotBaseline);
        Assert.Equal(0, _module.BootstrapObservedPortsForTest);
        physical = 90;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _module.BootstrapOriginForTest(now - .000001 - 1d / 60);
        int seen = 0;
        NativeClock.SnapshotAction = () => seen = _records[0x84];
        _module.RealSnapshotImage(now);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(1, _parry.Calls);
        Assert.Equal(.025f, _parry.Delta);
        Assert.Equal(3, nativeClocks);
        Assert.Equal(90, seen);
        Assert.Equal(90, _records[0x84]);
        physical = 64;
        _module.RealSnapshotImage(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.True(_module.SnapshotBaseline);
        now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _module.BootstrapOriginForTest(now - .000001 - 1d / 60);
        physical = 90;
        _module.RealSnapshotImage(now);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(2, _parry.Calls);
        Assert.Equal(1f / 60, _parry.Delta);
        Assert.Equal(64, seen);
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(5, nativeClocks);
        Assert.Equal(3, standalone);
    }

    private void SetPhysical(int value)
    {
        for (int port = 0; port < 2; port++) _records[port * 0x100 + 0x84] = (byte)value;
    }

    private void Neutralize()
    {
        for (int port = 0; port < 2; port++)
        {
            _records.AsSpan(port * 0x100 + 0x84, 16).Clear();
            _records.AsSpan(port * 0x100 + 0x98, 4).Clear();
            BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x84, 4), 0x80808080u);
            BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x90, 4), 0x80808080u);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePollFactsSurviveWarmupAndSupplyTheFirstDueOwnedTick(bool capture)
    {
        _module.CaptureLoops(capture);
        int standalone = 0;
        NativeClock.Advance = () => _qol.Publish(_module.PublishBootstrapInputForTest);
        NativeClock.LoopAction = _ =>
        {
            if (!_parry.PreUpdate(.025f)) standalone++;
            _module.CallPadPoll();
            _module.RunNativePass();
            _module.CallSnapshotCore();
            _module.ReadExec();
        };
        _module.RealSnapshotImage(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(1, standalone);
        Assert.True(_module.SnapshotBaseline);
        int nativeFacts = _module.BootstrapObservedPortsForTest;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        _module.BootstrapOriginForTest(now - .000001 - 1d / 60);
        for (int port = 0; port < 2; port++) _records[port * 0x100 + 0x84] = 90;
        int snapshotValue = 0, callbackValue = 0;
        NativeClock.SnapshotAction = () => snapshotValue = _records[0x84];
        _parry.DuringTick = () => callbackValue = _records[0x84];
        _module.RealSnapshotImage(now);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(2, nativeFacts);
        Assert.Equal(1, _parry.Calls);
        Assert.Equal(1f / 60, _parry.Delta);
        Assert.Equal(64, snapshotValue);
        Assert.Equal(64, callbackValue);
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(1, standalone);
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool BindBootstrapInputForTest(params object[] modules) => bind_pad_input_policy(modules);
    internal void PublishBootstrapInputForTest()
    {
        uint slot = 0x822224;
        pad_latch_invoke(slot, (nuint)(&slot));
    }
    internal int BootstrapObservedPortsForTest => _padTimeline.LatestObserved().Count(sample => sample is not null);
    internal void BootstrapOriginForTest(double origin) { _simulationClock.Reset(); _simulationClock.Plan(origin); }
}