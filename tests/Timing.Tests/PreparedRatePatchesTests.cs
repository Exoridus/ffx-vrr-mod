using System.Reflection;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class PreparedRatePatchesTests
{
    private readonly VrrModule _module = new(false);

    public PreparedRatePatchesTests()
    {
        NativeMemory.Clear(FhUtil.Memory, 0x800000);
        VrrModule.TestRate = 60;
        byte* dream = FhUtil.ptr_at<byte>(EngineAddresses.DreamStarCounterCompare);
        dream[0] = 0x66; dream[1] = 0x83; dream[2] = 0xf8; dream[3] = 3;
        FhUtil.set_at(EngineAddresses.DreamStarCounter, (ushort)1);
        foreach (nint rva in new nint[] { 0x316D42, 0x3171A2 })
        {
            byte* site = FhUtil.ptr_at<byte>(rva);
            site[0] = 0x81; site[1] = 0x40; site[2] = 0x0c;
            *(int*)(site + 3) = 0x1000;
        }
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 1);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, 0x1000);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x18, 1);
        _module._patches.BeginPreparation();
    }

    [Fact]
    public void DreamPhaseAndThresholdPublishTogether()
    {
        Call("patch_dream_overlay_stars");
        AssertDream(1, 3);
        _module._patches.CommitPrepared();
        AssertDream(2, 6);
    }

    [Fact]
    public void CancellingPreparedDreamLeavesNativePhaseUntouched()
    {
        Call("patch_dream_overlay_stars");
        _module._patches.RestoreAll();
        AssertDream(1, 3);
    }

    [Fact]
    public void StaleDreamPhaseRefusesBeforeThresholdPublication()
    {
        Call("patch_dream_overlay_stars");
        FhUtil.set_at(EngineAddresses.DreamStarCounter, (ushort)2);
        Assert.Throws<InvalidOperationException>(() => _module._patches.CommitPrepared());
        AssertDream(2, 3);
        Assert.Empty(_module._patches.Values);
    }

    [Fact]
    public void PreparedManagerRebasesOnceAfterCommitAndArmingAtSameCachedStep()
    {
        Call("patch_particle_timeline");
        AssertManager(0x1000);
        Assert.Equal(0x1000, FhUtil.get_at<int>(0x316D45));
        _module._patches.CommitPrepared();
        Assert.Equal(0x800, FhUtil.get_at<int>(0x316D45));
        Call("patch_particle_timeline");
        AssertManager(0x1000);
        _module.ArmSyntheticRuntimeForTest();
        Call("patch_particle_timeline");
        AssertManager(0x800);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, 0x1000);
        Call("patch_particle_timeline");
        AssertManager(0x1000);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(30)]
    public void PendingManagerRebasePrecedesUnsupportedOrLiveCoarseningRequest(float nextRate)
    {
        Call("patch_particle_timeline");
        AssertManager(0x1000);
        _module._patches.CommitPrepared();
        _module.ArmSyntheticRuntimeForTest();
        VrrModule.TestRate = nextRate;
        Call("patch_particle_timeline");
        AssertManager(0x800);
        Assert.Equal(0x800, FhUtil.get_at<int>(0x316D45));
    }

    [Fact]
    public void CancelledPreparationNeverRebasesManagers()
    {
        Call("patch_particle_timeline");
        _module._patches.RestoreAll();
        Call("patch_particle_timeline");
        AssertManager(0x1000);
    }

    private void Call(string name) => typeof(VrrModule).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_module, null);
    private static void AssertManager(int expected) => Assert.Equal(expected, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x10));
    private static void AssertDream(ushort phase, byte threshold)
    {
        Assert.Equal(phase, FhUtil.get_at<ushort>(EngineAddresses.DreamStarCounter));
        Assert.Equal(threshold, FhUtil.get_at<byte>(EngineAddresses.DreamStarCounterCompare + 3));
    }
}
