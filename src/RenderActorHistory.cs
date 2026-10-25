using System.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Fahrenheit.Mods.Vrr;

internal readonly record struct RenderActorIdentity(long Group, long Mesh, long Palette, long World);

internal sealed class RenderActorWrite(int bones)
{
    private readonly byte[] _canonicalWorld = new byte[48], _writtenWorld = new byte[48];
    private readonly Matrix4x4[] _canonicalBones = new Matrix4x4[bones], _writtenBones = new Matrix4x4[bones];
    private bool _owns;

    internal void Record(ReadOnlySpan<byte> canonicalWorld, ReadOnlySpan<byte> writtenWorld,
        ReadOnlySpan<Matrix4x4> canonicalBones, ReadOnlySpan<Matrix4x4> writtenBones)
    {
        canonicalWorld.CopyTo(_canonicalWorld);
        writtenWorld.CopyTo(_writtenWorld);
        canonicalBones.CopyTo(_canonicalBones);
        writtenBones.CopyTo(_writtenBones);
        _owns = true;
    }

    internal int Restore(Span<byte> world, Span<Matrix4x4> palette)
    {
        if (!_owns) return 0;
        _owns = false;
        int restored = 0;
        if (world.SequenceEqual(_writtenWorld)) { _canonicalWorld.CopyTo(world); restored++; }
        for (int i = 0; i < palette.Length; i++)
            if (RenderCameraWrite.BitwiseEqual(palette[i], _writtenBones[i])) { palette[i] = _canonicalBones[i]; restored++; }
        return restored;
    }
}

internal sealed class RenderActorHistory
{
    private sealed class Entry
    {
        internal RenderActorIdentity Identity;
        internal Matrix4x4 PreviousRoot, Root;
        internal Matrix4x4[] PreviousBones = [], Bones = [];
        internal long Generation;
        internal bool Pair;
        internal float Step, PreviousStep;
        internal bool PreviousStepValid;
    }

    private readonly Dictionary<long, Entry> _entries = [];
    private long _generation;

    internal void BeginSimulation() => _generation++;

    internal void CompleteSimulation()
    {
        foreach (long key in _entries.Where(pair => pair.Value.Generation != _generation).Select(pair => pair.Key).ToArray())
            _entries.Remove(key);
    }

    internal void Reset() => _entries.Clear();

    internal void Capture(long key, RenderActorIdentity identity, Matrix4x4 root, ReadOnlySpan<Matrix4x4> bones)
    {
        if (!_entries.TryGetValue(key, out var entry)) _entries[key] = entry = new();
        if (entry.Generation != _generation)
        {
            bool pair = entry.Generation == _generation - 1 && entry.Identity == identity && entry.Bones.Length == bones.Length;
            entry.PreviousStepValid = entry.Pair;
            entry.PreviousStep = entry.Step;
            entry.Pair = pair;
            entry.PreviousRoot = entry.Root;
            (entry.PreviousBones, entry.Bones) = (entry.Bones, entry.PreviousBones);
            entry.Generation = _generation;
        }
        if (entry.Identity != identity || (entry.Pair && entry.PreviousBones.Length != bones.Length)) entry.Pair = false;
        entry.Identity = identity;
        entry.Root = root;
        if (entry.Bones.Length != bones.Length) entry.Bones = new Matrix4x4[bones.Length];
        bones.CopyTo(entry.Bones);
        entry.Step = Vector3.Distance(entry.PreviousRoot.Translation, root.Translation);
        if (entry.Step > 500 || (entry.PreviousStepValid && entry.Step > 30 && entry.Step > 3 * entry.PreviousStep))
            entry.Pair = false;
    }

    internal bool TryInterpolate(long key, RenderActorIdentity identity, Matrix4x4 root, ReadOnlySpan<Matrix4x4> bones,
        float alpha, out Matrix4x4 renderedRoot, Span<Matrix4x4> renderedBones)
    {
        renderedRoot = root;
        if (!float.IsFinite(alpha) || !_entries.TryGetValue(key, out var entry) || !entry.Pair || entry.Identity != identity
            || entry.Bones.Length != bones.Length || renderedBones.Length != bones.Length
            || !RenderCameraWrite.BitwiseEqual(entry.Root, root) || !Equal(entry.Bones, bones)) return false;
        alpha = Math.Clamp(alpha, 0, 1);
        if (!TryBlend(entry.PreviousRoot, root, alpha, out var blendedRoot)) return false;
        for (int i = 0; i < bones.Length; i++)
            if (!TryBlend(entry.PreviousBones[i], bones[i], alpha, out renderedBones[i])) return false;
        renderedRoot = blendedRoot;
        return true;
    }

    internal static bool Equal(ReadOnlySpan<Matrix4x4> first, ReadOnlySpan<Matrix4x4> second)
        => MemoryMarshal.AsBytes(first).SequenceEqual(MemoryMarshal.AsBytes(second));

    internal bool TryInterpolateCurrent(long key, RenderActorIdentity identity, ReadOnlySpan<Matrix4x4> liveBones,
        float alpha, out Matrix4x4 root, Span<Matrix4x4> renderedBones)
    {
        root = default;
        return _entries.TryGetValue(key, out var entry)
            && TryInterpolate(key, identity, entry.Root, liveBones, alpha, out root, renderedBones);
    }

    private static bool TryBlend(Matrix4x4 previous, Matrix4x4 current, float alpha, out Matrix4x4 result)
    {
        result = current;
        if (!Affine(previous) || !Affine(current)) return false;
        if (alpha >= 1 || RenderCameraWrite.BitwiseEqual(previous, current)) return true;
        if (alpha <= 0) { result = previous; return true; }
        var first = previous;
        var second = current;
        first.Translation = second.Translation = Vector3.Zero;
        if (RenderCameraWrite.BitwiseEqual(first, second))
        {
            result.Translation = Vector3.Lerp(previous.Translation, current.Translation, alpha);
            return true;
        }
        if (!Matrix4x4.Decompose(previous, out var previousScale, out var previousRotation, out var previousPosition)
            || !Matrix4x4.Decompose(current, out var currentScale, out var currentRotation, out var currentPosition)
            || MathF.Sign(previous.GetDeterminant()) != MathF.Sign(current.GetDeterminant())
            || Vector3.Dot(previousScale, currentScale) <= 0
            || MathF.Abs(Quaternion.Dot(previousRotation, currentRotation)) < .8660254f) return false;
        result = Matrix4x4.CreateScale(Vector3.Lerp(previousScale, currentScale, alpha))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(previousRotation, currentRotation, alpha));
        result.Translation = Vector3.Lerp(previousPosition, currentPosition, alpha);
        return Affine(result);
    }

    private static bool Affine(Matrix4x4 value)
    {
        foreach (float item in MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref value, 1)))
            if (!float.IsFinite(item)) return false;
        return MathF.Abs(value.M14) < .0001f && MathF.Abs(value.M24) < .0001f && MathF.Abs(value.M34) < .0001f
            && MathF.Abs(value.M44 - 1) < .0001f;
    }
}
