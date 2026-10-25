using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class BattleBlinkClockTests
{
    [Fact]
    public void Foreign_operand_is_never_replaced()
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        FhUtil.set_at(EngineAddresses.BattleCursorBlinkGate + 2, 0x12345678u);
        RenderPassContext.IsActive = true;
        RenderPassContext.BeginImage(1d / 120);
        try
        {
            module.PatchBlinkForTest();
            Assert.Empty(module._patches.Values);
            Assert.Equal(0x12345678u, FhUtil.get_at<uint>(EngineAddresses.BattleCursorBlinkGate + 2));
        }
        finally { RenderPassContext.IsActive = false; }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void Cursor_has_fifteen_cycles_per_second_at_either_image_rate(int images)
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        RenderPassContext.IsActive = true;
        try
        {
            for (int i = 0; i < images; i++)
            {
                RenderPassContext.BeginImage(1d / images);
                module.PatchBlinkForTest();
                Assert.Equal((i + 1) * 30 / images % 2, module.BlinkPhaseForTest & 1);
            }
            Assert.Equal(1, FhUtil.get_at<byte>(EngineAddresses.BattleCursorBlinkGate + 6));
        }
        finally { RenderPassContext.IsActive = false; }
    }

    [Fact]
    public void Irregular_images_use_elapsed_time_and_never_rewrite_the_shared_counter()
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        FhUtil.set_at(EngineAddresses.SgCount, 100u);
        RenderPassContext.IsActive = true;
        try
        {
            double elapsed = 0;
            foreach (double seconds in new[] { 0.003, 0.01, 0.02, 0.007, 0.021, 0.005, 0.034 })
            {
                elapsed += seconds;
                RenderPassContext.BeginImage(seconds);
                module.PatchBlinkForTest();
                Assert.Equal((int)Math.Floor(elapsed * 30 + 1e-9) % 2, module.BlinkPhaseForTest & 1);
                Assert.Equal(100u, FhUtil.get_at<uint>(EngineAddresses.SgCount));
            }
        }
        finally { RenderPassContext.IsActive = false; }
    }

    [Fact]
    public void Native_owner_restores_native_operand_and_resets_fractional_render_phase()
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        RenderPassContext.IsActive = true;
        try
        {
            RenderPassContext.BeginImage(0.03);
            module.PatchBlinkForTest();
            RenderPassContext.IsActive = false;
            module.PatchBlinkForTest();
            Assert.Equal((uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.SgCount), FhUtil.get_at<uint>(EngineAddresses.BattleCursorBlinkGate + 2));
            Assert.Equal(2, FhUtil.get_at<byte>(EngineAddresses.BattleCursorBlinkGate + 6));
            RenderPassContext.IsActive = true;
            RenderPassContext.BeginImage(0.01);
            module.PatchBlinkForTest();
            Assert.Equal(0, module.BlinkPhaseForTest & 1);
        }
        finally { RenderPassContext.IsActive = false; }
    }
}

public unsafe sealed partial class VrrModule
{
    internal void PatchBlinkForTest() => patch_battle_cursor_blink();
    internal byte BlinkPhaseForTest => _blink_render_phase == null ? (byte)0 : *_blink_render_phase;
}
