using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class RenderImageReadinessTests
{
    [Fact]
    public void Failed_atomic_image_commit_keeps_the_actual_scheduler_on_native_clock()
    {
        var module = new VrrModule { CommitImage = false };
        module.PreparePass();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(0, module.AdmittedImages);
        Assert.Equal(60, module.ReportedRate);
    }

    [Fact]
    public void Only_a_fully_admitted_image_marks_independent_tracking()
    {
        var module = new VrrModule();
        module.PreparePass();
        module.BeginFrame();
        Assert.Equal(1, module.AdmittedImages);
        module.ProducerQualified = false;
        module.BeginFrame();
        Assert.Equal(1, module.AdmittedImages);
    }

    [Fact]
    public void Missing_image_identity_keeps_new_frame_on_native_clock()
    {
        var module = new VrrModule { ImageReady = false };
        module.PreparePass();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
    }

    [Fact]
    public void Identity_loss_between_admission_and_clock_releases_independent_pass()
    {
        var module = new VrrModule();
        module.PreparePass();
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
        module.ImageReady = false;
        module.RunClock();
        Assert.False(RenderPassContext.IsActive);
        Assert.False(RenderPassContext.IsPresentation);
        Assert.Equal(60, module.ReportedRate);
    }
}
