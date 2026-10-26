using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class TexturePresentationTests
{
    private static void Prepare(TextureClockMemory data)
    {
        *(short*)(data.Record + 0x1c) = 3;
        *(short*)(data.Record + 0xe) = 100;
        *(float*)(data.Record + 0x28) = -20;
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.BeginImage(1d / 60);
        RenderPassContext.SetPass(0.5f, false);
    }

    [Fact]
    public void Actual_capture_and_final_image_output_leave_all_canonical_bytes_unchanged()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        byte[] before = new ReadOnlySpan<byte>(data.Record - 8, 0xd4c).ToArray();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        Assert.Empty(module.PresentUvForTest(data.Slot, true, false));
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.4f);
        Assert.Equal(0.3f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)), 5);
        Assert.Equal(before, new ReadOnlySpan<byte>(data.Record - 8, 0xd4c).ToArray());
        Assert.Empty(module.PresentUvForTest(data.Slot, true, true));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Extra_image_without_native_texture_call_publishes_intermediate_output()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.4f);
        module.PresentUvForTest(data.Slot, true, true);
        RenderPassContext.BeginImage(1d / 144);
        RenderPassContext.IsPresentation = true;
        RenderPassContext.SetPass(0.75f, true);
        Assert.Equal(0.35f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)), 5);
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.9f);
        RenderPassContext.BeginImage(1d / 144);
        Assert.Equal(0.35f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)), 5);
        RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
    }

    [Fact]
    public void Native_owner_handoff_discards_previous_visual_history()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        Assert.Empty(module.PresentUvForTest(data.Slot, false, false));
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.8f);
        Assert.Equal(0.8f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Recycled_work_or_changed_height_is_rejected_before_native_output()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        *(short*)(data.Record + 0xe) = 200;
        Assert.Empty(module.PresentUvForTest(data.Slot, true, true));
        RenderPassContext.BeginImage(1d / 60);
        *(short*)(data.Record + 0xe) = 100;
        *(uint*)(data.Slot + 4) = (uint)(nuint)(data.OtherRecord - 8);
        Assert.Empty(module.PresentUvForTest(data.Slot, true, true));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Same_address_lifecycle_reuse_starts_without_previous_motion()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        module.InvalidateUvForTest(0);
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.8f);
        Assert.Equal(0.8f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Long_gap_reset_with_adjacent_image_serial_does_not_bridge_history()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        long image = RenderPassContext.ImageSerial;
        RenderPassContext.BeginImage(0.25);
        Assert.Equal(image + 1, RenderPassContext.ImageSerial);
        RenderPassContext.ResetHistory();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.8f);
        Assert.Equal(0.8f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Epoch_reset_before_extra_image_discards_samples_without_a_native_texture_call()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        RenderPassContext.BeginImage(1d / 144);
        RenderPassContext.IsPresentation = true;
        RenderPassContext.ResetHistory();
        Assert.Empty(module.PresentUvForTest(data.Slot, true, true));
        RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
    }

    [Fact]
    public void Skipped_owner_images_do_not_bridge_old_samples_on_reentry()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        RenderPassContext.BeginImage(0.1);
        RenderPassContext.BeginImage(0.1);
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.8f);
        Assert.Equal(0.8f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Live_actor_association_rejects_a_replaced_renderer_or_slot()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        byte* actor = data.OtherRecord;
        actor[2] = 1;
        *(uint*)(actor + 0x6fc) = 1;
        *(int*)(actor + 0x700) = 0;
        *(uint*)(actor + 0x830) = (uint)(nuint)data.Shadow;
        Assert.True(VrrModule.ActorUvMatchesForTest(actor, data.Slot, data.Shadow));
        *(int*)(actor + 0x700) = 1;
        Assert.False(VrrModule.ActorUvMatchesForTest(actor, data.Slot, data.Shadow));
        *(int*)(actor + 0x700) = 0;
        *(uint*)(actor + 0x830) = 0;
        Assert.False(VrrModule.ActorUvMatchesForTest(actor, data.Slot, data.Shadow));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Horizontal_canonical_motion_does_not_change_the_native_zero_U_output()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        *(float*)(data.Record + 0x24) = 12;
        *(float*)(data.Record + 0x2c) = 55;
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        Assert.Equal(0, module.PresentUvUForTest(data.Slot));
        Assert.Equal(55, *(float*)(data.Record + 0x2c));
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Sprite_pause_does_not_invent_a_scroll_pause()
    {
        using var data = new TextureClockMemory();
        Prepare(data);
        data.Slot[0xe] = 0;
        var module = new VrrModule();
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.2f);
        RenderPassContext.BeginSimulation();
        module.CaptureUvForTest(data.Slot, data.OtherRecord, 0.4f);
        Assert.Equal(0.3f, Assert.Single(module.PresentUvForTest(data.Slot, true, true)), 5);
        Assert.Equal(0, data.Slot[0xe]);
        RenderPassContext.IsActive = false;
    }
}

public unsafe sealed partial class VrrModule
{
    private bool IndependentPresentationEnabled => _config.VrrEnabled;
    internal void CaptureUvForTest(byte* slot, byte* renderer, float v)
        => capture_texture_uv(slot, 0, 0, 1, 0, v, (uint)(nuint)renderer);
    internal void InvalidateUvForTest(int slot) => invalidate_texture_uv(slot);
    internal static bool ActorUvMatchesForTest(byte* actor, byte* slot, byte* renderer)
        => texture_uv_actor_matches(actor, 1, new(0, 0, 1, *(uint*)slot, *(uint*)(slot + 4), (uint)(nuint)renderer, 100));
    internal float PresentUvUForTest(byte* slot)
    {
        float result = float.NaN;
        present_texture_uv(true, true, target => texture_uv_slot_matches(slot, target), (_, u, _) => result = u);
        return result;
    }
    internal List<float> PresentUvForTest(byte* slot, bool active, bool final)
    {
        List<float> outputs = [];
        present_texture_uv(active, final, target => texture_uv_slot_matches(slot, target), (_, _, v) => outputs.Add(v));
        return outputs;
    }
}
