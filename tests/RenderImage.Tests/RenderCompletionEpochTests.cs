using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class RenderCompletionEpochTests
{
    [Fact]
    public void An_already_initialized_flag_does_not_authorize_an_unobserved_lifetime()
    {
        using var f = new BridgeFixture(); f.Init(false);
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.False(f.Module.AdmitReady());
            Assert.False(f.Module.MarkAdmitted());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Only_the_normal_return_of_observed_initialization_publishes_readiness()
    {
        using var f = new BridgeFixture(); f.Init(false);
        BridgeNative.OnInitialize = () =>
        {
            Assert.False(f.Module.AdmitReady());
            Assert.False(f.Module.MarkAdmitted());
            return 0;
        };
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.False(f.Module.AdmitReady());
            Assert.Equal(0u, f.Initialize());
            Assert.True(f.Module.AdmitReady());
            Assert.True(f.Module.MarkAdmitted());
            f.Module.AnimationEnd(f.Application);
            f.Ordinary();
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Equal(1, BridgeNative.InitializeCalls);
    }

    [Theory]
    [InlineData(0x12345678u, true, false)]
    [InlineData(0u, false, false)]
    [InlineData(0u, true, true)]
    public void Failed_or_throwing_initialization_never_reopens_observation(uint result, bool flag, bool throws)
    {
        using var f = new BridgeFixture(); f.Init(false);
        BridgeNative.OnInitialize = () =>
        {
            *(uint*)(FhUtil.Base + 0x8cb45c) = flag ? 1u : 0u;
            if (throws) throw new InvalidOperationException("initialize");
            return result;
        };
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            if (throws) Assert.Throws<InvalidOperationException>(() => f.Initialize());
            else Assert.Equal(result, f.Initialize());
            Assert.False(f.Module.AdmitReady());
            BridgeNative.OnInitialize = null;
            Assert.Equal(0u, f.Initialize());
            Assert.False(f.Module.AdmitReady());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
        Assert.Equal(2, BridgeNative.InitializeCalls);
    }

    [Fact]
    public void Successful_initialization_before_arming_cannot_activate_after_arming()
    {
        using var f = new BridgeFixture(); f.Module.ModeReady = false; f.Init(false);
        f.Initialize();
        f.Module.ModeReady = true;
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.False(f.Module.AdmitReady());
            f.Initialize();
            Assert.True(f.Module.AdmitReady());
            return 0;
        };
        f.Frame();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Every_lifecycle_entry_invalidates_before_its_original_and_holds_the_old_image(int kind)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            Complete(f);
            f.Flip();
            void InsideOriginal()
            {
                Assert.False(f.Module.AdmitReady());
                Assert.False(f.Module.MarkAdmitted());
                AssertOutput(f, true);
                Assert.Equal(1u, *(uint*)(FhUtil.Base + 0x8cb45c));
            }
            BridgeNative.OnInitialize = () => { InsideOriginal(); return 0x12345678; };
            BridgeNative.OnFinalize = () => { InsideOriginal(); return 0xabcdef01; };
            BridgeNative.OnReset = InsideOriginal;
            if (kind == 0) Assert.Equal(0x12345678u, f.Initialize());
            else if (kind == 1) Assert.Equal(0xabcdef01u, f.FinalizeGraphics());
            else f.ResetGraphics();
            Assert.False(f.Module.AdmitReady());
            BridgeNative.OnInitialize = null;
            f.Initialize();
            Assert.False(f.Module.AdmitReady());
            return 0;
        };
        f.Frame();
        Assert.Equal("render_image_graphics_lifecycle", f.Module.Failure);
        BridgeNative.OnFrame = _ => f.Flip();
        f.Frame();
        AssertOutput(f, false);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Pre_admission_finalize_or_reset_closes_without_poisoning_native_images(bool reset)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.True(f.Module.AdmitReady());
            f.Flip();
            if (reset) f.ResetGraphics(); else f.FinalizeGraphics();
            f.Initialize();
            Assert.False(f.Module.AdmitReady());
            return 0;
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Nested_initialization_cannot_publish_either_completion()
    {
        using var f = new BridgeFixture(); f.Init(false);
        int depth = 0;
        BridgeNative.OnInitialize = () =>
        {
            if (depth++ == 0) f.Initialize();
            Assert.False(f.Module.AdmitReady());
            return 0;
        };
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            f.Initialize();
            Assert.False(f.Module.AdmitReady());
            BridgeNative.OnInitialize = null;
            f.Initialize();
            Assert.False(f.Module.AdmitReady());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
        Assert.Equal(3, BridgeNative.InitializeCalls);
    }

    [Fact]
    public void Concurrent_lifecycle_callbacks_do_not_hold_the_gate_or_publish_readiness()
    {
        using var f = new BridgeFixture(); f.Init(false);
        BridgeNative.OnInitialize = () =>
        {
            Exception? failure = null;
            var other = new Thread(() =>
            {
                try { f.FinalizeGraphics(); }
                catch (Exception error) { failure = error; }
            });
            other.Start();
            Assert.True(other.Join(5000));
            Assert.Null(failure);
            return 0;
        };
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            f.Initialize();
            Assert.False(f.Module.AdmitReady());
            return 0;
        };
        f.Frame();
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Lifecycle_between_readiness_and_commit_leaves_actual_image_native()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.True(f.Module.AdmitReady());
            f.ResetGraphics();
            *(uint*)(FhUtil.Base + 0x8cb45c) = 1;
            Assert.False(f.Module.MarkAdmitted());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Lifecycle_during_native_admission_probes_cannot_commit_or_report_ready(bool commit)
    {
        using var f = new BridgeFixture(); f.Init();
        bool entered = false;
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            MemoryProbe.CopyAllowed = address =>
            {
                if (!entered && address == FhUtil.Base + 0x8cb45c)
                {
                    entered = true;
                    f.ResetGraphics();
                    *(uint*)(FhUtil.Base + 0x8cb45c) = 1;
                }
                return true;
            };
            Assert.False(commit ? f.Module.MarkAdmitted() : f.Module.AdmitReady());
            return f.Flip();
        };
        f.Frame();
        Assert.True(entered);
        AssertOutput(f, false);
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Lifecycle_during_present_probe_cannot_rehabilitate_the_same_pointer_and_flag()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ => { Complete(f); return f.Flip(); };
        f.Frame();
        bool entered = false;
        MemoryProbe.CopyAllowed = address =>
        {
            if (!entered && address == FhUtil.Base + 0x8cb45c)
            {
                entered = true;
                f.ResetGraphics();
                f.Initialize();
            }
            return true;
        };
        AssertOutput(f, true);
        Assert.True(entered);
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(0xdeadbeefu)]
    public void Any_normal_ordinary_return_after_animation_certifies_its_image(uint result)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnOrdinary = () => result;
        BridgeNative.OnFrame = _ =>
        {
            f.Admit(); f.Module.AnimationEnd(f.Application);
            Assert.Equal(result, f.Ordinary());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
        Assert.Equal(1, BridgeNative.OrdinaryCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Missing_ordinary_or_missing_normal_animation_completion_holds_image(int kind)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Admit();
            if (kind == 0) f.Module.AnimationEnd(f.Application);
            else
            {
                if (kind == 2) f.Module.AnimationEnd(f.Application, false);
                f.Ordinary();
                f.Module.AnimationEnd(f.Application);
            }
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, true);
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void A_flip_before_or_inside_ordinary_keeps_its_incomplete_snapshot(bool inside)
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Admit(); f.Module.AnimationEnd(f.Application);
            if (inside) BridgeNative.OnOrdinary = () => { f.Flip(); return 0; };
            else f.Flip();
            f.Ordinary();
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, true);
        AssertOutput(f, false);
    }

    [Fact]
    public void A_throwing_ordinary_call_clears_an_earlier_completion_even_when_caught()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            Complete(f);
            BridgeNative.OnOrdinary = () => throw new InvalidOperationException("ordinary");
            Assert.Throws<InvalidOperationException>(() => f.Ordinary());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, true);
        Assert.Null(f.Module.Failure);
        Assert.Equal(2, BridgeNative.OrdinaryCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Nested_ordinary_or_frame_scope_never_certifies_its_parent(bool nestedFrame)
    {
        using var f = new BridgeFixture(); f.Init();
        int depth = 0;
        BridgeNative.OnFrame = _ =>
        {
            if (depth != 0) return 0;
            f.Admit(); f.Module.AnimationEnd(f.Application);
            f.Ordinary();
            return f.Flip();
        };
        BridgeNative.OnOrdinary = () =>
        {
            if (depth++ == 0)
            {
                if (nestedFrame) f.Frame(); else f.Ordinary();
            }
            return 0x12345678;
        };
        f.Frame();
        AssertOutput(f, true);
        Assert.NotNull(f.Module.Failure);
    }

    [Fact]
    public void Foreign_ordinary_does_not_certify_the_producer_thread_scope()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Admit(); f.Module.AnimationEnd(f.Application);
            Exception? failure = null;
            var thread = new Thread(() => { try { f.Ordinary(); } catch (Exception error) { failure = error; } });
            thread.Start();
            Assert.True(thread.Join(5000));
            Assert.Null(failure);
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, true);
    }

    [Fact]
    public void The_render_consumer_does_not_need_to_be_the_lifecycle_owner_thread()
    {
        using var f = new BridgeFixture(); f.Init();
        BridgeNative.OnFrame = _ => { Complete(f); return f.Flip(); };
        f.Frame();
        Exception? failure = null;
        var thread = new Thread(() => { try { AssertOutput(f, false); } catch (Exception error) { failure = error; } });
        thread.Start();
        Assert.True(thread.Join(5000));
        Assert.Null(failure);
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Startup_initialization_on_a_foreign_thread_does_not_adopt_the_application_owner()
    {
        using var f = new BridgeFixture(); f.Init(false);
        var thread = new Thread(() => f.Initialize());
        thread.Start(); Assert.True(thread.Join(5000));
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            Assert.False(f.Module.AdmitReady());
            Assert.False(f.Module.MarkAdmitted());
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, false);
    }

    [Fact]
    public void Cdecl_originals_preserve_full_uint_and_void_abis()
    {
        using var f = new BridgeFixture(); f.Init(false);
        BridgeNative.NativeGraphicsOriginal = true;
        Assert.Equal(0xdeadbeefu, f.Ordinary());
        Assert.Equal(0xdeadbeefu, f.Initialize());
        Assert.Equal(0xdeadbeefu, f.FinalizeGraphics());
        f.ResetGraphics();
        Assert.Equal(3, BridgeNative.OrdinaryCalls);
        Assert.Equal(1, BridgeNative.ResetCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Ordinary_entry_cannot_adopt_animation_or_admission_completed_inside_its_original(bool initializeInside)
    {
        using var f = new BridgeFixture(); f.Init(!initializeInside);
        BridgeNative.OnFrame = _ =>
        {
            if (initializeInside)
            {
                f.Module.Animate(f.Application);
                f.Module.AnimationEnd(f.Application);
            }
            else f.Admit();
            BridgeNative.OnOrdinary = () =>
            {
                if (initializeInside) { f.Initialize(); Assert.True(f.Module.MarkAdmitted()); }
                else f.Module.AnimationEnd(f.Application);
                return 0;
            };
            f.Ordinary();
            return f.Flip();
        };
        f.Frame();
        AssertOutput(f, true);
    }

    [Fact]
    public void Native_probes_allow_a_lifecycle_callback_on_another_thread()
    {
        using var f = new BridgeFixture(); f.Init();
        bool entered = false;
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            MemoryProbe.CopyAllowed = address =>
            {
                if (!entered && address == FhUtil.Base + 0x8cb45c)
                {
                    entered = true;
                    Exception? failure = null;
                    var thread = new Thread(() => { try { f.ResetGraphics(); } catch (Exception error) { failure = error; } });
                    thread.Start();
                    Assert.True(thread.Join(5000));
                    Assert.Null(failure);
                }
                return true;
            };
            Assert.False(f.Module.MarkAdmitted());
            return 0;
        };
        f.Frame();
        Assert.True(entered);
    }
    private static void Complete(BridgeFixture f)
    {
        f.Admit();
        f.Module.AnimationEnd(f.Application);
        f.Ordinary();
    }

    private static void AssertOutput(BridgeFixture f, bool rejected)
    {
        BridgeNative.OnAction = _ => { Assert.Equal(rejected, f.Module.Reject()); return 0; };
        f.Action();
    }
}
