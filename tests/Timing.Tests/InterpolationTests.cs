using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class InterpolationTests
{
    private static Transform Pose(float x) => new(new Vector3(x, 0, 0), Quaternion.Identity);

    [Theory]
    [InlineData("camera", "character")]
    [InlineData("character", "camera")]
    public void PauseInvalidatesEveryTransformRegardlessOfSnapshotOrder(string first, string second)
    {
        double now = 0;
        var interpolator = new RenderInterpolator(() => now);
        interpolator.SetTickPeriod(1.0 / 30);
        interpolator.Snapshot(first, Pose(0));
        interpolator.Snapshot(second, Pose(0));
        now = 1;
        interpolator.Snapshot(first, Pose(10));
        interpolator.Snapshot(second, Pose(20));

        Assert.False(interpolator.TryGetInterpolated(first, 1.0 / 60, out var firstResult));
        Assert.False(interpolator.TryGetInterpolated(second, 1.0 / 60, out var secondResult));
        Assert.Equal(Pose(10), firstResult);
        Assert.Equal(Pose(20), secondResult);
    }

    [Fact]
    public void ActiveCameraDoesNotKeepAnAbsentCharacterHistoryAlive()
    {
        double now = 0;
        var interpolator = new RenderInterpolator(() => now);
        interpolator.SetTickPeriod(1.0 / 30);
        interpolator.Snapshot("character", Pose(0));
        for (int tick = 1; tick <= 30; tick++)
        {
            now = tick / 30.0;
            interpolator.Snapshot("camera", Pose(tick));
        }
        interpolator.Snapshot("character", Pose(100));

        Assert.False(interpolator.TryGetInterpolated("character", 1.0 / 60, out var result));
        Assert.Equal(Pose(100), result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    public void ContinuousTicksKeepTheDelayedInterpolationConvention(double tickFraction, float expected)
    {
        double now = 0;
        var interpolator = new RenderInterpolator(() => now);
        interpolator.SetTickPeriod(1.0 / 30);
        interpolator.Snapshot("camera", Pose(0));
        now = 1.0 / 30;
        interpolator.Snapshot("camera", Pose(10));

        Assert.True(interpolator.TryGetInterpolated("camera", tickFraction / 30, out var result));
        Assert.Equal(Pose(expected), result);
    }

    [Fact]
    public void FirstSampleReturnsCurrentWithoutBlending()
    {
        var interpolator = new RenderInterpolator(() => 0);
        interpolator.Snapshot("camera", Pose(10));
        Assert.False(interpolator.TryGetInterpolated("camera", 0, out var result));
        Assert.Equal(Pose(10), result);
    }

    [Fact]
    public void ContinuousSampleAfterPauseStartsANewPair()
    {
        double now = 0;
        var interpolator = new RenderInterpolator(() => now);
        interpolator.SetTickPeriod(1.0 / 30);
        interpolator.Snapshot("camera", Pose(0));
        now = 1;
        interpolator.Snapshot("camera", Pose(10));
        now += 1.0 / 30;
        interpolator.Snapshot("camera", Pose(20));
        Assert.True(interpolator.TryGetInterpolated("camera", 1.0 / 60, out var result));
        Assert.Equal(Pose(15), result);
    }
}
