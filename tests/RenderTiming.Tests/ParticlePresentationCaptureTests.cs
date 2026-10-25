using System.Buffers.Binary;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class ParticlePresentationCaptureTests
{
    [Fact]
    public void OnlyCompletedMatchingScopeQualifiesAndCallsKeepNativeOrder()
    {
        var capture = new ParticlePresentationCapture();
        Assert.False(capture.CanPresent(1, 9, 4));
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.Record(2, 5, 6, 1, ParticlePresentationKernel.DrawMatrix);
        Assert.False(capture.CanPresent(1, 9, 4));
        capture.CompleteRealRun();
        Assert.True(capture.CanPresent(1, 9, 4));
        Assert.False(capture.CanPresent(2, 9, 4));
        Assert.False(capture.CanPresent(1, 10, 4));
        Assert.False(capture.CanPresent(1, 9, 5));
        Assert.Equal(new nint[] { 3, 5 }, capture.Calls.Select(call => call.Data));
    }

    [Fact]
    public void AddressReuseWithinOneRunCannotResurrectAnOldCall()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Created(2);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        long oldGeneration = capture.Calls[0].Generation;
        capture.Deleted(2);
        capture.Created(2);
        Assert.False(capture.StillOwns(2, oldGeneration));
        capture.CompleteRealRun();
        Assert.Empty(capture.Calls);
        Assert.False(capture.CanPresent(1, 9, 4));
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        Assert.NotEqual(oldGeneration, capture.Calls[0].Generation);
    }

    [Fact]
    public void DeletionDropsOnlyDeadObjectsUnderPostSimulationLifetime()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.Record(5, 6, 7, 0, ParticlePresentationKernel.MatrixXyz);
        capture.Deleted(2);
        capture.CompleteRealRun();
        Assert.True(capture.CanPresent(1, 9, 4));
        Assert.Equal((nint)5, Assert.Single(capture.Calls).Object);
    }

    [Fact]
    public void LoopResetRejectsEntireEpochEvenIfLaterCallbacksWereCaptured()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.LoopReset(2);
        capture.Record(5, 6, 7, 0, ParticlePresentationKernel.MatrixXyz);
        capture.CompleteRealRun();
        Assert.False(capture.CanPresent(1, 9, 4));
    }

    [Fact]
    public void LoopResetOfAnUncapturedObjectStillRejectsTheWholeEpoch()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.LoopReset(99);
        capture.CompleteRealRun();
        Assert.False(capture.CanPresent(1, 9, 4));
    }
    [Fact]
    public void ResetAfterCompletionRevokesAnAlreadyQualifiedEpoch()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.CompleteRealRun();
        Assert.True(capture.CanPresent(1, 9, 4));
        capture.LoopReset(2);
        Assert.False(capture.CanPresent(1, 9, 4));
    }
    [Fact]
    public void NewRunAndExplicitInvalidationExpireOldVisuals()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.CompleteRealRun();
        capture.BeginRealRun(1, 9, 4);
        capture.CompleteRealRun();
        Assert.False(capture.CanPresent(1, 9, 4));
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        capture.CompleteRealRun();
        capture.Invalidate();
        Assert.False(capture.CanPresent(1, 9, 4));
        Assert.Empty(capture.Calls);
    }

    [Fact]
    public void UnsupportedKernelCannotEnterReplay()
    {
        var capture = new ParticlePresentationCapture();
        capture.BeginRealRun(1, 9, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => capture.Record(2, 3, 4, 0, (ParticlePresentationKernel)0x40b7d0));
        capture.CompleteRealRun();
        Assert.False(capture.CanPresent(1, 9, 4));
    }

    [Fact]
    public void CaptureOverflowRejectsTheWholeEpoch()
    {
        var capture = new ParticlePresentationCapture(1);
        capture.BeginRealRun(1, 9, 4);
        capture.Record(2, 3, 4, 0, ParticlePresentationKernel.MatrixXyz);
        Assert.Throws<InvalidOperationException>(() => capture.Record(2, 5, 6, 1, ParticlePresentationKernel.DrawMatrix));
        capture.CompleteRealRun();
        Assert.False(capture.CanPresent(1, 9, 4));
        Assert.Single(capture.Calls);
    }

    [Fact]
    public void LostOwnerIdentityDoesNotRestoreIntoRecycledStorage()
    {
        byte[] owner = new byte[0xb3];
        bool stillOwned = true;
        bool restored = ParticlePresentationOwnerScope.Run(owner, 0x1234, () => stillOwned, () =>
        {
            owner.AsSpan().Fill(0xa5);
            stillOwned = false;
        });
        Assert.False(restored);
        Assert.All(owner, value => Assert.Equal((byte)0xa5, value));
    }
    [Fact]
    public void OwnerScopeRestoresCanonicalControlBytesWhenProducerThrows()
    {
        byte[] owner = Enumerable.Range(0, 0xb3).Select(i => (byte)i).ToArray();
        byte[] before = (byte[])owner.Clone();
        Assert.Throws<InvalidOperationException>(() => ParticlePresentationOwnerScope.Run(owner, 0x1234, () => true, () =>
        {
            Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(owner));
            Assert.All(owner[4..20], value => Assert.Equal((byte)0, value));
            Assert.Equal(0, owner[0x1f]);
            Assert.Equal((byte)(before[0xb2] | 6), owner[0xb2]);
            throw new InvalidOperationException();
        }));
        Assert.Equal(before, owner);
    }

    [Fact]
    public void InvalidOwnerScopeWritesNothingAndDoesNotInvokeProducer()
    {
        byte[] owner = Enumerable.Range(0, 0xb2).Select(i => (byte)i).ToArray();
        byte[] before = (byte[])owner.Clone();
        bool invoked = false;
        Assert.Throws<ArgumentException>(() => ParticlePresentationOwnerScope.Run(owner, 0x1234, () => true, () => invoked = true));
        Assert.Equal(before, owner);
        Assert.False(invoked);
        Assert.Throws<ArgumentOutOfRangeException>(() => ParticlePresentationOwnerScope.Run(new byte[0xb3], 0, () => true, () => invoked = true));
        Assert.False(invoked);
    }
}
