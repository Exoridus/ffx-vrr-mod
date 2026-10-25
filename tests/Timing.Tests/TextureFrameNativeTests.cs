using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class TextureFrameNativeTests
{
    private static VrrModule CreateModule()
    {
        var module = new VrrModule();
        typeof(VrrModule).GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(module, new VrrConfig { TextureFrameInterpolation = true });
        return module;
    }

    private static void Prepare(TextureClockMemory data, int selector = 5)
    {
        RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.BeginImage(1d / 60);
        RenderPassContext.BeginSimulation();
        *(int*)(data.Slot + 8) = 3;
        *(int*)(data.Descriptor + 8) = 0x40;
        *(int*)(data.Descriptor + 0x68) = 0x200;
        *(short*)(data.Descriptor + 0x6c) = 8;
        for (int i = 0; i < 8; i++) *(short*)(data.Descriptor + 0x200 + i * 4) = 2;
        *(short*)(data.Record + 0x1c) = (short)selector;
    }

    [Fact]
    public void Native_sixty_installs_shared_draw_and_lifetime_hooks_without_UV_presentation()
    {
        using var fixture = new TimingTests();
        var module = CreateModule();
        Convert.FromBase64String("VYvsgeyEAAAAodgTxgAzxYlF/ItFCItNDItVEFNWjTQ=").CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x37f360), 32));
        byte* step = FhUtil.ptr_at<byte>(0x37f3e3);
        new byte[] { 0x0f, 0xbe, 0x86 }.CopyTo(new Span<byte>(step, 3));
        *(uint*)(step + 3) = (uint)(nuint)FhUtil.ptr_at<byte>(0x1f1000e);
        new byte[] { 0x55, 0x8b, 0xec, 0x81, 0xec, 0x08, 0x03, 0, 0 }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x244150), 9));
        new byte[] { 0x55, 0x8b, 0xec, 0x81, 0xec, 0x20, 0x04, 0, 0 }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x42ed50), 9));
        new byte[] { 0x55, 0x8b, 0xec, 0x83, 0xec, 0x10, 0x8b, 0x45, 0x08 }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x37f9c0), 9));
        new byte[] { 0x55, 0x8b, 0xec, 0x56, 0x8b, 0x75, 0x08, 0x85, 0xf6 }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x37c770), 9));
        Assert.True(module.InitializeTextureClockForTest());
        Assert.True(module.FramePhaseBoundForTest);
        Assert.False(module.UvPresentationBoundForTest);
    }

    [Fact]
    public void Actual_native_record_phase_uses_random_hold_and_preserves_canonical_bytes()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        *(int*)(data.Record + 4) = 7;
        *(short*)(data.Record + 0x14) = 2;
        *(short*)(data.Record + 0x16) = 1;
        byte[] work = new ReadOnlySpan<byte>(data.Record - 8, 0xd4c).ToArray();
        byte[] descriptor = new ReadOnlySpan<byte>(data.Descriptor, 0x240).ToArray();
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.Equal(new TextureFrameBlend(7, 0, 2.5f / 3), module.QueryFrameForTest(7));
        Assert.Equal(work, new ReadOnlySpan<byte>(data.Record - 8, 0xd4c).ToArray());
        Assert.Equal(descriptor, new ReadOnlySpan<byte>(data.Descriptor, 0x240).ToArray());
    }

    [Fact]
    public void Observed_w001_selector_zero_uses_the_native_default_sequence_threshold()
    {
        using var data = new TextureClockMemory();
        Prepare(data, 0);
        *(short*)(data.Record + 0x14) = 1;
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.Equal(new TextureFrameBlend(0, 1, 0.75f), module.QueryFrameForTest(0));
    }

    [Fact]
    public void Selector_three_uses_strict_threshold_period()
    {
        using var data = new TextureClockMemory();
        Prepare(data, 3);
        *(short*)(data.Record + 0x14) = 2;
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.Equal(new TextureFrameBlend(0, 1, 2.5f / 3), module.QueryFrameForTest(0));
    }

    [Theory]
    [InlineData(2, 1, 8, true)]
    [InlineData(5, -1, 8, true)]
    [InlineData(5, 2, 8, true)]
    [InlineData(5, 1, 7, true)]
    [InlineData(5, 1, 8, false)]
    public void Unsupported_selector_step_bank_or_model_is_left_native(int selector, int step, int count, bool target)
    {
        using var data = new TextureClockMemory();
        Prepare(data, selector);
        data.Slot[0xe] = unchecked((byte)step);
        *(short*)(data.Descriptor + 0x6c) = (short)count;
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5, target);
        module.PublishFrameForTest();
        Assert.Null(module.QueryFrameForTest(0));
    }

    [Fact]
    public void Paused_native_timer_holds_the_previously_captured_fraction()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        RenderPassContext.BeginSimulation();
        data.Slot[0xe] = 0;
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0);
        module.PublishFrameForTest();
        Assert.Equal(new TextureFrameBlend(0, 1, 0.25f), module.QueryFrameForTest(0));
    }

    [Fact]
    public void Epoch_and_same_address_lifecycle_invalidate_published_phase()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.NotNull(module.QueryFrameForTest(0));
        RenderPassContext.ResetHistory();
        Assert.Null(module.QueryFrameForTest(0));
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        module.InvalidateUvForTest(0);
        Assert.Null(module.QueryFrameForTest(0));
    }

    [Fact]
    public void Same_native_frame_with_distinct_actor_phase_is_ambiguous()
    {
        using var first = new TextureClockMemory();
        using var second = new TextureClockMemory();
        Prepare(first);
        Prepare(second);
        *(short*)(second.Record + 0x14) = 1;
        var module = CreateModule();
        module.CaptureFrameForTest(first.Slot, first.Shadow, 0.5);
        module.CaptureFrameForTest(second.Slot, second.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.Null(module.QueryFrameForTest(0));
    }

    [Fact]
    public void Published_availability_matches_image_epoch_and_lifecycle()
    {
        using var data = new TextureClockMemory();
        Prepare(data, 0);
        var module = CreateModule();
        Assert.False(module.AvailableFrameForTest());
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        Assert.False(module.AvailableFrameForTest());
        module.PublishFrameForTest();
        Assert.True(module.AvailableFrameForTest());
        RenderPassContext.BeginImage(1d / 144);
        Assert.False(module.AvailableFrameForTest());
        module.PublishFrameForTest();
        Assert.True(module.AvailableFrameForTest());
        RenderPassContext.ResetHistory();
        Assert.False(module.AvailableFrameForTest());
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.True(module.AvailableFrameForTest());
        module.InvalidateUvForTest(0);
        Assert.False(module.AvailableFrameForTest());
    }

    [Fact]
    public void Availability_returns_without_waiting_for_phase_lock()
    {
        using var data = new TextureClockMemory();
        Prepare(data, 0);
        var module = CreateModule();
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        module.PublishFrameForTest();
        module.WithPhaseLockForTest(() =>
        {
            var query = Task.Run(module.AvailableFrameForTest);
            Assert.True(query.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(query.Result);
        });
    }

    [Fact]
    public void Only_ambiguous_published_phases_are_unavailable()
    {
        using var first = new TextureClockMemory();
        using var second = new TextureClockMemory();
        Prepare(first, 0);
        Prepare(second, 0);
        *(short*)(second.Record + 0x14) = 1;
        var module = CreateModule();
        module.CaptureFrameForTest(first.Slot, first.Shadow, 0.5);
        module.CaptureFrameForTest(second.Slot, second.Shadow, 0.5);
        module.PublishFrameForTest();
        Assert.False(module.AvailableFrameForTest());
    }

    [Fact]
    public void Vrr_publication_crossing_boundary_returns_previous_first_frame()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = CreateModule();
        *(int*)(data.Record + 4) = 7;
        *(short*)(data.Record + 0x14) = 1;
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0.5);
        RenderPassContext.BeginSimulation();
        *(int*)(data.Record + 4) = 0;
        *(short*)(data.Record + 0x14) = 0;
        module.CaptureFrameForTest(data.Slot, data.Shadow, 0);
        RenderPassContext.IsActive = true;
        RenderPassContext.SetPass(0.5f, true);
        module.PublishFrameForTest();
        Assert.Equal(new TextureFrameBlend(7, 0, 0.875f), module.QueryFrameForTest(0));
        RenderPassContext.IsActive = false;
    }
}

public unsafe sealed partial class VrrModule
{
    private void invalidate_texture_frame_gpu() { }
    internal bool AvailableFrameForTest() => texture_frame_blend_available();
    internal void WithPhaseLockForTest(Action action) { lock (_textureUvLock) action(); }
    internal bool FramePhaseBoundForTest => _textureFramePhaseBound;
    internal bool UvPresentationBoundForTest => _texturePresentationBound;
    internal void CaptureFrameForTest(byte* slot, byte* renderer, double fraction, bool target = true)
        => capture_texture_frame_phase_data(slot, 0, (uint)(nuint)renderer, target, fraction);
    internal void PublishFrameForTest() => publish_texture_frame_phase(_ => true);
    internal TextureFrameBlend? QueryFrameForTest(uint nativeFrame)
        => try_texture_frame_blend(nativeFrame, out uint first, out uint next, out float alpha) ? new(first, next, alpha) : null;
}
