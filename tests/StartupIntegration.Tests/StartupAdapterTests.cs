using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class StartupAdapterTests
{
    [Fact]
    public void Cleanup_revokes_activation_and_holds_the_publication_gate_before_restoration()
    {
        using var image = new StartupImage();
        image.Prepare();
        Assert.True(image.InvokeCore());
        bool cleanupEntered = false;
        image.Module.CleanupObserved = () =>
        {
            cleanupEntered = true;
            Assert.False(image.Module.RuntimeEffectsArmed);
            Assert.True(image.Module.CleanupOwnsPublicationGate);
            Assert.True(image.Module.Initialized);
            Assert.True(image.Module.RenderReady);
        };
        try { image.Module.Cleanup(); }
        finally { image.Module.CleanupObserved = null; }
        Assert.True(cleanupEntered);
        AssertUnpublished(image.State);
    }

    [Fact]
    public void Published_typed_entry_forwards_native_verifies_own_links_then_publishes_both_journals()
    {
        using var image = new StartupImage();
        image.Prepare();
        Assert.Equal(4, IntPtr.Size);
        Assert.True(SyntheticSdk.ReceiptExistedBeforeInsertion);
        Assert.Equal(1, SyntheticSdk.Insertions);
        AssertUnpublished(image.State);

        image.Module.InvokePublished();

        Assert.Equal(1, image.NativeCalls);
        Assert.Equal(1, image.RequiredProbes);
        Assert.Equal(2, SyntheticSdk.Lookups);
        Assert.Equal(1, SyntheticSdk.DefaultLocationLookups);
        Assert.Equal(["startup lookup", "native", "required probe", "startup lookup", "startup_armed"], image.Trace.Select(item => item.Stage));
        foreach (var observation in image.Trace.Take(4)) AssertUnpublished(observation.State);
        AssertPublished(image.Trace.Last().State);
        AssertPublished(image.State);
    }

    [Fact]
    public void Render_readiness_remains_the_prepared_value_after_activation()
    {
        using var image = new StartupImage();
        image.Prepare(renderReady: false);
        Assert.True(image.InvokeCore());
        Assert.True(image.Module.Initialized);
        Assert.True(image.Module.RuntimeEffectsArmed);
        Assert.False(image.Module.RenderReady);
        Assert.Equal(new byte[] { 0xA1, 0xA2 }, image.State.General);
        Assert.Equal(new byte[] { 0xB1, 0xB2 }, image.State.Water);
    }

    [Theory]
    [InlineData("caller opcode")]
    [InlineData("stack word")]
    [InlineData("unreadable caller")]
    [InlineData("unreadable slot")]
    [InlineData("original entry return")]
    public void Unmatched_caller_forwards_native_without_consuming_prepared_activation(string mismatch)
    {
        using var image = new StartupImage();
        image.Prepare();
        if (mismatch == "caller opcode") image.BreakCaller();
        if (mismatch == "stack word") image.SetStackReturn(image.ReturnAddress + 1);
        if (mismatch == "unreadable caller") image.SetCallerReadable(false);
        if (mismatch == "unreadable slot") image.SetSlotReadable(false);
        nuint address = mismatch == "original entry return" ? (nuint)FhUtil.ImageBase + 0x5493c6u : image.ReturnAddress;

        Assert.False(image.Module.InvokeCore(address, image.ReturnSlot, image.RunNative));

        Assert.Equal(1, image.NativeCalls);
        Assert.Equal(0, image.RequiredProbes);
        Assert.Equal(0, SyntheticSdk.Lookups);
        AssertUnpublished(image.State);
        image.SetCallerReadable(true);
        image.SetSlotReadable(true);
        image.RestoreCaller();
        image.SetStackReturn(image.ReturnAddress);
        Assert.True(image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        AssertPublished(image.State);
    }

    [Fact]
    public void Foreign_native_thread_forwards_without_consuming_the_owner_attempt()
    {
        using var image = new StartupImage();
        image.Prepare();
        bool result = true;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = image.InvokeCore(); }
            catch (Exception failure) { error = failure; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(error);
        Assert.False(result);
        Assert.Equal(1, image.NativeCalls);
        Assert.Equal(0, image.RequiredProbes);
        Assert.Equal(0, SyntheticSdk.Lookups);
        AssertUnpublished(image.State);
        Assert.True(image.InvokeCore());
        AssertPublished(image.State);
    }

    [Theory]
    [InlineData("required false")]
    [InlineData("required missing")]
    [InlineData("startup link missing")]
    public void Missing_required_evidence_refuses_before_publishing_any_patch(string missing)
    {
        using var image = new StartupImage();
        image.Prepare(missing == "required false" ? RequiredReceipt.False
            : missing == "required missing" ? RequiredReceipt.Missing : RequiredReceipt.Available);
        if (missing == "startup link missing") SyntheticSdk.Available = false;

        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());

        Assert.Equal(1, image.NativeCalls);
        AssertUnpublished(image.State);
        int lookups = SyntheticSdk.Lookups;
        int probes = image.RequiredProbes;
        SyntheticSdk.Available = true;
        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        Assert.Equal(lookups, SyntheticSdk.Lookups);
        Assert.Equal(probes, image.RequiredProbes);
        AssertUnpublished(image.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_baseline_in_either_journal_refuses_before_the_first_write(bool staleWater)
    {
        using var image = new StartupImage();
        image.Prepare();
        image.Write(staleWater ? StartupImage.WaterRva : StartupImage.GeneralRva, [0xEE, 0xEF]);

        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());

        Assert.Equal(1, image.NativeCalls);
        Assert.Equal(1, image.RequiredProbes);
        Assert.Equal(staleWater ? new byte[] { 0x11, 0x12 } : [0xEE, 0xEF], image.State.General);
        Assert.Equal(staleWater ? new byte[] { 0xEE, 0xEF } : [0x21, 0x22], image.State.Water);
        Assert.False(image.Module.Initialized);
        Assert.False(image.Module.RenderReady);
        Assert.False(image.Module.RuntimeEffectsArmed);
    }

    [Fact]
    public void Cross_journal_overlap_is_refused_without_any_patch_write()
    {
        using var image = new StartupImage();
        image.Prepare(overlap: true);
        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());
        Assert.Equal(1, image.NativeCalls);
        AssertUnpublished(image.State);
    }

    [Fact]
    public void Native_exception_is_preserved_without_verifying_applying_or_retrying_activation()
    {
        using var image = new StartupImage();
        image.Prepare();
        var expected = new InvalidOperationException("original native failure");
        image.DuringNative = () => throw expected;
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => image.InvokeCore()));
        Assert.Equal(1, image.NativeCalls);
        Assert.Equal(0, image.RequiredProbes);
        Assert.Equal(0, SyntheticSdk.Lookups);
        AssertUnpublished(image.State);
        image.DuringNative = null;
        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        Assert.Equal(0, image.RequiredProbes);
        Assert.Equal(0, SyntheticSdk.Lookups);
        AssertUnpublished(image.State);
    }

    [Fact]
    public void Repeated_valid_call_forwards_native_without_reverification_or_reapplying_patches()
    {
        using var image = new StartupImage();
        image.Prepare();
        Assert.True(image.InvokeCore());
        int lookups = SyntheticSdk.Lookups;
        image.Write(StartupImage.GeneralRva, [0xC1, 0xC2]);
        image.Write(StartupImage.WaterRva, [0xD1, 0xD2]);
        Assert.False(image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        Assert.Equal(1, image.RequiredProbes);
        Assert.Equal(lookups, SyntheticSdk.Lookups);
        Assert.Equal(new byte[] { 0xC1, 0xC2 }, image.State.General);
        Assert.Equal(new byte[] { 0xD1, 0xD2 }, image.State.Water);
        Assert.True(image.Module.RuntimeEffectsArmed);
        image.Write(StartupImage.GeneralRva, [0xA1, 0xA2]);
        image.Write(StartupImage.WaterRva, [0xB1, 0xB2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_or_cleanup_before_the_call_discards_the_activation_opportunity(bool cleanup)
    {
        using var image = new StartupImage();
        image.Prepare();
        if (cleanup) image.Module.Cleanup(); else image.Module.Stop();
        Assert.False(image.InvokeCore());
        Assert.False(image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        Assert.Equal(0, image.RequiredProbes);
        AssertUnpublished(image.State);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("required probe")]
    public void Cleanup_during_an_owned_attempt_prevents_later_publication(string stage)
    {
        using var image = new StartupImage();
        image.Prepare();
        if (stage == "native") image.DuringNative = image.Module.Cleanup;
        else image.DuringRequiredProbe = image.Module.Cleanup;
        Assert.Throws<InvalidOperationException>(() => image.InvokeCore());
        Assert.Equal(1, image.NativeCalls);
        AssertUnpublished(image.State);
        image.DuringNative = null;
        image.DuringRequiredProbe = null;
        Assert.False(image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        AssertUnpublished(image.State);
    }

    [Fact]
    public void Cleanup_after_publication_revokes_activation_and_restores_both_native_baselines()
    {
        using var image = new StartupImage();
        image.Prepare();
        Assert.True(image.InvokeCore());
        image.Module.Cleanup();
        AssertUnpublished(image.State);
        Assert.False(image.InvokeCore());
        Assert.Equal(2, image.NativeCalls);
        AssertUnpublished(image.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsupported_cookie_entry_or_unavailable_bridge_refuses_preparation(bool unavailableBridge)
    {
        using var image = new StartupImage();
        if (unavailableBridge) NativeSnapshotBridge.Available = false;
        else image.BreakCookieEntry();
        Assert.False(image.Module.PrepareBridge());
        Assert.Equal(unavailableBridge ? 1 : 0, NativeSnapshotBridge.Creations);
        Assert.Equal(0, SyntheticSdk.Insertions);
        AssertUnpublished(image.State);
    }

    private static void AssertUnpublished(StartupState state)
    {
        Assert.Equal(new byte[] { 0x11, 0x12 }, state.General);
        Assert.Equal(new byte[] { 0x21, 0x22 }, state.Water);
        Assert.False(state.Initialized);
        Assert.False(state.RenderReady);
        Assert.False(state.Armed);
    }

    private static void AssertPublished(StartupState state)
    {
        Assert.Equal(new byte[] { 0xA1, 0xA2 }, state.General);
        Assert.Equal(new byte[] { 0xB1, 0xB2 }, state.Water);
        Assert.True(state.Initialized);
        Assert.True(state.RenderReady);
        Assert.True(state.Armed);
    }
}
