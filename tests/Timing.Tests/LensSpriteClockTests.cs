using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class LensSpriteClockTests
{
    [Theory]
    [InlineData(30f, false, false, 512)]
    [InlineData(60f, false, false, 256)]
    [InlineData(120f, false, false, 128)]
    [InlineData(30f, true, false, 0)]
    [InlineData(60f, true, false, 0)]
    [InlineData(60f, false, true, 0)]
    public void Native_draw_adds_only_the_owned_clock_step(float rate, bool paused, bool presentation, short expected)
    {
        VrrModule.TestRate = rate;
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused ? 1 : 0);
        RenderPassContext.IsPresentation = presentation;
        NativeCalls.LensDuration = 20000;
        NativeCalls.LensCalls = 0;
        short accumulator = 0;
        byte* work = stackalloc byte[16];
        *(short**)(work + 4) = &accumulator;
        try
        {
            new VrrModule().RunLensClockForTest((nint)work);
            Assert.Equal(expected, accumulator);
        }
        finally { RenderPassContext.IsPresentation = false; FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0); }
    }

    [Fact]
    public void Additional_images_do_not_duplicate_sixty_hz_simulation_time()
    {
        VrrModule.TestRate = 60;
        NativeCalls.LensDuration = 20000;
        NativeCalls.LensCalls = 0;
        short accumulator = 0;
        byte* work = stackalloc byte[16];
        *(short**)(work + 4) = &accumulator;
        try
        {
            for (int i = 0; i < 120; i++)
            {
                bool presentation = i % 2 == 1;
                FhUtil.set_at(EngineAddresses.PpvUserStopPartF, presentation ? 1 : 0);
                RenderPassContext.IsPresentation = presentation;
                new VrrModule().RunLensClockForTest((nint)work);
            }
            Assert.Equal(30 * 512, accumulator);
        }
        finally { RenderPassContext.IsPresentation = false; FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0); }
    }

    [Theory]
    [InlineData(true, false, 512, 600)]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, true, 512, 600)]
    [InlineData(false, true, 0, 0)]
    public void Frozen_clock_preserves_frame_and_duration_debt(bool paused, bool presentation, short duration, short debt)
    {
        VrrModule.TestRate = 60;
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused ? 1 : 0);
        RenderPassContext.IsPresentation = presentation;
        byte* phase = stackalloc byte[4];
        new Span<byte>(phase, 4).Clear();
        *(short*)phase = debt;
        byte* work = stackalloc byte[16];
        *(byte**)(work + 4) = phase;
        NativeCalls.LensDuration = duration;
        NativeCalls.LensCalls = 0;
        try
        {
            new VrrModule().RunLensClockForTest((nint)work);
            Assert.Equal(debt, *(short*)phase);
            Assert.Equal(0, phase[2]);
            Assert.Equal(0, NativeCalls.LensCalls);
        }
        finally
        {
            RenderPassContext.IsPresentation = false;
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
        }
    }

    [Fact]
    public void Uninitialized_work_has_no_clock_to_adjust()
    {
        byte* work = stackalloc byte[16];
        *(nint*)(work + 4) = 0;
        VrrModule.PrepareLensSpriteForTest(0);
        VrrModule.PrepareLensSpriteForTest((nint)work);
    }
}

public unsafe sealed partial class VrrModule
{
    internal static void PrepareLensSpriteForTest(nint work) => prepare_lens_sprite(work);
    internal void RunLensClockForTest(nint work) => h_lens_shape_update(work);
}

internal static partial class EngineAddresses
{
    public const nint KeLnsShpUpdate = 0x36abe0;
}

internal unsafe static partial class NativeCalls
{
    internal static short LensDuration = 20000;
    internal static int LensCalls;
    public static void LensClock(nint work)
    {
        LensCalls++;
        byte* phase = *(byte**)(work + 4);
        *(short*)phase = unchecked((short)(*(short*)phase + 512));
        if (*(short*)phase < LensDuration) return;
        *(short*)phase = unchecked((short)(*(short*)phase - LensDuration));
        phase[2]++;
    }
}
