using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class ActorCompletionTests
{
    [Fact]
    public void Draw_owned_hierarchy_blends_after_completion_without_persistent_world_or_palette_loans()
    {
        using var f = new ActorDrawFixture();
        f.Construct();
        byte[] world = f.PersistentWorld, palette = f.PersistentPalette;
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(0, 0); f.Draw();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20);
        Assert.Equal(0xaabbccddu, f.Draw());
        Assert.Equal(5, f.Root.Translation.X);
        Assert.Equal(10, f.Bone.Translation.X);
        Assert.Equal(world, f.PersistentWorld); Assert.Equal(palette, f.PersistentPalette);
        f.Module.ActorDrawRestore(); f.Module.ActorDrawReset();
        Assert.Equal(world, f.PersistentWorld); Assert.Equal(palette, f.PersistentPalette);
    }

    [Fact]
    public void Extra_image_blends_fresh_draw_output_without_replaying_actor_setters()
    {
        using var f = Paired();
        f.Begin(simulation: false, alpha: .25f);
        f.Pose(10, 20); f.Draw();
        Assert.Equal(2.5f, f.Root.Translation.X);
        Assert.Equal(5, f.Bone.Translation.X);
        Assert.Equal(3, ActorDrawNative.HierarchyCalls);
        Assert.Equal(3, ActorDrawNative.DrawCalls);
        Assert.Equal(0, BridgeNative.ActorInstances);
    }

    [Fact]
    public void Unknown_preexisting_instance_stays_native_until_a_constructor_generation_is_observed()
    {
        using var f = new ActorDrawFixture();
        f.Begin(); f.Pose(0, 0); f.Draw();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X);
        f.Construct();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20); f.Draw();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(20, 40); f.Draw();
        Assert.Equal(15, f.Root.Translation.X);
        Assert.Equal(30, f.Bone.Translation.X);
    }

    [Fact]
    public void Destruction_and_same_address_reconstruction_cannot_reuse_old_pose_history()
    {
        using var f = Paired();
        Assert.Equal(0xdeadbeefu, f.Module.ActorDrawDestruct(f.Instance));
        f.Construct();
        f.Begin(); f.Pose(100, 200); f.Draw();
        Assert.Equal(100, f.Root.Translation.X);
        Assert.Equal(200, f.Bone.Translation.X);
        Assert.Equal(1, ActorDrawNative.Destructs);
        Assert.Equal(2, ActorDrawNative.Constructs);
    }

    [Theory]
    [InlineData("world")]
    [InlineData("palette")]
    [InlineData("instance")]
    [InlineData("mesh")]
    public void Output_aliases_are_forwarded_without_interpolating_shared_storage(string alias)
    {
        using var f = Paired();
        f.Begin(simulation: false, alpha: .25f);
        ActorDrawNative.Output = alias switch { "world" => f.World, "palette" => f.Palette, "instance" => f.Instance, _ => f.Mesh };
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X);
        Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Non_draw_hierarchy_call_does_not_borrow_the_previous_draw_scope()
    {
        using var f = Paired();
        f.Pose(10, 20);
        f.Module.ActorDrawHierarchy(f.Instance, f.Scratch + 64);
        Assert.Equal(10, f.Root.Translation.X);
        Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Native_hierarchy_exception_preserves_exception_and_balances_scope_for_later_native_forwarding()
    {
        using var f = Paired();
        var error = new InvalidOperationException("native hierarchy failed");
        ActorDrawNative.BeforeHierarchyReturn = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => f.Draw()));
        ActorDrawNative.BeforeHierarchyReturn = null;
        f.Pose(30, 60);
        f.Module.ActorDrawHierarchy(f.Instance, f.Scratch + 64);
        Assert.Equal(30, f.Root.Translation.X);
        Assert.Equal(60, f.Bone.Translation.X);
    }

    [Fact]
    public void Lifecycle_change_inside_hierarchy_leaves_the_new_native_output_unmodified()
    {
        using var f = Paired();
        f.Begin(simulation: false, alpha: .25f);
        ActorDrawNative.BeforeHierarchyReturn = () => Assert.Equal(0u, f.Bridge.Initialize());
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X);
        Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Native_stamp_and_skin_upload_order_are_unchanged_for_opaque_and_transparent_submissions()
    {
        using var f = Paired();
        ActorDrawNative.Order.Clear();
        int uploads = ActorDrawNative.Uploads;
        f.Begin(simulation: false); f.Pose(10, 20);
        f.Draw(); f.Draw();
        Assert.Equal(uploads + 1, ActorDrawNative.Uploads);
        Assert.Equal(new[] { "hierarchy", "stamp", "upload", "draw", "hierarchy", "draw" }, ActorDrawNative.Order);
    }

    [Fact]
    public void Draw_with_an_unmatched_old_stamp_cannot_borrow_a_new_image_tick()
    {
        using var f = Paired();
        uint oldStamp = ActorDrawNative.Stamp;
        f.Begin(); f.Pose(20, 40);
        ActorDrawNative.Stamp = oldStamp;
        f.Draw();
        Assert.Equal(20, f.Root.Translation.X);
        Assert.Equal(40, f.Bone.Translation.X);
    }

    [Fact]
    public void Worker_draw_after_completion_uses_the_saved_image_receipt_after_live_phase_is_cleared()
    {
        using var f = Paired();
        Exception? failure = null;
        var worker = new Thread(() => { try { f.Draw(); } catch (Exception error) { failure = error; } });
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(5))); Assert.Null(failure);
        Assert.False(RenderPassContext.IsActive); Assert.False(RenderPassContext.IsPresentation);
        Assert.Equal(5, f.Root.Translation.X); Assert.Equal(10, f.Bone.Translation.X);
    }

    [Fact]
    public void Uncompleted_simulation_pose_cannot_seed_later_history()
    {
        using var f = new ActorDrawFixture(); f.Construct();
        f.Begin(); f.Pose(0, 0); f.Draw();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Reset_during_native_hierarchy_invalidates_its_receipt_without_writing_persistent_storage()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        byte[] world = f.PersistentWorld, palette = f.PersistentPalette;
        ActorDrawNative.BeforeHierarchyReturn = f.Module.ActorDrawReset;
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
        Assert.Equal(world, f.PersistentWorld); Assert.Equal(palette, f.PersistentPalette);
    }

    [Fact]
    public void Same_address_constructor_during_native_hierarchy_invalidates_the_older_scope()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        ActorDrawNative.BeforeHierarchyReturn = f.Construct;
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Wrapping_persistent_palette_range_cannot_pass_the_output_alias_guard()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        *(uint*)(f.Instance + 12) = uint.MaxValue - 8;
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sampling_failure_cannot_skip_or_replace_the_native_hierarchy(bool afterNative)
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        Action failReads = () => MemoryProbe.CopyAllowed = _ => throw new InvalidOperationException("actor copy fixture");
        if (afterNative) ActorDrawNative.BeforeHierarchyReturn = failReads; else failReads();
        int calls = ActorDrawNative.HierarchyCalls;
        f.Pose(10, 20);
        Assert.Equal(0xaabbccddu, f.Draw());
        Assert.Equal(calls + 1, ActorDrawNative.HierarchyCalls);
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }
    [Fact]
    public void Same_stamp_without_an_image_or_action_owner_stays_native()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f); f.Pose(10, 20);
        f.Module.ActorDrawInvoke(f.Bridge.Renderer, f.Instance, 222, 333, unchecked((int)ActorDrawNative.Stamp));
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Foreign_renderer_with_a_matching_stamp_cannot_borrow_the_actions_receipt()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f); f.Pose(10, 20);
        nint foreign = f.Bridge.Renderer + 0x1000;
        f.Module.ActorDrawInAction(f.Bridge.Application,
            () => f.Module.ActorDrawInvoke(foreign, f.Instance, 222, 333, unchecked((int)ActorDrawNative.Stamp)));
        Assert.Equal(foreign, ActorDrawNative.LastRenderer);
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }
    [Fact]
    public void Scene_history_epoch_change_during_native_hierarchy_cannot_publish_old_interpolation()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        ActorDrawNative.BeforeHierarchyReturn = RenderPassContext.ResetHistory;
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }

    [Fact]
    public void Stalled_native_stamp_across_distinct_images_is_ambiguous_but_later_unique_stamps_recover()
    {
        using var f = Paired(); ActorDrawNative.Stamp--;
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X);
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(20, 40); f.Draw();
        Assert.Equal(20, f.Root.Translation.X);
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(30, 60); f.Draw();
        Assert.Equal(25, f.Root.Translation.X); Assert.Equal(50, f.Bone.Translation.X);
    }
    [Theory]
    [InlineData("construct")]
    [InlineData("destruct")]
    [InlineData("draw")]
    public void Disarmed_actor_callbacks_forward_without_touching_managed_actor_state(string callback)
    {
        using var f = Paired();
        int states = f.Module.ActorDrawTracked;
        long skipped = f.Module.ActorDrawSkipped;
        f.Module.StopSyntheticRuntimeForTest();
        if (callback == "construct") f.Construct();
        if (callback == "destruct") Assert.Equal(0xdeadbeefu, f.Module.ActorDrawDestruct(f.Instance));
        if (callback == "draw") f.Draw();
        Assert.Equal(states, f.Module.ActorDrawTracked);
        Assert.Equal(skipped, f.Module.ActorDrawSkipped);
    }

    [Fact]
    public void Disarm_inside_native_draw_prevents_later_hierarchy_substitution()
    {
        using var f = Paired(); f.Begin(simulation: false, alpha: .25f);
        ActorDrawNative.InDraw = f.Module.StopSyntheticRuntimeForTest;
        f.Pose(10, 20); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }
    [Fact]
    public void Production_frame_flip_slot_action_propagates_the_preincrement_stamp_to_the_worker_draw()
    {
        using var f = new ActorDrawFixture(); f.Construct();
        using var worker = new ActorActionWorker();
        for (int i = 0; i < 2; i++)
        {
            f.Pose(i * 10, i * 20);
            uint stamp = f.QueueImage();
            worker.Run(() =>
            {
                BridgeNative.OnAction = _ => { f.DrawQueued(stamp); return 1; };
                f.Bridge.Action();
            });
            Assert.Null(f.Module.Failure);
        }
        Assert.Equal(5, f.Root.Translation.X); Assert.Equal(10, f.Bone.Translation.X);
        Assert.Equal(2, BridgeNative.Frames); Assert.Equal(2, BridgeNative.Flips);
        Assert.Equal(2, BridgeNative.Slots); Assert.Equal(2, BridgeNative.Actions);
        Assert.False(RenderPassContext.IsActive); Assert.False(RenderPassContext.IsPresentation);
    }

    [Fact]
    public void Reset_after_newer_tick_invalidates_an_older_actions_already_cached_pose()
    {
        using var f = new ActorDrawFixture(); f.Construct();
        using var worker = new ActorActionWorker();
        f.Pose(0, 0); uint first = f.QueueImage();
        worker.Run(() => { BridgeNative.OnAction = _ => { f.DrawQueued(first); return 1; }; f.Bridge.Action(); });
        f.Pose(10, 20); uint second = f.QueueImage();
        using var cached = new ManualResetEventSlim(); using var reset = new ManualResetEventSlim();
        worker.Start(() =>
        {
            BridgeNative.OnAction = _ =>
            {
                f.DrawQueued(second); Assert.Equal(5, f.Root.Translation.X); cached.Set();
                Assert.True(reset.Wait(TimeSpan.FromSeconds(5)));
                f.DrawQueued(second); Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
                return 1;
            };
            f.Bridge.Action();
        });
        try
        {
            Assert.True(cached.Wait(TimeSpan.FromSeconds(5)));
            f.QueueImage(); f.Module.ActorDrawReset();
        }
        finally { reset.Set(); }
        worker.Finish();
    }
    [Fact]
    public void Completion_for_a_different_simulation_cannot_promote_an_older_unfinished_pose()
    {
        using var f = new ActorDrawFixture(); f.Construct();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(0, 0); f.Draw();
        f.Begin(); f.Pose(10, 20); f.Draw();
        RenderPassContext.BeginSimulation(); f.Module.ActorDrawComplete();
        f.Begin(simulation: false, alpha: .25f); f.Draw();
        Assert.Equal(10, f.Root.Translation.X); Assert.Equal(20, f.Bone.Translation.X);
    }
    [Theory]
    [InlineData(null, true, true, 60, false, false)]
    [InlineData("1", false, true, 60, false, false)]
    [InlineData("1", true, false, 60, false, false)]
    [InlineData("1", true, true, 120, false, false)]
    [InlineData("1", true, true, 60, true, false)]
    [InlineData("1", true, true, 60, false, true)]
    public void Passive_actor_probe_requires_every_explicit_gate(string? value, bool capture, bool probe, double hz, bool independent, bool expected)
    {
        string? prior = Environment.GetEnvironmentVariable("FFX_VRR_ACTOR_PROBE");
        try
        {
            Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", value);
            using var f = new ActorDrawFixture();
            if (!independent) f.Module.Disable();
            f.Module.SubmissionChosenHz = hz; f.Module.GraphicsCapture(capture, probe);
            Assert.Equal(expected, f.Module.ActorProbeRequested);
        }
        finally { Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", prior); }
    }

    [Fact]
    public void Passive_actor_probe_samples_bounded_draw_output_without_a_receipt_or_native_actor_writes()
    {
        string? prior = Environment.GetEnvironmentVariable("FFX_VRR_ACTOR_PROBE");
        try
        {
            Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", "1");
            using var f = new ActorDrawFixture(); f.Construct(); f.Module.Disable(); f.Module.GraphicsCapture();
            byte[] world = f.PersistentWorld, palette = f.PersistentPalette;
            var events = new List<(string Kind, System.Text.Json.JsonElement Value)>();
            f.Module.ActorDiagnostic = (kind, data) => events.Add((kind, System.Text.Json.JsonSerializer.SerializeToElement(data)));
            f.Pose(12, 24); f.DrawQueued(99);
            Assert.Equal(12, f.Root.Translation.X); Assert.Equal(24, f.Bone.Translation.X);
            Assert.Equal(world, f.PersistentWorld); Assert.Equal(palette, f.PersistentPalette);
            var hierarchy = Assert.Single(events, e => e.Kind == "actor_hierarchy_output").Value;
            Assert.Equal(2, hierarchy.GetProperty("sampled_matrices").GetInt32());
            Assert.Equal(32, hierarchy.GetProperty("raw_words").GetArrayLength());
            Assert.False(hierarchy.GetProperty("native_actor_storage_written").GetBoolean());
            var draw = Assert.Single(events, e => e.Kind == "actor_draw_scope").Value;
            Assert.Equal(System.Text.Json.JsonValueKind.Null, draw.GetProperty("image").ValueKind);
            Assert.True(draw.GetProperty("normal_return").GetBoolean());
            Assert.Equal(ActorDrawNative.DrawResult, draw.GetProperty("native_result").GetUInt32());
            Assert.Equal(0, f.Module.ActorDrawBlends);
            f.Module.ActorDiagnostic = (_, _) => throw new InvalidOperationException("diagnostic sink");
            f.DrawQueued(100);
            Assert.Equal(2, ActorDrawNative.DrawCalls); Assert.Equal(2, ActorDrawNative.HierarchyCalls);
        }
        finally { Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", prior); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passive_lifetime_events_preserve_native_carriers_and_managed_lifetime(bool destruct)
    {
        LifetimeProbe(destruct, false, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passive_lifetime_events_preserve_original_exception_identity_and_native_once(bool destruct)
    {
        LifetimeProbe(destruct, true, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Passive_lifetime_telemetry_failure_cannot_replace_native_result_or_retry(bool destruct)
    {
        LifetimeProbe(destruct, false, true);
    }

    private static void LifetimeProbe(bool destruct, bool nativeThrows, bool telemetryThrows)
    {
        string? prior = Environment.GetEnvironmentVariable("FFX_VRR_ACTOR_PROBE");
        try
        {
            Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", "1");
            using var f = new ActorDrawFixture();
            if (destruct) f.Construct();
            f.Module.Disable(); f.Module.GraphicsCapture();
            var events = new List<(string Kind, System.Text.Json.JsonElement Value)>();
            f.Module.ActorDiagnostic = (kind, data) =>
            {
                events.Add((kind, System.Text.Json.JsonSerializer.SerializeToElement(data)));
                if (telemetryThrows) throw new InvalidOperationException("telemetry");
            };
            var expectedError = new InvalidOperationException("original lifetime native");
            if (nativeThrows)
            {
                if (destruct) ActorDrawNative.DestructError = expectedError;
                else ActorDrawNative.ConstructError = expectedError;
            }
            uint result = 0;
            void Invoke() => result = destruct ? f.Module.ActorDrawDestruct(f.Instance) : unchecked((uint)(nuint)f.Module.ActorDrawConstruct(f.Instance, f.Mesh));
            if (nativeThrows) Assert.Same(expectedError, Assert.Throws<InvalidOperationException>(Invoke));
            else { Invoke(); Assert.Equal(destruct ? 0xdeadbeefu : unchecked((uint)(nuint)f.Instance), result); }
            Assert.Equal(destruct ? 1 : 0, ActorDrawNative.Destructs);
            Assert.Equal(1, ActorDrawNative.Constructs);
            var item = Assert.Single(events);
            Assert.Equal(destruct ? "actor_destructor" : "actor_constructor", item.Kind);
            var data = item.Value;
            Assert.Equal(destruct ? "dtor" : "ctor", data.GetProperty("kind").GetString());
            Assert.Equal(!nativeThrows, data.GetProperty("normal_return").GetBoolean());
            Assert.Equal(unchecked((uint)(nuint)f.Instance), data.GetProperty("instance").GetUInt32());
            if (nativeThrows) Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("native_result").ValueKind);
            else Assert.Equal(result, data.GetProperty("native_result").GetUInt32());
            Assert.Equal(destruct, data.GetProperty("lifetime_before").ValueKind == System.Text.Json.JsonValueKind.Number);
            Assert.Equal(!destruct && !nativeThrows, data.GetProperty("lifetime_after").ValueKind == System.Text.Json.JsonValueKind.Number);
        }
        finally { Environment.SetEnvironmentVariable("FFX_VRR_ACTOR_PROBE", prior); }
    }
    private static ActorDrawFixture Paired()
    {
        var f = new ActorDrawFixture();
        f.Construct();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(0, 0); f.Draw();
        f.Begin(); f.Module.ActorDrawComplete(); f.Pose(10, 20); f.Draw();
        return f;
    }
}

internal sealed class ActorActionWorker : IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private readonly ManualResetEventSlim _complete = new();
    private readonly Thread _thread;
    private Exception? _error;
    internal ActorActionWorker()
    {
        _thread = new Thread(() =>
        {
            foreach (var action in _work.GetConsumingEnumerable())
            {
                try { action(); } catch (Exception error) { _error = error; }
                finally { _complete.Set(); }
            }
        }) { IsBackground = true };
        _thread.Start();
    }
    internal void Start(Action action) { _error = null; _complete.Reset(); _work.Add(action); }
    internal void Finish()
    {
        Assert.True(_complete.Wait(TimeSpan.FromSeconds(5)));
        if (_error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
    }
    internal void Run(Action action) { Start(action); Finish(); }
    public void Dispose()
    {
        _work.CompleteAdding(); Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
        _work.Dispose(); _complete.Dispose();
    }
}