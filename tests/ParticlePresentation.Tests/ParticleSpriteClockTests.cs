using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public unsafe sealed class ParticleSpriteClockTests : IDisposable
{
    public ParticleSpriteClockTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothClientsShareOneOwnerForEachLifetimeAndLeaf(bool presentation)
    {
        var module = new VrrModule();
        module.ConfigureParticles(presentation, false);
        module.EnableSpriteClock();
        Assert.True(module.InitializeParticleOwners());
        foreach (nint target in new nint[] { 0x3123c0, 0x316770, 0x3168f0, 0x312d20, 0x311f20, 0x340a70, 0x340f60 })
            Assert.Equal(1, PilotNative.HookTargets.Count(value => value == target));
        Assert.Equal(presentation, PilotNative.HookTargets.Contains((nint)0x40cc00));
    }

    [Theory]
    [InlineData(0x340a70)]
    [InlineData(0x340f60)]
    [InlineData(0x316770)]
    [InlineData(0x3168f0)]
    [InlineData(0x312d20)]
    [InlineData(0x3123c0)]
    [InlineData(0x311f20)]
    public void EveryChangedBindingRejectsBeforeArming(int target)
    {
        var module = new VrrModule();
        module.ConfigureParticles(false, false);
        module.EnableSpriteClock();
        FhUtil.BadPrefix = target;
        Assert.False(module.InitializeParticleOwners());
        Assert.Empty(PilotNative.HookTargets);
    }

    [Theory]
    [InlineData(0x340a70, 0x3b7)]
    [InlineData(0x340a70, 0x3b9)]
    [InlineData(0x340a70, 0x3c3)]
    [InlineData(0x340a70, 0x3c7)]
    [InlineData(0x340f60, 0x467)]
    [InlineData(0x340f60, 0x469)]
    [InlineData(0x340f60, 0x473)]
    [InlineData(0x340f60, 0x477)]
    public void ChangedPauseGateOrStepInstructionRefusesClockBeforeAnyHooks(int target, int offset)
    {
        var module = new VrrModule();
        module.ConfigureParticles(false, false);
        module.EnableSpriteClock();
        FhUtil.BadInstructionTarget = target;
        FhUtil.BadInstructionOffset = offset;
        Assert.False(module.InitializeSpriteClock());
        Assert.Empty(PilotNative.HookTargets);
    }

    [Theory]
    [InlineData(0x340a70, 0x3c3)]
    [InlineData(0x340f60, 0x473)]
    public void RejectsEsiStepWhereInstalledLeafUsesEax(int target, int step)
    {
        var module = new VrrModule();
        module.EnableSpriteClock();
        byte* entry = FhUtil.ptr_at<byte>(target);
        entry[step + 2] = 0x46;
        Assert.False(module.InitializeSpriteClock());
        Assert.Empty(PilotNative.HookTargets);
    }

    [Fact]
    public void FailedSharedLifecycleInstallIsNeverRetriedByTheOtherClient()
    {
        var module = new VrrModule();
        module.EnableSpriteClock();
        PilotNative.FailHook = 0x311f20;
        Assert.False(module.InitializePilot());
        Assert.False(module.InitializeSpriteClock());
        foreach (nint target in new nint[] { 0x316770, 0x3168f0, 0x312d20, 0x311f20 })
            Assert.Equal(1, PilotNative.HookTargets.Count(value => value == target));
        Assert.DoesNotContain((nint)0x340a70, PilotNative.HookTargets);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(15)]
    [InlineData(-15)]
    [InlineData(0)]
    [InlineData(short.MinValue)]
    [InlineData(short.MaxValue)]
    public void AuthoredTimeIsConservedAtEverySupportedSimulationRate(short step)
    {
        foreach (int hz in new[] { 30, 60, 120 })
        {
            var clock = new ParticleSpriteClock();
            Assert.True(clock.Created(10, 20));
            long total = 0;
            for (int i = 0; i < hz * 20; i++)
            {
                Assert.True(clock.TryScale(20, 30, 0, step, hz, out short applied));
                total += applied;
            }
            Assert.Equal(step * 30L * 20, total);
        }
    }

    [Fact]
    public void FractionalUnitsSurviveSimulationRateAndOperandChanges()
    {
        var clock = new ParticleSpriteClock();
        clock.Created(10, 20);
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out short first));
        Assert.Equal(3, first);
        Assert.True(clock.TryScale(20, 30, 0, 0, 30, out short zero));
        Assert.Equal(0, zero);
        Assert.True(clock.TryScale(20, 30, 0, 15, 60, out short second));
        Assert.Equal(8, second);
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out short third));
        Assert.Equal(4, third);
    }

    [Fact]
    public void ProgramAndPrivateSlotCarryRemainIndependent()
    {
        var clock = new ParticleSpriteClock();
        clock.Created(10, 20);
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out short a));
        Assert.True(clock.TryScale(20, 31, 0, 15, 120, out short b));
        Assert.True(clock.TryScale(20, 30, 4, 15, 120, out short c));
        Assert.Equal((3, 3, 3), ((int)a, (int)b, (int)c));
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out short next));
        Assert.Equal(4, next);
    }

    [Fact]
    public void CarryCannotSurviveLoopDeleteReuseOrManagerEpoch()
    {
        var clock = new ParticleSpriteClock();
        void First(nint obj)
        {
            Assert.True(clock.TryScale(obj, 30, 0, 15, 120, out short applied));
            Assert.Equal(3, applied);
        }
        clock.Created(10, 20);
        First(20);
        clock.LoopReset(20);
        First(20);
        clock.Deleted(20);
        Assert.False(clock.TryScale(20, 30, 0, 15, 120, out _));
        clock.Created(11, 20);
        First(20);
        clock.ResetManager(10);
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out short retained));
        Assert.Equal(4, retained);
        clock.ResetManager(11);
        Assert.False(clock.TryScale(20, 30, 0, 15, 120, out _));
        clock.Created(11, 20);
        First(20);
        clock.Created(11, 20);
        First(20);
    }

    [Fact]
    public void HistoryIsBoundedAndUntrackedFractionalTimingIsRejected()
    {
        var clock = new ParticleSpriteClock(objectLimit: 1, slotLimit: 1);
        Assert.True(clock.Created(10, 0));
        Assert.True(clock.Created(10, 20));
        Assert.False(clock.Created(10, 21));
        Assert.False(clock.TryScale(21, 30, 0, 15, 120, out _));
        Assert.True(clock.TryScale(21, 30, 0, 512, 120, out short integral));
        Assert.Equal(128, integral);
        Assert.True(clock.TryScale(20, 30, 0, 15, 120, out _));
        Assert.False(clock.TryScale(20, 31, 0, 15, 120, out _));
        Assert.False(clock.TryScale(20, 30, 0, 15, 90, out _));
        clock.Deleted(20);
        Assert.True(clock.Created(10, 21));
    }

    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 16)]
    public void DrawUsesSynchronousOperandCopyAndPreservesAllOtherBytes(bool extended, int length)
    {
        using var memory = new PilotMemory();
        var module = SpriteModule();
        nint obj = memory.Address + 0x1000, operand = memory.Address + 0x4000, program = memory.Address + 0x5000;
        Prepare(memory.Address, 15);
        PilotNative.CreatedObject = obj;
        Assert.Equal(obj, module.CreateSprite(77, 99));
        byte[] before = new ReadOnlySpan<byte>((void*)operand, length).ToArray();
        int draws = 0;
        PilotNative.OnSprite = (actualObj, copy, actualProgram) =>
        {
            draws++;
            Assert.Equal(obj, actualObj);
            Assert.Equal(program, actualProgram);
            Assert.NotEqual(operand, copy);
            Assert.Equal(draws == 1 ? 7 : 8, *(short*)(copy + 8));
            byte[] expected = (byte[])before.Clone();
            *(short*)(copy + 8) = 15;
            Assert.Equal(expected, new ReadOnlySpan<byte>((void*)copy, length).ToArray());
        };
        module.DrawSprite(obj, operand, program, extended);
        module.DrawSprite(obj, operand, program, extended);
        Assert.Equal(2, draws);
        Assert.Equal(before, new ReadOnlySpan<byte>((void*)operand, length).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedNoTextureAndPresentationCallsDoNotConsumeCarry(bool extended)
    {
        using var memory = new PilotMemory();
        var module = SpriteModule();
        VrrModule.TargetFramerate = 120;
        nint obj = memory.Address + 0x1000, operand = memory.Address + 0x4000, program = memory.Address + 0x5000;
        Prepare(memory.Address, 15);
        PilotNative.CreatedObject = obj;
        module.CreateSprite(77, 99);
        var steps = new List<int>();
        int draws = 0;
        PilotNative.OnSprite = (_, copy, _) =>
        {
            draws++;
            if (FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) == 0 && *(uint*)(copy + 4) != 0xffff)
                steps.Add(*(short*)(copy + 8));
        };
        module.DrawSprite(obj, operand, program, extended);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 7);
        module.DrawSprite(obj, operand, program, extended);
        Assert.Equal(7, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
        *(uint*)(operand + 4) = 0xffff;
        module.DrawSprite(obj, operand, program, extended);
        *(uint*)(operand + 4) = 3;
        RenderPassContext.IsPresentation = true;
        module.DrawSprite(obj, operand, program, extended);
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        RenderPassContext.IsPresentation = false;
        module.DrawSprite(obj, operand, program, extended);
        Assert.Equal(new[] { 3, 4 }, steps);
        Assert.Equal(5, draws);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PresentationDrawPreservesDurationDebtAndRestoresPauseOnException(bool extended)
    {
        using var memory = new PilotMemory();
        var module = SpriteModule();
        nint obj = memory.Address + 0x1000, operand = memory.Address + 0x4000, program = memory.Address + 0x5000;
        Prepare(memory.Address, 0);
        *(ushort*)(obj + 0xa0) = 1024;
        *(ushort*)(obj + 0xa2) = 1;
        int draws = 0;
        PilotNative.OnSprite = (_, copy, _) =>
        {
            draws++;
            if (FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) == 0)
            {
                *(ushort*)(obj + 0xa0) += (ushort)*(short*)(copy + 8);
                while (*(ushort*)(obj + 0xa0) >= 512)
                {
                    *(ushort*)(obj + 0xa0) -= 512;
                    (*(ushort*)(obj + 0xa2))++;
                }
            }
        };
        RenderPassContext.IsPresentation = true;
        module.DrawSprite(obj, operand, program, extended);
        Assert.Equal(1024, *(ushort*)(obj + 0xa0));
        Assert.Equal(1, *(ushort*)(obj + 0xa2));
        Assert.Equal(1, draws);
        PilotNative.OnSprite = (_, _, _) => throw new InvalidOperationException("draw failed");
        Assert.Throws<InvalidOperationException>(() => module.DrawSprite(obj, operand, program, extended));
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        RenderPassContext.IsPresentation = false;
    }

    [Fact]
    public void LifetimeCallbacksClearOnlyTheirManagerBeforeNativeDestruction()
    {
        var module = SpriteModule();
        VrrModule.TargetFramerate = 120;
        PilotNative.CreatedObject = 20;
        module.CreateSprite(10, 90);
        PilotNative.CreatedObject = 21;
        module.CreateSprite(11, 90);
        Assert.True(module.ScaleSprite(20, 30, 0, 15, out _));
        Assert.True(module.ScaleSprite(21, 30, 0, 15, out _));
        module.StartSharedManager(10, 0x1000, 91, 2);
        Assert.False(module.ScaleSprite(20, 30, 0, 15, out _));
        Assert.True(module.ScaleSprite(21, 30, 0, 15, out short retained));
        Assert.Equal(4, retained);
        PilotNative.OnLifecycle = (kind, address) =>
        {
            if (kind == "loop")
            {
                Assert.True(module.ScaleSprite(address, 30, 0, 15, out short reset));
                Assert.Equal(3, reset);
            }
            else if (kind == "delete") Assert.False(module.ScaleSprite(address, 30, 0, 15, out _));
            else if (kind == "free") Assert.False(module.ScaleSprite(21, 30, 0, 15, out _));
        };
        module.LoopSprite(21);
        module.DeleteSprite(21);
        PilotNative.CreatedObject = 21;
        module.CreateSprite(11, 90);
        module.FreeSprites(11);
    }

    private static VrrModule SpriteModule()
    {
        var module = new VrrModule();
        module.ConfigureParticles(false, false);
        module.EnableSpriteClock();
        Assert.True(module.InitializeParticleOwners());
        return module;
    }

    private static void Prepare(nint memory, short step)
    {
        new Span<byte>((void*)(memory + 0x4000), 16).Fill(0xa7);
        *(uint*)(memory + 0x4004) = 3;
        *(short*)(memory + 0x4008) = step;
        *(uint*)(memory + 0x500c) = (uint)(nuint)(memory + 0x6000);
        *(int*)(memory + 0x6000) = 0;
    }
}
