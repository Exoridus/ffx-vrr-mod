using System.Numerics;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    readonly RenderInterpolator _interpolator = new();

    void init_interpolation()
    {
        // TargetFramerate (not _config.TargetFramerate - the config only carries an optional
        // override, TargetFramerateOverride) is this module's own runtime-resolved rate: the
        // measured display rate, snapped to the nearest nominal value, or the override if set.
        _interpolator.SetTickPeriod(1.0 / TargetFramerate);
    }

    // Call this at the end of the existing per-tick hook (h_frame), once camera/character state for
    // this tick has already been written by the ported (unmodified) tick-layer hooks.
    void snapshot_interpolated_transforms()
    {
        _interpolator.Snapshot("camera", read_camera_transform());
        _interpolator.Snapshot("character", read_character_transform());
    }

    /// <summary>
    ///     PLACEHOLDER. No hook in this mod (ported unchanged from ffx-fpsunlock-mod) reads a live
    ///     camera world-space transform - MsCameraMoveFrame/MsCameraMoveAcc only retime scripted move
    ///     *durations* in frames, they never expose position/rotation. Finding the real camera struct
    ///     and its position/rotation field offsets is unstarted reverse-engineering work, not covered
    ///     by this task. Returns a fixed identity transform so the interpolation pipeline is
    ///     structurally wired and testable; it does not yet reflect real camera motion. Replace this
    ///     with a real read once the offsets are found (check ffx-kb first, per this project's Rule 0).
    /// </summary>
    Transform read_camera_transform() => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>
    ///     PLACEHOLDER, same reasoning as <see cref="read_camera_transform"/>. The one Chr struct
    ///     field named "pos" found in canonical/ffx/engine/data_types/structs.json (offset 1278) is
    ///     almost certainly the battle row/formation position (front/back), not a 3D world-space
    ///     coordinate - it sits between narrow status/battle fields, not among any of the struct's
    ///     other float triples. Do not treat it as a world position without independent confirmation.
    /// </summary>
    Transform read_character_transform() => new(Vector3.Zero, Quaternion.Identity);

    /// <summary>PLACEHOLDER counterpart to the read functions above - see their remarks.</summary>
    void write_camera_transform(Transform value) { }

    /// <summary>PLACEHOLDER counterpart to the read functions above - see their remarks.</summary>
    void write_character_transform(Transform value) { }

    public bool GetInterpolatedCameraAndCharacterTransforms(
        double timeSinceLastTickSeconds, out Transform camera, out Transform character)
    {
        bool camBlended  = _interpolator.TryGetInterpolated("camera", timeSinceLastTickSeconds, out camera);
        bool charBlended = _interpolator.TryGetInterpolated("character", timeSinceLastTickSeconds, out character);
        return camBlended || charBlended;
    }
}
