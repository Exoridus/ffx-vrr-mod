using System.Numerics;
using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Vrr;

internal readonly record struct RenderCameraIdentity(int Source, uint Camera, uint Source2, uint Source3);
internal readonly record struct RenderCameraSample(Matrix4x4 View, Vector3 Projection, RenderCameraIdentity Identity);

internal sealed class RenderCameraHistory
{
    private sealed class Viewport
    {
        internal RenderCameraSample Previous, Current;
        internal long Generation;
        internal bool Valid, Pair, StepValid;
        internal float StepDistance, StepAngle;
        internal bool PreviousStepValid;
        internal float PreviousStepDistance, PreviousStepAngle;
    }

    private readonly Viewport[] _viewports = [new(), new(), new()];
    private long _generation;

    internal void BeginSimulation() => _generation++;

    internal void CompleteSimulation()
    {
        foreach (var state in _viewports)
            if (state.Generation != _generation) state.Valid = state.Pair = state.StepValid = false;
    }

    internal void Capture(int viewport, RenderCameraSample sample)
    {
        if ((uint)viewport >= _viewports.Length) return;
        var state = _viewports[viewport];
        if (state.Generation != _generation)
        {
            state.Previous = state.Current;
            state.Pair = state.Valid && state.Generation == _generation - 1;
            state.PreviousStepValid = state.StepValid;
            state.PreviousStepDistance = state.StepDistance;
            state.PreviousStepAngle = state.StepAngle;
            state.Generation = _generation;
        }
        state.Current = sample;
        state.Valid = TryPose(sample.View, out _, out _) && ValidProjection(sample.Projection);
        if (!state.Valid || state.Previous.Identity != sample.Identity) state.Pair = false;
        state.StepValid = false;
        if (!state.Pair) return;
        TryPose(state.Previous.View, out var previousEye, out var previousRotation);
        TryPose(sample.View, out var currentEye, out var currentRotation);
        float distance = Vector3.Distance(previousEye, currentEye);
        float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(previousRotation, currentRotation)), 0, 1)) * 180 / MathF.PI;
        bool jump = state.PreviousStepValid
            && ((distance > 20 && distance > 4 * state.PreviousStepDistance)
                || (angle > 10 && angle > 4 * state.PreviousStepAngle));
        if (distance > 500 || angle > 60 || jump || ProjectionCut(state.Previous.Projection, sample.Projection))
        {
            state.Pair = false;
            return;
        }
        state.StepValid = true;
        state.StepDistance = distance;
        state.StepAngle = angle;
    }

    internal void Reset()
    {
        foreach (var state in _viewports) state.Valid = state.Pair = state.StepValid = false;
    }

    internal bool TryInterpolate(int viewport, RenderCameraSample canonical, float alpha, out RenderCameraSample sample)
    {
        sample = canonical;
        if ((uint)viewport >= _viewports.Length || !float.IsFinite(alpha)) return false;
        var state = _viewports[viewport];
        if (!state.Valid || !state.Pair || state.Current.Identity != canonical.Identity
            || !RenderCameraWrite.BitwiseEqual(state.Current.View, canonical.View)
            || state.Current.Projection != canonical.Projection) return false;
        TryPose(state.Previous.View, out var previousEye, out var previousRotation);
        TryPose(state.Current.View, out var currentEye, out var currentRotation);
        alpha = Math.Clamp(alpha, 0, 1);
        var world = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(previousRotation, currentRotation, alpha));
        world.Translation = Vector3.Lerp(previousEye, currentEye, alpha);
        if (!Matrix4x4.Invert(world, out var view)) return false;
        sample = canonical with { View = view, Projection = Vector3.Lerp(state.Previous.Projection, canonical.Projection, alpha) };
        return true;
    }

    private static bool ValidProjection(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            && value.X > 0 && value.Y > 0 && value.Z > 0;

    private static bool ProjectionCut(Vector3 previous, Vector3 current)
        => MathF.Abs(previous.X - current.X) > .25f * MathF.Max(MathF.Abs(previous.X), MathF.Abs(current.X))
            || MathF.Abs(previous.Y - current.Y) > .25f * MathF.Max(MathF.Abs(previous.Y), MathF.Abs(current.Y))
            || MathF.Abs(previous.Z - current.Z) > .25f * MathF.Max(MathF.Abs(previous.Z), MathF.Abs(current.Z));

    private static bool TryPose(Matrix4x4 view, out Vector3 eye, out Quaternion rotation)
    {
        eye = default;
        rotation = default;
        var values = MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref view, 1));
        foreach (float value in values) if (!float.IsFinite(value)) return false;
        if (MathF.Abs(view.M14) > .001f || MathF.Abs(view.M24) > .001f || MathF.Abs(view.M34) > .001f
            || MathF.Abs(view.M44 - 1) > .001f) return false;
        var x = new Vector3(view.M11, view.M12, view.M13);
        var y = new Vector3(view.M21, view.M22, view.M23);
        var z = new Vector3(view.M31, view.M32, view.M33);
        if (MathF.Abs(x.LengthSquared() - 1) > .01f || MathF.Abs(y.LengthSquared() - 1) > .01f
            || MathF.Abs(z.LengthSquared() - 1) > .01f || MathF.Abs(Vector3.Dot(x, y)) > .01f
            || MathF.Abs(Vector3.Dot(x, z)) > .01f || MathF.Abs(Vector3.Dot(y, z)) > .01f
            || Vector3.Dot(Vector3.Cross(x, y), z) < .99f || !Matrix4x4.Invert(view, out var world)) return false;
        eye = world.Translation;
        rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
        return true;
    }
}

internal sealed class RenderCameraWrite
{
    private Matrix4x4 _canonical, _written;
    private bool _owns;

    internal void Record(Matrix4x4 canonical, Matrix4x4 written)
    {
        _canonical = canonical;
        _written = written;
        _owns = true;
    }

    internal bool TryRestore(Matrix4x4 live, out Matrix4x4 canonical)
    {
        canonical = live;
        bool restore = _owns && BitwiseEqual(live, _written);
        _owns = false;
        if (restore) canonical = _canonical;
        return restore;
    }

    internal static bool BitwiseEqual(Matrix4x4 first, Matrix4x4 second)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref first, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref second, 1)));
}
