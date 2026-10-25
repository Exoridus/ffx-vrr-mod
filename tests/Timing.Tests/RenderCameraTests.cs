using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class RenderCameraTests
{
    private static RenderCameraSample Sample(float eye, int source = 1, float fov = 100)
        => new(Matrix4x4.CreateLookAt(new Vector3(eye, 0, 0), new Vector3(eye, 0, -1), Vector3.UnitY),
            new Vector3(fov, 100, 1), new RenderCameraIdentity(source, 42, 0, 0));

    private static void Tick(RenderCameraHistory history, int viewport, RenderCameraSample sample)
    {
        history.BeginSimulation();
        history.Capture(viewport, sample);
        history.CompleteSimulation();
    }

    [Fact]
    public void PresentationUsesTwoCanonicalSamplesAndDoesNotAdvanceHistory()
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        Tick(history, 0, Sample(10));
        Assert.True(history.TryInterpolate(0, Sample(10), .25f, out var quarter));
        Assert.Equal(-2.5f, quarter.View.M41, 4);
        Assert.True(history.TryInterpolate(0, Sample(10), .75f, out var later));
        Assert.Equal(-7.5f, later.View.M41, 4);
        Tick(history, 0, Sample(20));
        Assert.True(history.TryInterpolate(0, Sample(20), .5f, out var next));
        Assert.Equal(-15, next.View.M41, 4);
    }

    [Fact]
    public void DuplicateFinalizeWithinOneSimulationKeepsPreviousTick()
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        history.BeginSimulation();
        history.Capture(0, Sample(5));
        history.Capture(0, Sample(10));
        Assert.True(history.TryInterpolate(0, Sample(10), .5f, out var result));
        Assert.Equal(-5, result.View.M41, 4);
    }

    [Fact]
    public void SourceChangesAndAbsentViewportsInvalidateTheirOwnHistory()
    {
        var history = new RenderCameraHistory();
        history.BeginSimulation();
        history.Capture(0, Sample(0));
        history.Capture(1, Sample(20));
        history.CompleteSimulation();
        Tick(history, 0, Sample(10));
        Assert.False(history.TryInterpolate(1, Sample(20), .5f, out _));
        Tick(history, 0, Sample(20, source: 3));
        Assert.False(history.TryInterpolate(0, Sample(20, source: 3), .5f, out _));
    }

    [Theory]
    [InlineData(600, 100)]
    [InlineData(10, 40)]
    public void TeleportsAndProjectionCutsUseCurrentShot(float eye, float fov)
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        Tick(history, 0, Sample(eye, fov: fov));
        Assert.False(history.TryInterpolate(0, Sample(eye, fov: fov), .5f, out var result));
        Assert.Equal(Sample(eye, fov: fov), result);
    }

    [Fact]
    public void SuddenJumpAfterSmoothMovementIsACut()
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        Tick(history, 0, Sample(1));
        Tick(history, 0, Sample(100));
        Assert.False(history.TryInterpolate(0, Sample(100), .5f, out _));
    }

    [Fact]
    public void InvalidMatricesAndUnexpectedRenderStateNeverBlend()
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        Tick(history, 0, Sample(10));
        Assert.False(history.TryInterpolate(0, Sample(15), .5f, out _));
        var invalid = Sample(20) with { View = default };
        Tick(history, 0, invalid);
        Assert.False(history.TryInterpolate(0, invalid, .5f, out _));
    }

    [Fact]
    public void FovAndEyeUseSameInterpolationFraction()
    {
        var history = new RenderCameraHistory();
        Tick(history, 0, Sample(0));
        Tick(history, 0, Sample(10, fov: 90));
        Assert.True(history.TryInterpolate(0, Sample(10, fov: 90), .5f, out var result));
        Assert.Equal(95, result.Projection.X);
        Assert.Equal(-5, result.View.M41, 4);
    }

    [Fact]
    public void RestoreConsumesOwnershipAndPreservesEngineReplacements()
    {
        var ownership = new RenderCameraWrite();
        var canonical = Sample(10).View;
        var rendered = Sample(5).View;
        ownership.Record(canonical, rendered);
        Assert.True(ownership.TryRestore(rendered, out var restored));
        Assert.Equal(canonical, restored);
        Assert.False(ownership.TryRestore(rendered, out _));
        ownership.Record(canonical, rendered);
        Assert.False(ownership.TryRestore(Sample(7).View, out _));
        Assert.False(ownership.TryRestore(rendered, out _));
    }

    [Fact]
    public void RestoreRequiresExactBitsIncludingSignedZero()
    {
        var ownership = new RenderCameraWrite();
        var rendered = Sample(5).View;
        ownership.Record(Sample(10).View, rendered);
        rendered.M14 = -0f;
        Assert.False(ownership.TryRestore(rendered, out _));
    }
}
