using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class CameraSnapshotCompositionTests : IDisposable
{
    private const double Step = 1d / 60;
    public void Dispose() => CameraNative.Release();

    private static VrrModule Seed(int source, int viewport = 0)
    {
        CameraNative.Reset();
        CameraNative.Source(source);
        var module = new VrrModule();
        module.BindSyntheticCamera();
        module.Pass(0, () =>
        {
            Assert.False(RenderPassContext.IsActive);
            Assert.False(module.Presentation);
            module.Snapshot();
            Assert.Equal(0, module.FinalizeCamera(viewport));
        });
        Assert.True(module.Baseline);
        CameraNative.Pose(8, 1.2f);
        module.Pass(Step, () => { module.Snapshot(); Assert.Equal(0, module.FinalizeCamera(viewport)); });
        Assert.True(module.Baseline);
        module.RestoreCamera();
        return module;
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public void Held_snapshot_keeps_real_camera_finalize_and_selected_native_output(int source, int viewport)
    {
        var module = Seed(source, viewport);
        int snapshots = CameraNative.SnapshotCalls, finalizes = CameraNative.FinalizeCalls;
        int sourceCalls = source switch { 1 => CameraNative.Source1Calls, 2 => CameraNative.Source2Calls, _ => CameraNative.Source3Calls };
        int views = CameraNative.ViewCalls, fovs = CameraNative.FovCalls;
        module.Pass(1.5 * Step, () =>
        {
            Assert.True(module.Presentation);
            module.Snapshot();
            Assert.True(module.Held);
            Assert.Equal(snapshots, CameraNative.SnapshotCalls);
            Assert.Equal(0, module.FinalizeCamera(viewport));
            Assert.Equal(finalizes + 1, CameraNative.FinalizeCalls);
            Assert.Equal(viewport, CameraNative.LastViewport);
            Assert.Equal(-4f, (*(Matrix4x4*)CameraNative.View).M41, 4);
            if (source is 1 or 3) Assert.Equal(-4f, CameraNative.OutputView.M41, 4);
            else Assert.Equal(CameraNative.CanonicalView, CameraNative.OutputView);
            Assert.Equal(views + (source == 3 ? 2 : 0), CameraNative.ViewCalls);
            Assert.Equal(fovs + (source == 1 ? 2 : 0), CameraNative.FovCalls);
            if (source == 1) Assert.Equal(1.1f, CameraNative.OutputProjection.X, 4);
            float* block = (float*)(CameraNative.Blocks + viewport * 0x1c0);
            Assert.Equal(1.2f, block[4]);
        });
        Assert.Equal(sourceCalls + 1, source switch { 1 => CameraNative.Source1Calls, 2 => CameraNative.Source2Calls, _ => CameraNative.Source3Calls });
        Assert.True(module.Baseline);
        Assert.False(module.Held);
        module.RestoreCamera();
        Assert.Equal(CameraNative.CanonicalView, *(Matrix4x4*)CameraNative.View);
    }

    [Fact]
    public void Source_one_viewport_one_retains_native_view_and_projection_ownership_during_hold()
    {
        var module = Seed(1, 1);
        int snapshots = CameraNative.SnapshotCalls, fovs = CameraNative.FovCalls;
        module.Pass(1.5 * Step, () =>
        {
            module.Snapshot();
            Assert.True(module.Held);
            Assert.Equal(0, module.FinalizeCamera(1));
            Assert.Equal(1, CameraNative.LastViewport);
            Assert.Equal(CameraNative.CanonicalView, *(Matrix4x4*)CameraNative.View);
            Assert.Equal(CameraNative.Projection, CameraNative.OutputProjection);
        });
        Assert.Equal(snapshots, CameraNative.SnapshotCalls);
        Assert.Equal(fovs + 1, CameraNative.FovCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Repeated_held_images_each_finalize_once_and_restore_before_the_next_native_output(int source)
    {
        var module = Seed(source);
        int snapshots = CameraNative.SnapshotCalls;
        foreach (float alpha in new[] { .25f, .5f, .75f })
        {
            int finalizes = CameraNative.FinalizeCalls;
            module.Pass((1 + alpha) * Step, () =>
            {
                module.Snapshot();
                Assert.True(module.Held);
                module.FinalizeCamera();
                Assert.Equal(-8 * alpha, (*(Matrix4x4*)CameraNative.View).M41, 4);
            });
            Assert.Equal(finalizes + 1, CameraNative.FinalizeCalls);
            Assert.Equal(snapshots, CameraNative.SnapshotCalls);
            Assert.True(module.Baseline);
        }
        module.RestoreCamera();
        Assert.Equal(CameraNative.CanonicalView, *(Matrix4x4*)CameraNative.View);
    }

    [Theory]
    [InlineData("hard_cut")]
    [InlineData("source")]
    [InlineData("camera_identity")]
    public void Fresh_camera_cut_or_identity_never_interpolates_old_pair_while_snapshot_is_held(string change)
    {
        var module = Seed(1);
        if (change == "source") CameraNative.Source(3);
        if (change == "camera_identity") *(uint*)CameraNative.Current = 8;
        CameraNative.Pose(change == "hard_cut" ? 1000 : 12, 1.2f);
        module.Pass(2 * Step, () => { module.Snapshot(); module.FinalizeCamera(); });
        module.RestoreCamera();
        int snapshots = CameraNative.SnapshotCalls, finalizes = CameraNative.FinalizeCalls;
        module.Pass(2.5 * Step, () =>
        {
            module.Snapshot();
            Assert.True(module.Held);
            Assert.Equal(0, module.FinalizeCamera());
            Assert.Equal(CameraNative.CanonicalView, *(Matrix4x4*)CameraNative.View);
            Assert.Equal(CameraNative.CanonicalView, CameraNative.OutputView);
        });
        Assert.Equal(snapshots, CameraNative.SnapshotCalls);
        Assert.Equal(finalizes + 1, CameraNative.FinalizeCalls);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("native_owner")]
    public void Scheduler_epoch_reset_or_native_owner_revokes_hold_and_camera_pair(string change)
    {
        var module = Seed(1);
        int snapshots = CameraNative.SnapshotCalls;
        if (change == "native_owner") module.NativeOwner = true;
        double now = change == "epoch" ? 1 : 1.5 * Step;
        module.Pass(now, () =>
        {
            module.Snapshot();
            Assert.False(module.Held);
            Assert.Equal(0, module.FinalizeCamera());
            Assert.Equal(CameraNative.CanonicalView, *(Matrix4x4*)CameraNative.View);
            Assert.Equal(CameraNative.CanonicalView, CameraNative.OutputView);
        });
        Assert.Equal(snapshots + 1, CameraNative.SnapshotCalls);
        Assert.False(module.Baseline);
    }

    [Theory]
    [InlineData(0, 23)]
    [InlineData(3, 0)]
    public void Native_result_and_viewport_are_forwarded_once_during_snapshot_hold(int viewport, int result)
    {
        var module = Seed(1);
        int calls = CameraNative.FinalizeCalls;
        CameraNative.Result = result;
        module.Pass(1.5 * Step, () =>
        {
            module.Snapshot();
            Assert.True(module.Held);
            Assert.Equal(result, module.FinalizeCamera(viewport));
            Assert.Equal(viewport, CameraNative.LastViewport);
        });
        Assert.Equal(calls + 1, CameraNative.FinalizeCalls);
    }

    [Fact]
    public void Guard_routes_reject_unknown_or_wrong_width_reads()
    {
        CameraNative.Reset();
        Assert.Throws<InvalidOperationException>(() => CameraNative.Route(0xf007b0, typeof(uint)));
        Assert.Throws<InvalidOperationException>(() => CameraNative.Route(0xabcdef, typeof(byte)));
    }
}
