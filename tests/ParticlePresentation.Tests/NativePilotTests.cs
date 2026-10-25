using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public sealed class NativePilotTests : IDisposable
{
    public NativePilotTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    [Fact]
    public void LastInvalidBindingRejectsBeforeAnyHookIsRegistered()
    {
        FhUtil.BadPrefix = 0x32e790;
        Assert.False(new VrrModule().InitializePilot());
        Assert.Equal(0, PilotNative.Hooks);
    }

    [Fact]
    public void EmptySceneCanQualifyButAnotherManagerOrActorRejectsTheWholeImage()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        Assert.Equal(14, PilotNative.Hooks);
        module.RunWorker(0, 0);
        Assert.True(module.QualifyPilot());
        module.LiveManagers = true;
        Assert.False(module.QualifyPilot());
        module.LiveManagers = false;
        FhUtil.set_at<nint>(EngineAddresses.PlayerChrs, 1234);
        Assert.False(module.QualifyPilot());
    }

    [Fact]
    public async Task FirstRealWorkerOwnsItsThreadIndependentlyOfTheLoaderThread()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        bool qualified = await Task.Run(() =>
        {
            module.RunWorker(0, 0);
            return module.QualifyPilot();
        });
        Assert.True(qualified);
        Assert.Equal(1, PilotNative.Workers);
    }
    [Fact]
    public void EmptySceneRequalifiesAfterHistoryResetWithoutNeedingAParticleRun()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.RunWorker(0, 0);
        module.ResetPilot();
        Assert.True(module.QualifyPilot());
    }
    [Fact]
    public unsafe void UnobservedThirdListOwnerCannotQualifyAsAnEmptyScene()
    {
        using var memory = new PilotMemory();
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.RunWorker(0, 0);
        *(uint*)FhUtil.ptr_at<byte>(0xea40c0) = (uint)(nuint)memory.Address;
        *(uint*)(memory.Address + 0x10) = (uint)(nuint)(memory.Address + 0x8000);
        *(uint*)(memory.Address + 0x14) = (uint)(nuint)(memory.Address + 0x8100);
        *(uint*)(memory.Address + 0x18) = (uint)(nuint)(memory.Address + 0x8200);
        *(uint*)(memory.Address + 0x20) = (uint)(nuint)(memory.Address + 0x1000);
        *(short*)(memory.Address + 0x8000) = -1;
        *(short*)(memory.Address + 0x8100) = -1;
        *(short*)(memory.Address + 0x8200) = 0;
        *(short*)(memory.Address + 0x8202) = -1;
        Assert.False(module.QualifyPilot());
    }
    [Fact]
    public void PresentationManagerWithoutAnOwnedDdScopeNeverRunsSimulation()
    {
        RenderPassContext.IsPresentation = true;
        Assert.Equal(0u, new VrrModule().RunManager(1234));
        Assert.Equal(0, PilotNative.Runs);
    }

    [Fact]
    public void NativeManagerRetainsItsOriginalStatusAndCall()
    {
        Assert.Equal(77u, new VrrModule().RunManager(1234));
        Assert.Equal(1, PilotNative.Runs);
    }

    [Fact]
    public unsafe void DdRestoresManagerVisualStateWhileRetainingPacketPublication()
    {
        using var memory = new PilotMemory();
        var module = new VrrModule();
        byte[] before = new ReadOnlySpan<byte>((void*)(memory.Address + 0x400), 0x80).ToArray();
        RenderPassContext.IsPresentation = true;
        PilotNative.OnDd = () => new Span<byte>((void*)(memory.Address + 0x440), 0x40).Fill(0xa5);
        Assert.Equal(memory.Address + 0x7004, module.DdForTest(memory.Address));
        Assert.Equal(before, new ReadOnlySpan<byte>((void*)(memory.Address + 0x400), 0x80).ToArray());
        Assert.Equal(1, PilotNative.PacketPublications);
    }
    [Fact]
    public unsafe void ReplayPreservesAllObjectBytesAndGlobalScopeWhileKeepingCallbackOrder()
    {
        using var memory = new PilotMemory();
        var module = new VrrModule();
        byte[] before = memory.ObjectBytes;
        SetGlobals();
        PilotNative.OnDraw = obj =>
        {
            Assert.Equal(1, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
            Assert.Equal((uint)(nuint)(memory.Address + 0x400), FhUtil.get_at<uint>(0x1f0ffe4));
            *(uint*)(obj + 0x20) = 0x12345678;
            *(uint*)(obj + 0xa0) = 0x87654321;
        };
        Assert.Equal(0u, module.ReplayForTest(memory.Address));
        Assert.Equal(new[] { 0, 1 }, PilotNative.KernelOrder);
        Assert.Equal(before, memory.ObjectBytes);
        AssertGlobals();
        Assert.Equal(0, PilotNative.Runs);
    }

    [Fact]
    public unsafe void RendererExceptionRestoresObjectBytesAndGlobals()
    {
        using var memory = new PilotMemory();
        var module = new VrrModule();
        byte[] before = memory.ObjectBytes;
        SetGlobals();
        PilotNative.OnDraw = obj =>
        {
            new Span<byte>((void*)(obj + 0x10), 0xf0).Clear();
            throw new InvalidOperationException("renderer failure");
        };
        Assert.Throws<InvalidOperationException>(() => module.ReplayForTest(memory.Address));
        Assert.Equal(before, memory.ObjectBytes);
        AssertGlobals();
    }

    private static void SetGlobals()
    {
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 7);
        FhUtil.set_at(0x1f0ffe4, 12);
        FhUtil.set_at(0x1f0fd20, 13);
    }

    private static void AssertGlobals()
    {
        Assert.Equal(7, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        Assert.Equal(12, FhUtil.get_at<int>(0x1f0ffe4));
        Assert.Equal(13, FhUtil.get_at<int>(0x1f0fd20));
    }
    [Fact]
    public void ColdEmptySceneBindsAtMainLoopWithoutAnyOpuWorker()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        Assert.False(module.QualifyPilot());
        module.EnterParticleLoop();
        Assert.True(module.QualifyPilot());
        Assert.Equal(0, PilotNative.Workers);
    }

    [Fact]
    public async Task MainLoopOwnerCannotBeTakenOverByAnotherThread()
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.EnterParticleLoop();
        Assert.True(module.QualifyPilot());
        bool other = await Task.Run(() => { module.EnterParticleLoop(); return module.QualifyPilot(); });
        Assert.False(other);
        Assert.Equal(0, PilotNative.Workers);
    }}
