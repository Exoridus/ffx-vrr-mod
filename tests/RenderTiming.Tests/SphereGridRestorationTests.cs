using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class SphereGridRestorationTests : IDisposable
{
    private static readonly int[] Offsets = [0x112B8, 0x112BC, 0x11308, 0x1130C, 0x11318, 0x1131C, 0x11450, 0x11454, 0x11620];
    private readonly nint _grid = (nint)NativeMemory.AllocZeroed(0x11700);
    private readonly VrrModule _module = new();

    public SphereGridRestorationTests()
    {
        FhUtil.SphereEngine = _grid;
        _module.PreparePass();
        Seed(2, 10);
    }

    public void Dispose()
    {
        FhUtil.SphereEngine = 0;
        NativeMemory.Free((void*)_grid);
        NativeClock.LoopAction = null;
        VrrModule.ClearDiagnosticLoop();
        RenderPassContext.IsPresentation = RenderPassContext.IsActive = false;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Restore_preserves_every_field_changed_before_smoothing(int index)
    {
        Set(index, 20);
        _module.SmoothSphere();
        Assert.Equal(20, Get(index));
        _module.RestoreSphere();
        Assert.Equal(20, Get(index));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Restore_preserves_every_field_changed_after_smoothing(int index)
    {
        _module.SmoothSphere();
        Assert.Equal(4, Get(index));
        Set(index, 20);
        _module.RestoreSphere();
        Assert.Equal(20, Get(index));
    }

    [Fact]
    public void Unchanged_owned_fields_restore_the_native_values_once()
    {
        _module.SmoothSphere();
        foreach (int index in Enumerable.Range(0, 9)) Assert.Equal(4, Get(index));
        Assert.True(_module.SphereSmoothed);
        _module.RestoreSphere();
        foreach (int index in Enumerable.Range(0, 9)) Assert.Equal(10, Get(index));
        Assert.False(_module.SphereSmoothed);
        Set(0, 30);
        _module.RestoreSphere();
        Assert.Equal(30, Get(0));
    }

    [Fact]
    public void Skipping_all_fields_does_not_claim_a_pending_blend()
    {
        Fill(20);
        _module.SmoothSphere();
        Assert.False(_module.SphereSmoothed);
        _module.RestoreSphere();
        foreach (int index in Enumerable.Range(0, 9)) Assert.Equal(20, Get(index));
    }

    [Theory]
    [InlineData(float.NaN, 10f, 0)]
    [InlineData(2f, float.NaN, 0)]
    [InlineData(float.PositiveInfinity, 10f, 0)]
    [InlineData(2f, float.NegativeInfinity, 0)]
    [InlineData(2f, 5000f, 0)]
    [InlineData(1f, 0f, 8)]
    public void Rejected_samples_leave_no_restore_ownership(float previous, float current, int index)
    {
        Seed(previous, current);
        _module.SmoothSphere();
        Assert.Equal(BitConverter.SingleToInt32Bits(current), Bits(index));
        Set(index, 20);
        _module.RestoreSphere();
        Assert.Equal(20, Get(index));
    }

    [Fact]
    public void Apply_rejects_a_bit_distinct_negative_zero()
    {
        Seed(-4, 0);
        SetBits(0, int.MinValue);
        _module.SmoothSphere();
        Assert.Equal(int.MinValue, Bits(0));
        _module.RestoreSphere();
        Assert.Equal(int.MinValue, Bits(0));
    }

    [Fact]
    public void Restore_preserves_a_bit_distinct_late_negative_zero()
    {
        Seed(-1, 3);
        _module.SmoothSphere();
        Assert.Equal(0, Bits(0));
        SetBits(0, int.MinValue);
        _module.RestoreSphere();
        Assert.Equal(int.MinValue, Bits(0));
    }

    [Fact]
    public void Replacement_grid_discards_pending_restoration_even_when_the_old_grid_returns()
    {
        nint replacement = (nint)NativeMemory.AllocZeroed(0x11700);
        try
        {
            _module.SmoothSphere();
            FhUtil.SphereEngine = replacement;
            _module.RestoreSphere();
            Assert.False(_module.SphereSmoothed);
            Assert.Equal(4, Get(0));
            Assert.Equal(0, *(float*)(replacement + Offsets[0]));
            FhUtil.SphereEngine = _grid;
            Set(0, 20);
            _module.RestoreSphere();
            Assert.Equal(20, Get(0));
        }
        finally { FhUtil.SphereEngine = _grid; NativeMemory.Free((void*)replacement); }
    }

    [Fact]
    public void Recording_a_replacement_grid_discards_old_ownership_before_blending_again()
    {
        nint replacement = (nint)NativeMemory.AllocZeroed(0x11700);
        try
        {
            _module.SmoothSphere();
            FhUtil.SphereEngine = replacement;
            foreach (int offset in Offsets) *(float*)(replacement + offset) = 2;
            _module.RecordSphere();
            Assert.False(_module.SphereSmoothed);
            foreach (int offset in Offsets) *(float*)(replacement + offset) = 10;
            _module.RecordSphere();
            _module.SmoothSphere();
            Assert.Equal(4, *(float*)(replacement + Offsets[0]));
            _module.RestoreSphere();
            Assert.Equal(10, *(float*)(replacement + Offsets[0]));
            Assert.Equal(4, Get(0));
        }
        finally { FhUtil.SphereEngine = _grid; NativeMemory.Free((void*)replacement); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Loop_exit_restores_before_other_cleanup_and_the_next_native_capture(bool presentation, bool throws)
    {
        bool restoredBeforeOtherCleanup = false;
        _module.DiagnosticCleanup = () => restoredBeforeOtherCleanup = Get(0) == 10;
        var error = new InvalidOperationException("native fixture");
        NativeClock.LoopAction = _ =>
        {
            RenderPassContext.IsPresentation = presentation;
            _module.SmoothSphere();
            Assert.Equal(4, Get(0));
            if (throws) throw error;
        };
        if (throws) Assert.Same(error, Assert.Throws<InvalidOperationException>(() => _module.CallDiagnosticLoop(1f / 60)));
        else _module.CallDiagnosticLoop(1f / 60);
        Assert.True(restoredBeforeOtherCleanup);
        Assert.Equal(10, Get(0));
        Assert.False(_module.SphereSmoothed);
        _module.RecordSphere();
        _module.SmoothSphere();
        Assert.Equal(10, Get(0));
    }

    private void Seed(float previous, float current)
    {
        Fill(previous);
        _module.RecordSphere();
        Fill(current);
        _module.RecordSphere();
    }

    private void Fill(float value)
    {
        for (int i = 0; i < Offsets.Length; i++) Set(i, value);
    }

    private float Get(int index) => *(float*)(_grid + Offsets[index]);
    private void Set(int index, float value) => *(float*)(_grid + Offsets[index]) = value;
    private int Bits(int index) => *(int*)(_grid + Offsets[index]);
    private void SetBits(int index, int bits) => *(int*)(_grid + Offsets[index]) = bits;
}

public unsafe sealed partial class VrrModule
{
    internal void RecordSphere() => sphere_record_update();
    internal void SmoothSphere() => sphere_smooth(true);
    internal void RestoreSphere() => sphere_smooth_restore();
    internal bool SphereSmoothed => _sphereSmoothed;
}
