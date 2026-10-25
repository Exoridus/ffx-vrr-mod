using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class RenderActorTests
{
    private static readonly RenderActorIdentity Identity = new(10, 20, 30, 40);
    private static Matrix4x4 Root(float x) => Matrix4x4.CreateTranslation(x, 0, 0);
    private static Matrix4x4[] Bones(float degrees)
        => [Matrix4x4.Identity, Matrix4x4.CreateRotationZ(degrees * MathF.PI / 180)];

    private static void Tick(RenderActorHistory history, Matrix4x4 root, Matrix4x4[] bones, RenderActorIdentity? identity = null)
    {
        history.BeginSimulation();
        history.Capture(1, identity ?? Identity, root, bones);
        history.CompleteSimulation();
    }

    [Fact]
    public void RootAndEveryBoneUseTheSameFractionWithoutChangingCanonicalInputs()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        var bones = Bones(40);
        Tick(history, Root(10), bones);
        var output = new Matrix4x4[2];
        Assert.True(history.TryInterpolate(1, Identity, Root(10), bones, .5f, out var root, output));
        Assert.Equal(5, root.M41, 4);
        Assert.Equal(MathF.Cos(20 * MathF.PI / 180), output[1].M11, 4);
        Assert.Equal(Bones(40), bones);
        Assert.True(history.TryInterpolate(1, Identity, Root(10), bones, .75f, out root, output));
        Assert.Equal(7.5f, root.M41, 4);
        Assert.Equal(MathF.Cos(30 * MathF.PI / 180), output[1].M11, 4);
    }

    [Fact]
    public void IdentityAndBoneCountChangesBreakHistory()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        var replacement = Identity with { Mesh = 21 };
        Tick(history, Root(10), Bones(20), replacement);
        Assert.False(history.TryInterpolate(1, replacement, Root(10), Bones(20), .5f, out _, new Matrix4x4[2]));
        Tick(history, Root(20), [Matrix4x4.Identity], replacement);
        Assert.False(history.TryInterpolate(1, replacement, Root(20), [Matrix4x4.Identity], .5f, out _, new Matrix4x4[1]));
    }

    [Fact]
    public void MissingSimulationAndResetDiscardHistory()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        Tick(history, Root(10), Bones(20));
        history.BeginSimulation();
        history.CompleteSimulation();
        Tick(history, Root(20), Bones(40));
        Assert.False(history.TryInterpolate(1, Identity, Root(20), Bones(40), .5f, out _, new Matrix4x4[2]));
        Tick(history, Root(30), Bones(50));
        history.Reset();
        Assert.False(history.TryInterpolate(1, Identity, Root(30), Bones(50), .5f, out _, new Matrix4x4[2]));
    }

    [Fact]
    public void OneInvalidBoneRejectsTheWholePoseAndRoot()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        Matrix4x4[] invalid = [Matrix4x4.Identity, default];
        Tick(history, Root(10), invalid);
        Assert.False(history.TryInterpolate(1, Identity, Root(10), invalid, .5f, out var root, new Matrix4x4[2]));
        Assert.Equal(Root(10), root);
    }

    [Fact]
    public void TeleportsSnapRootAndWholePaletteTogether()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        Tick(history, Root(600), Bones(20));
        Assert.False(history.TryInterpolate(1, Identity, Root(600), Bones(20), .5f, out var root, new Matrix4x4[2]));
        Assert.Equal(Root(600), root);
    }

    [Fact]
    public void DuplicateDrawsDoNotRotateSimulationHistory()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        history.BeginSimulation();
        history.Capture(1, Identity, Root(10), Bones(20));
        history.Capture(1, Identity, Root(10), Bones(20));
        Assert.True(history.TryInterpolate(1, Identity, Root(10), Bones(20), .5f, out var root, new Matrix4x4[2]));
        Assert.Equal(5, root.M41);
    }

    [Fact]
    public void StationarySmallScaledShearedBasisRemainsBitExact()
    {
        var history = new RenderActorHistory();
        var root = Matrix4x4.CreateScale(.001f);
        root.M12 = .0000002f;
        Tick(history, root, []);
        Tick(history, root, []);
        Assert.True(history.TryInterpolate(1, Identity, root, [], .5f, out var result, []));
        Assert.True(RenderCameraWrite.BitwiseEqual(root, result));
    }

    [Fact]
    public void RestorePreservesAReplacedRootAndReplacedBoneWhileRestoringOwnedBones()
    {
        var write = new RenderActorWrite(2);
        byte[] original = new byte[48], rendered = Enumerable.Repeat((byte)7, 48).ToArray();
        Matrix4x4[] canonicalBones = [Root(10), Root(20)], renderedBones = [Root(5), Root(15)];
        write.Record(original, rendered, canonicalBones, renderedBones);
        var liveWorld = Enumerable.Repeat((byte)9, 48).ToArray();
        Matrix4x4[] liveBones = [renderedBones[0], Root(100)];
        write.Restore(liveWorld, liveBones);
        Assert.Equal(Enumerable.Repeat((byte)9, 48), liveWorld);
        Assert.Equal(canonicalBones[0], liveBones[0]);
        Assert.Equal(Root(100), liveBones[1]);
        liveBones[0] = renderedBones[0];
        write.Restore(liveWorld, liveBones);
        Assert.Equal(renderedBones[0], liveBones[0]);
    }

    [Fact]
    public void RestoreCopiesBackExactOwnedNativeWorldBytes()
    {
        var write = new RenderActorWrite(0);
        byte[] original = Enumerable.Range(0, 48).Select(n => (byte)n).ToArray();
        byte[] rendered = Enumerable.Repeat((byte)7, 48).ToArray();
        write.Record(original, rendered, [], []);
        var world = rendered.ToArray();
        write.Restore(world, []);
        Assert.Equal(original, world);
    }

    [Fact]
    public void CachedPresentationReplaysWholePoseWithoutANewWorldSubmission()
    {
        var history = new RenderActorHistory();
        Tick(history, Root(0), Bones(0));
        Tick(history, Root(10), Bones(40));
        var output = new Matrix4x4[2];
        Assert.True(history.TryInterpolateCurrent(1, Identity, Bones(40), .5f, out var root, output));
        Assert.Equal(5, root.M41, 4);
        Assert.Equal(MathF.Cos(20 * MathF.PI / 180), output[1].M11, 4);
        Assert.False(history.TryInterpolateCurrent(1, Identity, Bones(50), .75f, out _, output));
        Assert.True(history.TryInterpolateCurrent(1, Identity, Bones(40), .75f, out root, output));
        Assert.Equal(7.5f, root.M41, 4);
    }
}
