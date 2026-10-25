using System.Collections.Concurrent;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class RenderImageIdentityTests
{
    [Fact]
    public void Images_receive_increasing_identities_before_refusal()
    {
        var admission = new PresentationAdmission();
        var first = admission.BeginImage();
        var second = admission.BeginImage();
        Assert.True(first.Serial > 0);
        Assert.Equal(first.Serial + 1, second.Serial);
        Assert.False(first.IsRejected);
        Assert.False(second.IsRejected);
    }

    [Fact]
    public void Refusal_rejects_all_pending_images_but_allows_later_native_images()
    {
        var admission = new PresentationAdmission();
        var pending = Enumerable.Range(0, 4).Select(_ => admission.BeginImage()).ToArray();
        Assert.True(admission.Refuse("producer_changed"));
        Assert.All(pending, image => Assert.True(image.IsRejected));
        var native = admission.BeginImage();
        Assert.False(native.IsRejected);
        Assert.Equal("producer_changed", admission.Failure);
        Assert.All(pending, image => Assert.True(image.IsRejected));
    }

    [Fact]
    public void Later_refusals_cannot_extend_the_rejected_image_boundary()
    {
        var admission = new PresentationAdmission();
        var rejected = admission.BeginImage();
        Assert.True(admission.Refuse("first"));
        var native = admission.BeginImage();
        Assert.False(admission.Refuse("second"));
        Assert.True(rejected.IsRejected);
        Assert.False(native.IsRejected);
        Assert.Equal("first", admission.Failure);
    }

    [Fact]
    public void Admission_instances_keep_rejection_and_pending_image_lifetimes_separate()
    {
        var oldAdmission = new PresentationAdmission();
        var oldImage = oldAdmission.BeginImage();
        var newAdmission = new PresentationAdmission();
        var newImage = newAdmission.BeginImage();
        Assert.Equal(oldImage.Serial, newImage.Serial);
        Assert.True(oldAdmission.Refuse("old_owner"));
        Assert.True(oldImage.IsRejected);
        Assert.False(newImage.IsRejected);
        Assert.Null(newAdmission.Failure);
        Assert.False(oldAdmission.BeginImage().IsRejected);
        Assert.True(oldImage.IsRejected);
    }

    [Fact]
    public async Task Pending_image_observes_refusal_published_on_another_thread()
    {
        var admission = new PresentationAdmission();
        var image = admission.BeginImage();
        await Task.Run(() => Assert.True(admission.Refuse("worker")));
        Assert.True(image.IsRejected);
        Assert.Equal("worker", admission.Failure);
    }

    [Fact]
    public void Concurrent_issuance_and_refusal_publish_one_contiguous_rejection_boundary()
    {
        for (int round = 0; round < 32; round++)
        {
            var admission = new PresentationAdmission();
            var first = admission.BeginImage();
            var issued = new ConcurrentBag<PresentationAdmission.Image>();
            Parallel.For(0, 128, index =>
            {
                var image = admission.BeginImage();
                issued.Add(image);
                if (index == 64) Assert.True(admission.Refuse("worker"));
            });
            var last = admission.BeginImage();
            var images = issued.Append(first).Append(last).OrderBy(image => image.Serial).ToArray();
            Assert.Equal(Enumerable.Range(1, 130).Select(value => (long)value), images.Select(image => image.Serial));
            Assert.True(first.IsRejected);
            Assert.False(last.IsRejected);
            long boundary = images.Last(image => image.IsRejected).Serial;
            Assert.All(images, image => Assert.Equal(image.Serial <= boundary, image.IsRejected));
            Assert.Equal("worker", admission.Failure);
        }
    }
}
