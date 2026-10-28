using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadPublicationTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private readonly VrrModule _module = new VrrModule().EnableAdapter();

    public PadPublicationTests()
    {
        NativePadObservation.Reset();
        FhUtil.MenuReservation = -1;
        FhUtil.Overdrive = FhUtil.MainMenu = FhUtil.MenuPause = 0;
        FhUtil.Dummy = 0;
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        for (int port = 0; port < 2; port++)
        {
            _records[port * 0x100 + 0x80] = 7;
            _records[port * 0x100 + 0x81] = 1;
            _records[port * 0x100 + 0x9e] = 1;
            _records[port * 0x100 + 0x84] = 64;
        }
        _module.CaptureLoops(false);
        _module.DeadlineImage(10);
        _module.DeadlineClockAndExit();
        Seed(1, 10.008, 10.01);
        _module.DiagnosticEvents.Clear();
    }

    public void Dispose()
    {
        NativeClock.Advance = NativeClock.PadAction = null;
        FhUtil.PadRecordPointer = 0;
        FhUtil.Overdrive = 0;
        NativePadObservation.Reset();
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactNativePublicationChecksBothPortsWithoutWritingInput(bool capture)
    {
        _module.CaptureLoops(capture);
        byte[] before = (byte[])_records.Clone();
        Run(() => Publish());
        Assert.True(Correlation().GetProperty("native_publication_observed").GetBoolean());
        Assert.False(Correlation().GetProperty("native_input_applied").GetBoolean());
        Assert.Equal(before, _records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QolNeutralizationBeforeOrInsideContinuationDiscardsTheCandidate(bool inside)
    {
        Action neutralize = () =>
        {
            for (int port = 0; port < 2; port++)
            {
                _records.AsSpan(port * 0x100 + 0x84, 16).Clear();
                BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x84, 4), 0x80808080u);
                BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x90, 4), 0x80808080u);
                _records.AsSpan(port * 0x100 + 0x98, 4).Clear();
            }
        };
        Run(() =>
        {
            if (inside) NativeClock.PadAction = neutralize;
            else
            {
                neutralize();
                NativeClock.PadAction = () => Assert.Null(_module.PreparedPadTickForTest);
            }
            Publish();
        });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
        Assert.Equal("publication_payload_changed", _module.PadTimelineBoundary);
        Assert.Equal(0x80, _records[0x84]);
        Assert.Equal(0x80, _records[0x184]);
    }

    [Theory]
    [InlineData(0x80)]
    [InlineData(0x81)]
    [InlineData(0x82)]
    [InlineData(0x83)]
    [InlineData(0x94)]
    [InlineData(0x96)]
    [InlineData(0x9e)]
    [InlineData(0x9f)]
    public void MetadataChangesDiscardCandidatesBeforeNativePublication(int offset)
    {
        Run(() => { _records[0x100 + offset] ^= 1; Publish(); });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
        Assert.Equal("publication_metadata_changed", _module.PadTimelineBoundary);
    }

    [Fact]
    public void LatestPollPayloadIsCheckedInsteadOfTheOlderTickCandidate()
    {
        _records[0x84] = 90;
        Seed(2, 10.04, 10.041);
        Run(() => Publish());
        var result = Correlation();
        Assert.True(result.GetProperty("native_publication_observed").GetBoolean());
        Assert.All(result.GetProperty("latest").EnumerateArray(), p => Assert.Equal(1, p.GetProperty("PollSequence").GetInt64()));
        Assert.Equal(90, _records[0x84]);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("slot")]
    [InlineData("native_owner")]
    [InlineData("epoch")]
    [InlineData("duplicate")]
    [InlineData("reentry")]
    public void NonordinaryOrChangedInvocationCannotRetainPreparedInput(string condition)
    {
        Run(() =>
        {
            if (condition == "native_owner") FhUtil.Overdrive = 1;
            if (condition == "epoch") _module.ReadPadCandidates(double.NaN);
            if (condition == "reentry") NativeClock.PadAction = () => { NativeClock.PadAction = null; Publish(); };
            Publish(condition == "caller" ? 0x12345678u : 0x822224u, condition == "slot");
            if (condition == "duplicate") Publish();
        }, condition is "duplicate" or "reentry" ? 2 : 1);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void NativeHistoryRepeatAndActuatorUpdatesRemainNativeOwned()
    {
        Run(() =>
        {
            NativeClock.PadAction = () =>
            {
                _records[6] = 77;
                _records[0x9c] = 3;
                _records[0x9d] = 1;
                _records[0xb0] = 66;
                _records[0xff] = 55;
            };
            Publish();
        });
        Assert.True(Correlation().GetProperty("native_publication_observed").GetBoolean());
        Assert.Equal(77, _records[6]);
        Assert.Equal(3, _records[0x9c]);
        Assert.Equal(1, _records[0x9d]);
        Assert.Equal(66, _records[0xb0]);
        Assert.Equal(55, _records[0xff]);
    }

    [Fact]
    public void ObservationFailureStillForwardsNativeExactlyOnce()
    {
        Run(() => { NativePadObservation.ThrowOnCopy = true; Publish(); });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
        Assert.Equal("observer_failed", _module.PadTimelineBoundary);
    }

    [Fact]
    public void NativeExceptionInvalidatesPreparedInputAndKeepsTheOriginalException()
    {
        var error = new InvalidOperationException("native publication fixture");
        NativeClock.Advance = () => { NativeClock.PadAction = () => throw error; Publish(); };
        _module.DeadlineImage(10.05);
        int before = NativeClock.PadCalls;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(_module.DeadlineClockWithoutExit));
        Assert.Equal(before + 1, NativeClock.PadCalls);
        Assert.Null(_module.PreparedPadTickForTest);
        _module.FailedPreparedTickExitForTest();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadablePortsCannotCertifyPublication(bool second)
    {
        Run(() => { NativePadObservation.FailedPort = second ? 1 : 0; Publish(); });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
        Assert.Equal("publication_unreadable", _module.PadTimelineBoundary);
    }

    [Fact]
    public void DeferredPublicationAndNativeClockCannotProduceASinglePublicationReceipt()
    {
        _module.DeadlineImage(10.05);
        _module.DeferPreparedTickLatchForTest();
        NativeClock.Advance = () => Publish();
        int before = NativeClock.PadCalls;
        _module.DeadlineClockAndExit();
        Assert.Equal(before + 2, NativeClock.PadCalls);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void PreArmEntryForwardsOnceWithoutReadingTheCallerOrInput()
    {
        Run(() =>
        {
            _module.RevokePadFrame(true);
            NativePadObservation.ThrowOnCopy = true;
            Publish();
        });
        Assert.Empty(NativePadObservation.Reads);
    }

    [Fact]
    public void ForeignNativeEntryForwardsOnceAndDiscardsTheOwnersCandidate()
    {
        Run(() =>
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { Publish(); } catch (Exception error) { failure = error; } });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void PreparationOverlappingAnAlreadyEnteredForeignPublicationCannotCertify()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? failure = null;
        NativeClock.PadAction = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var thread = new Thread(() => { try { Publish(); } catch (Exception error) { failure = error; } });
        int before = NativeClock.PadCalls;
        thread.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            NativeClock.PadAction = null;
            NativeClock.Advance = () =>
            {
                release.Set();
                Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
                Assert.Null(failure);
                Publish();
            };
            _module.DeadlineImage(10.05);
            _module.DeadlineClockAndExit();
            Assert.Equal(before + 2, NativeClock.PadCalls);
            Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
        }
        finally { release.Set(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
    }

    [Fact]
    public void RegistrationUsesTheOwnedNativeEntryForTheSdkContinuationKey()
    {
        Assert.True(_module.InitHistory());
        var installed = NativeClock.HistoryHookRegistrations.Last(r => r.Rva == 0x4893e0);
        Assert.Same(NativeSnapshotBridge.PadEntry, installed.Callback);
        int before = NativeClock.PadCalls;
        installed.Callback.DynamicInvoke();
        Assert.Equal(before + 1, NativeClock.PadCalls);
    }

    private void Run(Action action, int expectedCalls = 1)
    {
        NativeClock.Advance = action;
        int before = NativeClock.PadCalls;
        _module.DeadlineImage(10.05);
        _module.DeadlineClockAndExit();
        Assert.Equal(before + expectedCalls, NativeClock.PadCalls);
    }

    private void Publish(nuint address = 0x822224, bool wrongSlot = false)
    {
        uint slot = wrongSlot ? 0 : checked((uint)address);
        var method = typeof(VrrModule).GetMethod("pad_latch_invoke", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invoke = method.CreateDelegate<Action<nuint, nuint>>(_module);
        invoke(address, (nuint)(&slot));
    }

    private void Seed(long sequence, double start, double end)
    {
        for (int port = 0; port < 2; port++)
            _module.SeedPublicationCandidate(sequence, port, start, end, MemoryMarshal.Cast<byte, uint>(_records.AsSpan(port * 0x100 + 0x80, 32)));
    }

    private JsonElement Correlation() => JsonSerializer.SerializeToElement(_module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data).GetProperty("pad_tick_correlation");
}

public unsafe sealed partial class VrrModule
{
    internal void SeedPublicationCandidate(long sequence, int port, double start, double end, ReadOnlySpan<uint> words)
        => Assert.True(_padTimeline.Observe(sequence, port, (long)(start * Stopwatch.Frequency), (long)(end * Stopwatch.Frequency), words));
}
