using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class NativeImageEligibilityTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(257, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 128, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 2)]
    public void Native_ineligibility_blocks_admission_and_recovers_without_refusal(int initialized, int loading, int unreadable)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            SetFlags(f, initialized, loading, unreadable);
            Assert.False(f.Module.AdmitReady());
            Assert.Null(f.Module.Failure);
            SetFlags(f, 1, 0, 0);
            Assert.True(f.Module.AdmitReady());
            Assert.True(f.Module.MarkAdmitted());
            f.Module.AnimationEnd(f.Application); f.Ordinary();
            return f.Flip();
        };
        f.Frame();
        BridgeNative.OnAction = _ => { Assert.False(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 2)]
    public void Queued_independent_image_rechecks_native_flags_at_present(int initialized, int loading, int unreadable)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ => { f.Admit(); f.Module.AnimationEnd(f.Application); f.Ordinary(); return f.Flip(); };
        f.Frame();
        SetFlags(f, initialized, loading, unreadable);
        BridgeNative.OnAction = _ =>
        {
            Assert.True(f.Module.Reject());
            Assert.False(f.Module.Reject(1));
            return 0;
        };
        f.Action();
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 2)]
    public void Ineligible_flip_snapshot_survives_recovery_inside_original_flip(int initialized, int loading, int unreadable)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Admit(); f.Module.AnimationEnd(f.Application); f.Ordinary();
            SetFlags(f, initialized, loading, unreadable);
            return f.Flip();
        };
        BridgeNative.OnFlip = _ => { SetFlags(f, 1, 0, 0); f.Slot(); return 0; };
        f.Frame();
        BridgeNative.OnFrame = _ => { f.Module.Animate(f.Application); Assert.True(f.Module.AdmitReady()); return 0; };
        f.Frame();
        BridgeNative.OnAction = _ => { Assert.True(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 0, 2)]
    public void Later_unadmitted_native_image_passes_through_ineligible_flags(int initialized, int loading, int unreadable)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ => { f.Admit(); f.Module.AnimationEnd(f.Application); f.Ordinary(); return f.Flip(); };
        f.Frame();
        BridgeNative.OnAction = _ => { Assert.False(f.Module.Reject()); return 0; };
        f.Action();
        SetFlags(f, initialized, loading, unreadable);
        BridgeNative.OnFrame = _ => f.Flip();
        f.Frame();
        f.Action();
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Native_flip_before_admission_keeps_its_frozen_native_identity()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            f.Flip();
            Assert.True(f.Module.AdmitReady());
            f.Module.MarkAdmitted();
            SetFlags(f, 0, 1, 0);
            return f.Action();
        };
        BridgeNative.OnAction = _ => { Assert.False(f.Module.Reject()); return 0; };
        f.Frame();
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Flag_loss_between_decision_and_mark_keeps_the_image_native()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.True(f.Module.AdmitReady());
            SetFlags(f, 0, 1, 0);
            Assert.False(f.Module.MarkAdmitted());
            Assert.Null(f.Module.Failure);
            return f.Flip();
        };
        f.Frame();
        SetFlags(f, 1, 0, 0);
        BridgeNative.OnAction = _ => { Assert.False(f.Module.Reject()); return 0; };
        f.Action();
        BridgeNative.OnFrame = _ => { f.Module.Animate(f.Application); Assert.True(f.Module.AdmitReady()); return 0; };
        f.Frame();
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Producer_refusal_published_during_native_probe_is_visible_to_present()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ => { f.Admit(); f.Module.AnimationEnd(f.Application); f.Ordinary(); return f.Flip(); };
        f.Frame();
        MemoryProbe.CopyAllowed = address =>
        {
            if (address == FhUtil.Base + 0x8cb45c) f.Module.Refuse("producer_during_native_probe");
            return true;
        };
        BridgeNative.OnAction = _ => { Assert.True(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Equal("producer_during_native_probe", f.Module.Failure);
    }

    private static void SetFlags(BridgeFixture f, int initialized, int loading, int unreadable)
    {
        *(uint*)(FhUtil.Base + 0x8cb45c) = (uint)initialized;
        *(byte*)(f.Application + 0x3ad) = (byte)loading;
        nint denied = unreadable switch { 1 => FhUtil.Base + 0x8cb45c, 2 => f.Application + 0x3ad, _ => 0 };
        MemoryProbe.CopyAllowed = address => address != denied;
    }
}
