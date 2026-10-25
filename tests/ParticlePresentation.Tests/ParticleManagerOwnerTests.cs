using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public sealed class ParticleManagerOwnerTests : IDisposable
{
    public ParticleManagerOwnerTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    public void ManagerHasOneNativeOwnerForEachEnabledConfiguration(bool vrr, bool timeline, int expected)
    {
        var module = new VrrModule();
        module.ConfigureParticles(vrr, timeline);
        Assert.True(module.InitializeParticleOwners());
        Assert.Equal(expected, PilotNative.HookTargets.Count(target => target == EngineAddresses.PppRunPart));
        Assert.Equal(expected, PilotNative.HookTargets.Count(target => target == EngineAddresses.PppStartPart));
    }

    [Fact]
    public void SharedNativeManagerReturnsOriginalStatusExactlyOnce()
    {
        var module = new VrrModule();
        Assert.True(module.InitializeParticleOwners());
        Assert.Equal(77u, module.RunSharedManager(1234));
        Assert.Equal(1, PilotNative.Runs);
    }

    [Fact]
    public void SharedPresentationManagerCannotAdvanceOutsideItsOwnedScope()
    {
        var module = new VrrModule();
        Assert.True(module.InitializeParticleOwners());
        RenderPassContext.IsPresentation = true;
        Assert.Equal(0u, module.RunSharedManager(1234));
        Assert.Equal(0, PilotNative.Runs);
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
    }

    [Theory]
    [InlineData(true, 0x800, 1)]
    [InlineData(false, 0x1000, 0)]
    public void SharedStartPreservesArgumentsAndOnlyAppliesEnabledTimeline(bool timeline, int expectedStep, int managedSteps)
    {
        var module = new VrrModule();
        module.ConfigureParticles(true, timeline);
        Assert.True(module.InitializeParticleOwners());
        module.StartSharedManager(1234, 0x1000, 5678, 9);
        Assert.Equal(1, PilotNative.Starts);
        Assert.Equal((nint)1234, PilotNative.LastStartManager);
        Assert.Equal((nint)5678, PilotNative.LastStartData);
        Assert.Equal(9, PilotNative.LastStartFlags);
        Assert.Equal(expectedStep, PilotNative.LastStartStep);
        Assert.Equal(managedSteps, module.ManagedStepCount);
    }
}
