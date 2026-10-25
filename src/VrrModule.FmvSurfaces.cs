namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private static readonly (nint Operand, nint Original, bool Width)[] FmvSurfaceOperands =
    [
        (0x2D9E74, 0x74A1AC, true),
        (0x2D9E80, 0x74A1A0, false),
        (0x2D9E88, 0x74A1A8, true),
        (0x2D9E94, 0x74A198, false),
    ];

    private nint _fmv_surface_values;
    private double _active_fmv_rate = 30000d / 1001;

    private void select_fmv_surface(FmvReplacementMetadata? metadata)
    {
        if (metadata is not null && _fmv_surface_values == 0)
        {
            foreach (var site in FmvSurfaceOperands)
            {
                byte* operand = FhUtil.ptr_at<byte>(site.Operand);
                if (operand[-2] != 0xd9 || operand[-1] != 0x05
                    || *(uint*)operand != (uint)(nint)FhUtil.ptr_at<float>(site.Original))
                    throw new InvalidOperationException($"FMV surface operand mismatch at RVA 0x{site.Operand:X}.");
            }

            // Image operands reference this storage until the patch journal restores them.
            _fmv_surface_values = (nint)NativeMemory.Alloc(8);
        }

        if (_fmv_surface_values != 0)
        {
            if (metadata is not null)
            {
                *(float*)_fmv_surface_values = metadata.Width;
                *(float*)(_fmv_surface_values + 4) = metadata.Height;
            }

            foreach (var site in FmvSurfaceOperands)
            {
                nint target = metadata is null
                    ? (nint)FhUtil.ptr_at<float>(site.Original)
                    : _fmv_surface_values + (site.Width ? 0 : 4);
                _patches.Write(site.Operand, BitConverter.GetBytes((uint)target));
            }
        }

        _active_fmv_rate = metadata?.FrameRate ?? 30000d / 1001;
        _video_carry = 0;
    }

    private void release_fmv_surfaces()
    {
        NativeMemory.Free((void*)_fmv_surface_values);
        _fmv_surface_values = 0;
    }

    private static bool advance_source_frame(ref double carry, double sourceRate, double presentRate)
    {
        // High-rate replacements need a queued-frame upload on every available render call.
        // Decoder metadata uses NTSC rates; the correction clock uses nominal 30/60/120 rates.
        double nominalSourceRate = Math.Round(sourceRate * 1001.0 / 1000.0);
        if (sourceRate > 30 || presentRate <= nominalSourceRate) return true;
        carry += nominalSourceRate / presentRate;
        if (carry < 1) return false;
        carry -= 1;
        return true;
    }
}
