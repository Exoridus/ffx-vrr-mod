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
        _module.DeadlineImage(10.001);
        _module.DeadlineClockAndExit();
        Seed(1, 10.008, 10.01);
        _module.DiagnosticEvents.Clear();
    }

    public void Dispose()
    {
        NativeClock.Advance = NativeClock.PadAction = null;
        NativeClock.LoopAction = null;
        NativeClock.SnapshotAction = null;
        VrrModule.ClearDiagnosticLoop();
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

    [Fact]
    public void SelectedPayloadSurvivesThroughConsumersAndRootFinallyRestoresOnlyItsFields()
    {
        Assert.True(BindReplay());
        FuturePayload();
        byte[] before = (byte[])_records.Clone();
        NativeClock.PadAction = () =>
        {
            Assert.Equal(64, _records[0x84]);
            Assert.Equal(0, _records[0x98]);
            _records[6] = 77;
            _records[0x9c] = 3;
            _records[0xb0] = 66;
            _records[0xff] = 55;
        };
        RunReplay(() => { Assert.Equal(64, _records[0x84]); Assert.Equal(0, _records[0x98]); });
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
        before[6] = 77; before[0x9c] = 3; before[0xb0] = 66; before[0xff] = 55;
        Assert.Equal(before, _records);
        Assert.True(Correlation().GetProperty("native_input_applied").GetBoolean());
        Assert.False(Correlation().GetProperty("native_continuity_proven").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeOrEarlierCleanupFailureCannotLeaveTheSelectedRawPayload(bool cleanup)
    {
        Assert.True(BindReplay());
        FuturePayload();
        var error = new InvalidOperationException("replay cleanup fixture");
        Action fail = () => throw error;
        if (cleanup) _module.DiagnosticCleanup = fail;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => RunReplay(cleanup ? () => { } : fail)));
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }

    [Fact]
    public void FrameRevocationDoesNotCancelOwnedPayloadRestoration()
    {
        Assert.True(BindReplay());
        FuturePayload();
        RunReplay(() => _module.RevokePadFrame(false));
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }

    [Fact]
    public void ForeignPayloadIsRetainedAndPreventsAnotherReplayLoan()
    {
        Assert.True(BindReplay());
        FuturePayload();
        NativeClock.PadAction = () => _records[0x84] = 120;
        RunReplay(() => Assert.Equal(120, _records[0x84]));
        Assert.Equal(120, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
        Assert.NotEqual("", _module.InputGateReason);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void ObservedFocusPolicyChangeDuringPublicationCannotBeAccepted()
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule();
        Assert.True(BindReplay(policy));
        FuturePayload();
        NativeClock.PadAction = () => policy.Generation++;
        RunReplay(() => { });
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void InstalledIncompatibleOrDuplicateFocusPoliciesRefuseBinding()
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule();
        Assert.False(BindReplay(policy, new Fahrenheit.Mods.Qol.FocusInputModule()));
        Assert.True(BindReplay(policy));
        policy.Ready = false;
        FuturePayload();
        RunReplay(() => Assert.Equal(90, _records[0x84]));
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void FocusNeutralizationRetainsNativePayloadWithoutCreatingALoan()
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule { Neutralize = true };
        Assert.True(BindReplay(policy));
        FuturePayload();
        RunReplay(() => Assert.Equal(90, _records[0x84]));
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void ForeignLoopCancellationCannotConsumeTheOwnersRestorationAttempt()
    {
        Assert.True(BindReplay());
        FuturePayload();
        RunReplay(() =>
        {
            NativeClock.LoopAction = _ => { };
            Exception? failure = null;
            var thread = new Thread(() => { try { _module.CallDiagnosticLoop(1f / 60); } catch (Exception error) { failure = error; } });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
            Assert.Equal(64, _records[0x84]);
        });
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }

    [Fact]
    public void ReplayRestorationIsOutsideTheNativeCallMeasurement()
    {
        Assert.True(BindReplay());
        FuturePayload();
        _module.CaptureLoops(true);
        RunReplay(() => { });
        long restorationStart = NativePadObservation.Reads.TakeLast(2).First().Start;
        var data = JsonSerializer.SerializeToElement(_module.DiagnosticEvents.Last(e => e.Kind == "loop_exit").Data);
        Assert.True(data.GetProperty("native_end_ticks").GetInt64() <= restorationStart);
    }

    [Fact]
    public void FailedRestorationReadCannotReplaceTheNativeException()
    {
        Assert.True(BindReplay());
        FuturePayload();
        var error = new InvalidOperationException("primary native replay fixture");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => RunReplay(() =>
        {
            NativePadObservation.ThrowOnCopy = true;
            throw error;
        })));
        Assert.NotEqual("", _module.InputGateReason);
    }

    [Fact]
    public void UncapturedPublicationRestoresLatestNativeInputBeforeForwarding()
    {
        Assert.True(BindReplay());
        FuturePayload();
        RunReplay(() =>
        {
            NativeClock.PadAction = () => Assert.Equal(90, _records[0x84]);
            _module.CallPad();
        });
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }

    [Fact]
    public void CopyExceptionAfterNativePublicationCancelsTheLoanBeforeConsumers()
    {
        Assert.True(BindReplay());
        FuturePayload();
        NativeClock.PadAction = () => NativePadObservation.ThrowOnCopy = true;
        RunReplay(() => Assert.NotNull(_module.CoopFailure));
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void ForeignChangeAfterTheCopyCannotBeOverwrittenByStaging()
    {
        Assert.True(BindReplay());
        FuturePayload();
        NativePadObservation.AfterCopy = () =>
        {
            if (NativePadObservation.Reads.Count == 2) _records[0x84] = 120;
        };
        NativeClock.PadAction = () => Assert.Equal(120, _records[0x84]);
        RunReplay(() => { });
        Assert.Equal(120, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Theory]
    [InlineData(10.001, 2f)]
    [InlineData(10.05, 2f)]
    [InlineData(10.5, 1f)]
    public void NewlyUnmappedClockAfterParryGrantReturnsToNativeOnce(double now, float speed)
    {
        var parry = BindCooperativeReplay();
        int clocks = 0;
        NativeClock.Advance = () => { clocks++; Publish(); };
        NativeClock.SnapshotAction = () => Assert.Equal(90, _records[0x84]);
        NativeClock.LoopAction = _ =>
        {
            Assert.True(parry.PreUpdate(.025f));
            _module.SnapshotClock(speed / 60);
            Assert.Null(_module.PreparedPadTickForTest);
            _module.CallSnapshotCore();
        };
        _module.DeadlineImage(now);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(1, clocks);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(.025f, parry.Delta);
        Assert.Equal(90, _records[0x84]);
        if (now == 10.5)
        {
            int standaloneAfterReset = 0;
            NativeClock.LoopAction = _ =>
            {
                if (!parry.PreUpdate(.025f)) standaloneAfterReset++;
                _module.SnapshotClock(1f / 60);
                _module.CallSnapshotCore();
                _module.ReadExec();
            };
            _module.RealSnapshotImage(now + .01);
            _module.CallDiagnosticLoop(1f / 60);
            Assert.Equal(1, standaloneAfterReset);
            Assert.True(_module.SnapshotBaseline);
            SeedRecoverySamples(now + .012, now + .07);
            NativeClock.SnapshotAction = () => Assert.Equal(64, _records[0x84]);
            _module.RealSnapshotImage(now + .05);
            _module.CallDiagnosticLoop(1f / 60);
            Assert.Null(_module.CoopFailure);
            Assert.Equal(2, parry.Calls);
            Assert.Equal(1f / 60, parry.Delta);
            Assert.Equal(3, clocks);
            Assert.True(Correlation().GetProperty("native_input_applied").GetBoolean());
        }
    }

    [Fact]
    public void NativeOwnerLateBaselineDoesNotLoanParryBeforeTheClockMappingIsReady()
    {
        var parry = BindCooperativeReplay();
        _module.BeginRealSnapshotLifecycle();
        _module.DeadlineReset();
        _module.NativeMovie = true;
        int standaloneTicks = 0, nativeClocks = 0;
        NativeClock.Advance = () => { nativeClocks++; _module.NativeMovie = false; Publish(); };
        NativeClock.LoopAction = _ =>
        {
            if (!parry.PreUpdate(.025f)) standaloneTicks++;
            _module.SnapshotClock(1f / 60);
            _module.CallSnapshotCore();
            _module.ReadExec();
        };
        _module.RealSnapshotImage(10.05);
        _module.CallDiagnosticLoop(1f / 60);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.True(_module.SnapshotBaseline);
        Assert.False(_module.BootstrapClockMappedForTest);
        _module.RealSnapshotImage(10.07);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(3, standaloneTicks + parry.Calls);
        Assert.Equal(3, nativeClocks);
        Assert.Equal(90, _records[0x84]);
        SeedRecoverySamples(10.075, 10.095);
        NativeClock.SnapshotAction = () => Assert.Equal(64, _records[0x84]);
        parry.DuringTick = () => Assert.Equal(64, _records[0x84]);
        _module.RealSnapshotImage(10.09);
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Null(_module.CoopFailure);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(1f / 60, parry.Delta);
        Assert.Equal(4, nativeClocks);
        Assert.True(Correlation().GetProperty("native_input_applied").GetBoolean());
        Assert.Equal(90, _records[0x84]);
    }
    [Fact]
    public void RealLoopDispatchesParryAfterSelectedInputSnapshotAndBeforeRestoration()
    {
        var parry = BindCooperativeReplay();
        bool snapshot = false;
        NativeClock.SnapshotAction = () => snapshot = true;
        parry.DuringTick = () => { Assert.True(snapshot); Assert.Equal(64, _records[0x84]); };
        CooperativeReplayStep(parry, 10.05);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(1f / 60, parry.Delta);
        Assert.Equal(90, _records[0x84]);
        Assert.True(Correlation().GetProperty("native_input_applied").GetBoolean());
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x40)]
    public void StockHostCooperativeTickReadsTheSelectedSyntheticScriptAction(int actionMask)
    {
        _module.ReadPadCandidates(double.NaN);
        for (int port = 0; port < 2; port++)
            BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x98, 2), (ushort)actionMask);
        Seed(1, 10.008, 10.01);
        var parry = BindCooperativeReplay();
        ushort syntheticScriptButtons = 0;
        NativeClock.SnapshotAction = () => syntheticScriptButtons = BitConverter.ToUInt16(_records, 0x98);
        parry.DuringTick = () =>
        {
            Assert.Equal(actionMask, syntheticScriptButtons);
            Assert.True((syntheticScriptButtons & actionMask) != 0);
            Assert.Equal(64, _records[0x84]);
        };
        CooperativeReplayStep(parry, 10.05);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(2, _records[0x98]);
        Assert.Equal(90, _records[0x84]);
    }

    [Fact]
    public void ExtraImageConsumesNoParryTickAndNextSimulationStillDispatches()
    {
        var parry = BindCooperativeReplay();
        CooperativeReplayStep(parry, 10.005, expectedPublications: 0);
        Assert.Equal(0, parry.Calls);
        Assert.Null(_module.CoopFailure);
        CooperativeReplayStep(parry, 10.02);
        Assert.Equal(1, parry.Calls);
    }

    [Fact]
    public void ParryCallbackFailurePropagatesOnceAndRestoresTheInputLoan()
    {
        var parry = BindCooperativeReplay();
        var error = new InvalidOperationException("cooperative native tick fixture");
        parry.DuringTick = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => CooperativeReplayStep(parry, 10.05)));
        Assert.Equal(1, parry.Calls);
        Assert.NotNull(_module.CoopFailure);
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }

    [Fact]
    public void NativeOwnerSelectedBeforePublicationKeepsNativeInputAndFulfillsDeferredTick()
    {
        var parry = BindCooperativeReplay();
        NativeClock.SnapshotAction = () => Assert.Equal(90, _records[0x84]);
        CooperativeReplayStep(parry, 10.05, handover: true);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(.025f, parry.Delta);
        Assert.Equal(90, _records[0x84]);
    }

    [Fact]
    public void MissingRealSnapshotRefusesInsteadOfDeliveringALateParryTick()
    {
        var parry = BindCooperativeReplay();
        CooperativeReplayStep(parry, 10.05, snapshot: false);
        Assert.Equal(0, parry.Calls);
        Assert.NotNull(_module.CoopFailure);
        Assert.Equal(90, _records[0x84]);
    }

    [Fact]
    public void AcceptedPublicationRetainsItsRawTimelineThroughALaterNativeOwnerChange()
    {
        var parry = BindCooperativeReplay();
        NativeClock.SnapshotAction = () => Assert.Equal(64, _records[0x84]);
        CooperativeReplayStep(parry, 10.05, lateHandover: true);
        Assert.Equal(1, parry.Calls);
        Assert.Equal(1f / 60, parry.Delta);
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
    }
    private void SeedRecoverySamples(double past, double future)
    {
        for (int port = 0; port < 2; port++) _records[port * 0x100 + 0x84] = 64;
        Seed(3, past, past + .001);
        for (int port = 0; port < 2; port++) _records[port * 0x100 + 0x84] = 90;
        Seed(4, future, future + .001);
    }
    private Fahrenheit.Mods.Parry.ParryModule BindCooperativeReplay()
    {
        NativeSnapshotFixture.Reset();
        var parry = new Fahrenheit.Mods.Parry.ParryModule { Initialized = true };
        Assert.True(_module.CoopBind(parry));
        Assert.True(BindReplay());
        FuturePayload();
        return parry;
    }

    private void CooperativeReplayStep(Fahrenheit.Mods.Parry.ParryModule parry, double now,
        bool snapshot = true, bool handover = false, int expectedPublications = 1, bool lateHandover = false)
    {
        NativeClock.Advance = () => { if (handover) _module.NativeMovie = true; Publish(); if (lateHandover) _module.NativeMovie = true; };
        NativeClock.LoopAction = _ =>
        {
            Assert.True(parry.PreUpdate(.025f));
            _module.SnapshotClock(1f / 60);
            if (snapshot) _module.CallSnapshotCore();
        };
        _module.DeadlineImage(now);
        int before = NativeClock.PadCalls;
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(before + expectedPublications, NativeClock.PadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QolPublicationCooperationStagesTheSameTickInEitherHookOrder(bool qolOuter)
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule();
        Assert.True(BindReplay(policy));
        FuturePayload();
        Action terminal = () => { Assert.Equal(64, _records[0x84]); Assert.Equal(0, _records[0x98]); };
        NativeClock.PadAction = () => { if (qolOuter) terminal(); else policy.Publish(terminal); };
        NativeClock.Advance = () =>
        {
            if (qolOuter) policy.Publish(() => Publish(0x12345678));
            else Publish();
        };
        NativeClock.LoopAction = _ => { _module.DeadlineClockWithoutExit(); Assert.Equal(64, _records[0x84]); };
        _module.DeadlineImage(10.05);
        int before = NativeClock.PadCalls;
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(before + 1, NativeClock.PadCalls);
        Assert.Equal(90, _records[0x84]);
        Assert.Equal(2, _records[0x98]);
        Assert.True(Correlation().GetProperty("native_input_applied").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QolOwnsAllNeutralizedWordsIncludingThoseEqualToTheSelectedState(bool qolOuter)
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule();
        Assert.True(BindReplay(policy));
        FuturePayload();
        Action neutralize = () =>
        {
            policy.Generation++;
            policy.Neutralize = true;
            for (int port = 0; port < 2; port++)
            {
                _records.AsSpan(port * 0x100 + 0x84, 16).Clear();
                BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x84, 4), 0x80808080u);
                BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x90, 4), 0x80808080u);
                _records.AsSpan(port * 0x100 + 0x98, 4).Clear();
            }
        };
        Action terminal = () => Assert.Equal(0, _records[0x98]);
        NativeClock.PadAction = () =>
        {
            if (qolOuter) terminal();
            else { neutralize(); policy.Publish(terminal, neutralize: true); }
        };
        NativeClock.Advance = () =>
        {
            if (qolOuter) { neutralize(); policy.Publish(() => Publish(0x12345678), neutralize: true); }
            else Publish();
        };
        NativeClock.LoopAction = _ => _module.DeadlineClockWithoutExit();
        _module.DeadlineImage(10.05);
        int before = NativeClock.PadCalls;
        _module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(before + 1, NativeClock.PadCalls);
        Assert.Equal(0x80, _records[0x84]);
        Assert.Equal(0, _records[0x98]);
        Assert.Equal(0, _records[0x198]);
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    [Fact]
    public void Corrected60DiagnosticsBindParticipantsWithoutTakingTheirSimulation()
    {
        _module.RequestIndependent(false);
        Assert.True(_module.Preflight());
        _module.CaptureLoops(true);
        _module.ClearSyntheticCooperationBindingForTest();
        var parry = new Fahrenheit.Mods.Parry.ParryModule { Initialized = true };
        var qol = new Fahrenheit.Mods.Qol.FocusInputModule();
        var method = typeof(VrrModule).GetMethod("init_diagnostic_cooperation", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.CreateDelegate<Action<IEnumerable<object>>>(_module)(new object[] { parry, qol });
        Assert.False(parry.PreUpdate(1f / 60));
        int forwarded = 0;
        qol.Publish(() => forwarded++);
        Assert.Equal(1, forwarded);
        Assert.Equal(0, parry.Calls);
        Assert.Contains(_module.DiagnosticEvents, e => e.Kind == "qol_publication");
        Assert.Null(_module.CoopFailure);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("owner")]
    [InlineData("observation")]
    public void DeferredQolStagingRevalidatesBeforeTheFirstWrite(string boundary)
    {
        var policy = new Fahrenheit.Mods.Qol.FocusInputModule();
        Assert.True(BindReplay(policy));
        FuturePayload();
        NativeClock.PadAction = () =>
        {
            if (boundary == "epoch")
            {
                _module.ReadPadCandidates(double.NaN);
                Seed(3, 10.045, 10.046);
            }
            if (boundary == "owner") _module.NativeSync = true;
            if (boundary == "observation") Seed(3, 10.045, 10.046);
            policy.Publish(() =>
            {
                Assert.Equal(90, _records[0x84]);
                Assert.Equal(2, _records[0x98]);
            });
        };
        RunReplay(() => { });
        Assert.Equal(JsonValueKind.Null, Correlation().ValueKind);
    }

    private bool BindReplay(params object[] modules)
    {
        var method = typeof(VrrModule).GetMethod("bind_pad_input_policy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<IEnumerable<object>, bool>>(_module)(modules);
    }

    private void FuturePayload()
    {
        for (int port = 0; port < 2; port++)
        {
            _records[port * 0x100 + 0x84] = 90;
            _records[port * 0x100 + 0x98] = 2;
        }
        Seed(2, 10.04, 10.041);
    }

    private void RunReplay(Action consumers)
    {
        NativeClock.Advance = () => Publish();
        NativeClock.LoopAction = _ => { _module.DeadlineClockWithoutExit(); consumers(); };
        _module.DeadlineImage(10.05);
        _module.CallDiagnosticLoop(1f / 60);
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
    internal bool BootstrapClockMappedForTest => _simulationClock.HasMappedInterval;
    internal void ClearSyntheticCooperationBindingForTest()
    {
        _cooperativeBound = false;
        _cooperativeReady = null;
        _cooperativeTick = null;
    }
    internal void SeedPublicationCandidate(long sequence, int port, double start, double end, ReadOnlySpan<uint> words)
        => Assert.True(_padTimeline.Observe(sequence, port, (long)(start * Stopwatch.Frequency), (long)(end * Stopwatch.Frequency), words));
}
