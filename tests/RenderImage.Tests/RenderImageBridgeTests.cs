using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class RenderImageBridgeTests
{
    [Fact]
    public void Initializes_whole_guarded_group_without_resolving_originals()
    {
        using var f=new BridgeFixture();
        f.Init();
        Assert.Equal(4,BridgeNative.Callbacks.Count);
        Assert.Equal(0,BridgeNative.Resolutions);
        Assert.False(f.Module.AdmitReady());
    }

    [Theory]
    [InlineData(0x227940)] [InlineData(0x1b6af0)] [InlineData(0x214c00)]
    [InlineData(0x193760)] [InlineData(0x70d9a8)] [InlineData(0x736400)]
    [InlineData(0x21516e)] [InlineData(0x214d86)]
    public void A_bad_binding_refuses_before_installing_any_hook(int rva)
    {
        using var f=new BridgeFixture();
        f.Bytes(rva,[0]);
        Assert.False(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
    }

    [Fact]
    public void Unsupported_or_disabled_modes_install_no_hooks()
    {
        using var f=new BridgeFixture();
        NativeBuild.Supported=false;
        Assert.False(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
        f.Module.Disable();
        Assert.True(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
    }

    [Fact]
    public void Image_scope_covers_render_after_animation_and_ends_with_frame()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=>
        {
            Assert.False(f.Module.AdmitReady());
            f.Admit();
            f.Flip();
            f.Module.Refuse("late_worker");
            BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); return 0xdeadbe01; };
            Assert.Equal(0xdeadbe01u,f.Action());
            return 0x12345678;
        };
        Assert.Equal(0x12345678u,f.Frame());
        Assert.False(f.Module.AdmitReady());
        Assert.Equal(1,BridgeNative.Actions);
    }

    [Fact]
    public void Consumer_can_execute_before_producer_returns_and_restores_scope()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=> { f.Admit(); return f.Flip(); };
        BridgeNative.OnFlip=_=> { f.Slot(); Assert.Equal(0xaabbcc01u,f.Action()); return 0; };
        BridgeNative.OnAction=_=> { Assert.False(f.Module.Reject()); return 0xaabbcc01; };
        f.Frame();
        Assert.Null(f.Module.Failure);
        Assert.True(f.Module.Reject());
        Assert.Equal("render_image_missing_action",f.Module.Failure);
    }

    [Fact]
    public void Pending_image_cannot_be_rehabilitated_by_a_later_cpu_frame()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=> { f.Admit(); return f.Flip(); };
        f.Frame();
        BridgeNative.OnFrame=_=> { f.Module.Refuse("later_frame_fault"); return 0; };
        f.Frame();
        BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); return 0; };
        f.Action();
        BridgeNative.OnFrame=_=>f.Flip();
        f.Frame();
        BridgeNative.OnAction=_=> { Assert.False(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Equal("later_frame_fault",f.Module.Failure);
    }

    [Fact]
    public void Native_loading_flip_and_null_window_still_consume_an_identity()
    {
        using var f=new BridgeFixture(); f.Init();
        f.Flip();
        Assert.Equal(0xaabbcc01u,f.Action());
        BridgeNative.OnFrame=_=> { f.Admit(); f.Flip(); f.Module.Refuse("fault"); return 0; };
        f.Frame();
        BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Equal(2,BridgeNative.Actions);
    }

    [Fact]
    public void Allocation_failure_after_slot_refuses_without_inventing_an_action()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFlip=_=> { f.Slot(); return 0x0d; };
        Assert.Equal(0x0du,f.Flip());
        Assert.NotNull(f.Module.Failure);
        Assert.Equal(1,BridgeNative.Slots);
        Assert.Equal(0,BridgeNative.Actions);
    }

    [Fact]
    public void Refusal_before_slot_does_not_shift_the_next_native_identity()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFlip=_=>0x16;
        Assert.Equal(0x16u,f.Flip());
        Assert.Null(f.Module.Failure);
        BridgeNative.OnFlip=_=> { f.Slot(); return 0; };
        f.Flip();
        BridgeNative.OnAction=_=> { Assert.False(f.Module.Reject()); return 0; };
        f.Action();
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_or_duplicate_slot_is_a_permanent_admission_failure(bool duplicate)
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFlip=_=> { if(duplicate){f.Slot();f.Slot();} return 0; };
        f.Flip();
        Assert.NotNull(f.Module.Failure);
        if(duplicate)
        {
            BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); return 0; };
            f.Action();
        }
    }

    [Fact]
    public void Unknown_action_is_held_but_native_action_still_runs()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); Assert.False(f.Module.Reject(1)); return 0xfe000001; };
        Assert.Equal(0xfe000001u,f.Action());
        Assert.NotNull(f.Module.Failure);
        Assert.Equal(1,BridgeNative.Actions);
    }

    [Fact]
    public void Present_outside_actions_is_native_until_an_image_is_actually_admitted()
    {
        using var f=new BridgeFixture(); f.Init();
        Assert.False(f.Module.Reject());
        BridgeNative.OnFrame=_=> { f.Module.Animate(f.Application); Assert.True(f.Module.AdmitReady()); return 0; };
        f.Frame();
        Assert.False(f.Module.Reject());
        BridgeNative.OnFrame=_=> { f.Admit(); return 0; };
        f.Frame();
        Assert.True(f.Module.Reject());
        Assert.False(f.Module.Reject(1));
    }

    [Fact]
    public void Presenter_loss_and_wrong_animation_application_block_admission()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=>
        {
            f.Module.Animate(f.Application);
            f.Module.Presenter(false);
            Assert.False(f.Module.AdmitReady());
            f.Module.Presenter(true);
            f.Module.Animate(f.Application+4);
            Assert.False(f.Module.AdmitReady());
            return 0;
        };
        f.Frame();
        Assert.NotNull(f.Module.Failure);
    }

    [Fact]
    public void Nested_frames_refuse_and_restore_the_outer_scope()
    {
        using var f=new BridgeFixture(); f.Init();
        int depth=0;
        BridgeNative.OnFrame=_=>
        {
            if(depth++==0){f.Admit();f.Frame();Assert.False(f.Module.AdmitReady());}
            return 0;
        };
        f.Frame();
        Assert.NotNull(f.Module.Failure);
        Assert.False(f.Module.AdmitReady());
    }

    [Fact]
    public void Native_exceptions_restore_frame_and_consumer_scopes()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=> { f.Admit(); f.Flip(); throw new InvalidOperationException("native frame"); };
        Assert.Throws<InvalidOperationException>(()=>f.Frame());
        Assert.False(f.Module.AdmitReady());
        BridgeNative.OnAction=_=>throw new InvalidOperationException("native action");
        Assert.Throws<InvalidOperationException>(()=>f.Action());
        Assert.True(f.Module.Reject());
    }
    [Fact]
    public void Thiscall_native_original_preserves_the_this_pointer_and_full_EAX()
    {
        using var f=new BridgeFixture(); f.Init();
        f.Flip();
        BridgeNative.NativeActionOriginal=true;
        Assert.Equal(0xaabbbb77u,f.Action());
        Assert.Equal(1,BridgeNative.Actions);
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void Publication_follows_native_credit_acquisition_not_producer_entry_order()
    {
        using var f=new BridgeFixture(); f.Init();
        using var firstEntered=new ManualResetEventSlim();
        using var firstMayAcquire=new ManualResetEventSlim();
        using var credit=new SemaphoreSlim(1,1);
        int requested=0;
        BridgeNative.OnSlot=_=>
        {
            if(Interlocked.Increment(ref requested)==1)
            {
                firstEntered.Set();
                Assert.True(firstMayAcquire.Wait(5000));
            }
            Assert.True(credit.Wait(5000));
            return 0;
        };
        var consumed=new List<long?>();
        BridgeNative.OnAction=_=> { consumed.Add(f.Module.CurrentImage); return 0; };
        using var firstDone=new ManualResetEventSlim();
        using var secondDone=new ManualResetEventSlim();
        Exception? firstFailure=null, secondFailure=null;
        var first=new Thread(()=>
        {
            try { f.Flip(); } catch(Exception error) { firstFailure=error; }
            finally { firstDone.Set(); }
        }) { IsBackground=true };
        var second=new Thread(()=>
        {
            try { f.Flip(); } catch(Exception error) { secondFailure=error; }
            finally { secondDone.Set(); }
        }) { IsBackground=true };
        first.Start();
        try
        {
            Assert.True(firstEntered.Wait(5000));
            second.Start();
            Assert.True(secondDone.Wait(5000));
            Assert.Null(secondFailure);
            f.Action();
            credit.Release();
            firstMayAcquire.Set();
            Assert.True(firstDone.Wait(5000));
            Assert.Null(firstFailure);
            f.Action();
            Assert.Equal(new long?[]{2,1},consumed);
            Assert.Null(f.Module.Failure);
        }
        finally
        {
            firstMayAcquire.Set();
            first.Join(6000);
            if(second.ThreadState != ThreadState.Unstarted) second.Join(6000);
        }
    }
    [Fact]
    public void A_foreign_frame_thread_remains_native_and_cannot_reopen_admission()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFrame=_=> { f.Admit(); return 0; };
        f.Frame();
        BridgeNative.OnFrame=_=> { f.Module.Animate(f.Application); Assert.False(f.Module.AdmitReady()); return 0; };
        Exception? failure=null;
        var thread=new Thread(()=> { try { f.Frame(); } catch(Exception error) { failure=error; } });
        thread.Start();
        Assert.True(thread.Join(5000));
        Assert.Null(failure);
        Assert.NotNull(f.Module.Failure);
    }

    [Fact]
    public void Failure_reported_after_action_consumption_cannot_be_treated_as_cancelable()
    {
        using var f=new BridgeFixture(); f.Init();
        BridgeNative.OnFlip=_=> { f.Slot(); f.Action(); return 0x0d; };
        Assert.Equal(0x0du,f.Flip());
        Assert.Equal("render_image_flip_failed_after_slot",f.Module.Failure);
        Assert.Equal(1,BridgeNative.Actions);
    }
    [Fact]
    public void Broken_association_cannot_accept_a_later_native_token()
    {
        using var f=new BridgeFixture(); f.Init();
        f.Action();
        Assert.NotNull(f.Module.Failure);
        f.Flip();
        BridgeNative.OnAction=_=> { Assert.True(f.Module.Reject()); Assert.False(f.Module.Reject(1)); return 0; };
        f.Action();
        Assert.True(f.Module.Reject());
    }

    [Fact]
    public void Original_action_exception_poison_is_permanent_after_scope_restoration()
    {
        using var f=new BridgeFixture(); f.Init();
        f.Flip();
        BridgeNative.OnAction=_=>throw new InvalidOperationException("action");
        Assert.Throws<InvalidOperationException>(()=>f.Action());
        Assert.NotNull(f.Module.Failure);
        Assert.True(f.Module.Reject());
    }
}
