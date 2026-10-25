using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class ParticleUvClockTests
{
    [Theory]
    [InlineData(0x338ec0, 0xb8, 0x56, "U4vcg+wIg+Twg8QEVYtrBIlsJASL7IHsuAAAAKHYE8YAM8WJRfyDPTT9MAIAi0MQi1MIVotzDFeLeAyJlVD///+LTwiJtVT///+JjVj///8Phd0AAADZhBGoAAAA2IQRpAAAANmdXP///9mFXP///9mUEaQAAADZhBGgAAAA2ME=")]
    [InlineData(0x33a370, 0xf8, 0x5c, "U4vcg+wIg+Twg8QEVYtrBIlsJASL7IHs+AAAAKHYE8YAM8WJRfyDPTT9MAIAi0MQi1MIVotzDFeLeAyJlRT///+LTwiJtRj///+JhQz///+JjRD///8Phd0AAADZhBGoAAAA2IQRpAAAANmdHP///9mFHP///9mUEaQAAADZhBE=")]
    public void Native_prefix_accepts_the_verified_contract_and_rejects_a_changed_argument(int rva, byte frame, int integration, string prefix)
    {
        byte* entry = FhUtil.ptr_at<byte>(rva);
        Convert.FromBase64String(prefix).CopyTo(new Span<byte>(entry, 128));
        *(uint*)(entry + 0x28) = (uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.PpvUserStopPartF);
        Assert.True(VrrModule.ParticleUvBindingForTest(rva, frame, integration));
        entry[0x2f] = 0x0c;
        Assert.False(VrrModule.ParticleUvBindingForTest(rva, frame, integration));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_pause_prevents_private_state_access_on_every_image(bool presentation)
    {
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = presentation;
        var module = new VrrModule();
        try
        {
            module.PrepareParticleUvForTest(0, 0);
            Assert.Empty(module.ParticleUvErrorsForTest);
        }
        finally
        {
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
            RenderPassContext.IsPresentation = false;
            RenderPassContext.IsActive = false;
        }
    }

    [Fact]
    public void Unowned_extra_image_reports_once_without_prewarping()
    {
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
        RenderPassContext.IsPresentation = true;
        var module = new VrrModule();
        try
        {
            module.PrepareParticleUvForTest(0, 0);
            module.PrepareParticleUvForTest(0, 0);
            Assert.Single(module.ParticleUvErrorsForTest);
        }
        finally { RenderPassContext.IsPresentation = false; }
    }

    [Fact]
    public void Native_adapter_resolves_both_axes_through_the_program_descriptor()
    {
        byte* obj = stackalloc byte[0x100];
        byte* program = stackalloc byte[0x20];
        byte* descriptor = stackalloc byte[0x10];
        *(nint*)(program + 0xc) = (nint)descriptor;
        *(int*)(descriptor + 8) = 0x20;
        Span<float> channels = new(obj + 0xc0, 6);
        new float[] { 4, 3, 2, 10, -5, -2 }.CopyTo(channels);
        VrrModule.TestRate = 60;
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
        RenderPassContext.IsPresentation = false;
        new VrrModule().PrepareParticleUvForTest((nint)obj, (nint)program);
        for (int i = 0; i < 6; i += 3)
        {
            channels[i + 1] += channels[i + 2];
            channels[i] += channels[i + 1];
        }
        Assert.Equal(new float[] { 6.25f, 4, 2, 6.75f, -6, -2 }, channels.ToArray());
    }

    [Fact]
    public void Paused_native_draw_does_not_prewarp_either_axis()
    {
        float[] channels = [4, 3, 2, 10, -5, -2];
        ParticleUvClock.Prepare(channels, 0, true);
        Assert.Equal(new float[] { 4, 3, 2, 10, -5, -2 }, channels);
    }

    [Fact]
    public void Both_private_axes_use_the_fractional_step()
    {
        float[] channels = [4, 3, 2, 10, -5, -2];
        ParticleUvClock.Prepare(channels, 0.5f, false);
        for (int i = 0; i < 6; i += 3)
        {
            channels[i + 1] += channels[i + 2];
            channels[i] += channels[i + 1];
        }
        Assert.Equal(new float[] { 6.25f, 4, 2, 6.75f, -6, -2 }, channels);
    }

    [Theory]
    [InlineData(4f, 3f, 2f)]
    [InlineData(-2f, -5f, -3f)]
    [InlineData(100f, 0.5f, 0f)]
    public void Two_half_steps_equal_one_authored_step(float position, float velocity, float acceleration)
    {
        float expectedPosition = position + velocity + acceleration;
        float expectedVelocity = velocity + acceleration;
        Step(ref position, ref velocity, acceleration, 0.5f);
        Step(ref position, ref velocity, acceleration, 0.5f);
        Assert.Equal(expectedPosition, position, 5);
        Assert.Equal(expectedVelocity, velocity, 5);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Keyframe_additions_follow_integration_without_being_scaled(float elapsed)
    {
        float position = 4, velocity = 3, acceleration = 2;
        Step(ref position, ref velocity, acceleration, elapsed);
        position += 7;
        velocity += 11;
        acceleration += 13;
        Assert.Equal(4 + 3 * elapsed + 2 * elapsed * (elapsed + 1) / 2 + 7, position, 5);
        Assert.Equal(3 + 2 * elapsed + 11, velocity, 5);
        Assert.Equal(15, acceleration);
    }

    [Fact]
    public void Full_step_does_not_rewrite_private_state()
    {
        float position = 4, velocity = 3;
        ParticleUvClock.Prepare(ref position, ref velocity, 2, 1);
        Assert.Equal(4, position);
        Assert.Equal(3, velocity);
    }

    [Fact]
    public void Zero_step_cancels_the_native_integration()
    {
        float position = 4, velocity = 3;
        Step(ref position, ref velocity, 2, 0);
        Assert.Equal(4, position);
        Assert.Equal(3, velocity);
    }

    private static void Step(ref float position, ref float velocity, float acceleration, float elapsed)
    {
        ParticleUvClock.Prepare(ref position, ref velocity, acceleration, elapsed);
        velocity += acceleration;
        position += velocity;
    }
}

public unsafe sealed partial class VrrModule
{
    internal static bool ParticleUvBindingForTest(nint rva, byte frame, int integration) => particle_uv_binding_matches(rva, frame, integration);
    internal void PrepareParticleUvForTest(nint obj, nint program) => prepare_particle_uv(obj, program);
    internal IReadOnlyList<string> ParticleUvErrorsForTest => _logger.Errors;
}

internal static partial class EngineAddresses
{
    public const nint PpvUserStopPartF = 0x30540;
}
