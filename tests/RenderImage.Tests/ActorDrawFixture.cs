using System.Numerics;
using System.Reflection;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

internal unsafe sealed class ActorDrawFixture : IDisposable
{
    internal readonly BridgeFixture Bridge = new();
    internal VrrModule Module => Bridge.Module;
    internal nint Instance => FhUtil.Base + 0x810000;
    internal nint Mesh => FhUtil.Base + 0x811000;
    internal nint World => FhUtil.Base + 0x812000;
    internal nint Palette => FhUtil.Base + 0x813000;
    internal nint Scratch => FhUtil.Base + 0x816000;
    internal Matrix4x4 Root => *(Matrix4x4*)ActorDrawNative.Output;
    internal Matrix4x4 Bone => *(Matrix4x4*)(ActorDrawNative.Output + 64);
    internal byte[] PersistentWorld => new ReadOnlySpan<byte>((void*)World, 48).ToArray();
    internal byte[] PersistentPalette => new ReadOnlySpan<byte>((void*)Palette, 64).ToArray();

    internal ActorDrawFixture()
    {
        Bridge.Init();
        ActorDrawNative.Reset();
        ActorDrawNative.Module = Module;
        ActorDrawNative.Output = Scratch;
        *(nint*)Instance = Mesh; *(nint*)(Instance + 4) = World; *(nint*)(Instance + 12) = Palette;
        *(int*)(Instance + 0x80) = -1;
        *(uint*)(Mesh + 0x18) = 1;
        *(nint*)(Mesh + 0x2c) = FhUtil.Base + 0x814000;
        *(int*)(FhUtil.Base + 0x814000) = -1;
        new Span<byte>((void*)World, 48).Fill(0x5a);
        *(Matrix4x4*)Palette = Matrix4x4.CreateTranslation(100, 200, 300);
        SeedBindings();
        Assert.True(Module.ActorDrawInitialize());
    }

    private void SeedBindings()
    {
        Bridge.Bytes(0xf6ef0, Convert.FromHexString("558BEC81EC8000000056578BF9F6476C01740433C0EB038B4704"));
        Bridge.Bytes(0xf6f53, Convert.FromHexString("8B7508508D4EC0E8E1C3F5FF568BCFE8590000005F5E8BE55DC20400"));
        Bridge.Bytes(0x1b9a90, Convert.FromHexString("558BEC6AFF682B80AC00"));
        Bridge.Bytes(0x1b9ecc, Convert.FromHexString("8BE55DC21000"));
        Bridge.Bytes(0xf27e0, Convert.FromHexString("558BEC6AFF683777AB00"));
        Bridge.Bytes(0xf28df, Convert.FromHexString("8BC68B4DF464890D00000000595E8BE55DC20400"));
        Bridge.Bytes(0xf2c50, Convert.FromHexString("558BEC6AFF689777AB00"));
        Bridge.Bytes(0xf2dcb, Convert.FromHexString("8B4DF464890D00000000595F5E5B8BE55DC3"));
        Bridge.Bytes(0x1b9b78, Convert.FromHexString("C745FC000000008945E0E869D3F3FF"));
    }

    internal void Begin(bool simulation = true, float alpha = .5f)
    {
        ActorDrawNative.Stamp++;
        *(uint*)(Bridge.Application + 0x260) = ActorDrawNative.Stamp;
        BridgeNative.OnFrame = _ =>
        {
            Module.Animate(Bridge.Application);
            Assert.True(Module.MarkAdmitted());
            Module.ActorDrawBegin(simulation, alpha);
            Module.AnimationEnd(Bridge.Application);
            return 0;
        };
        try { Bridge.Frame(); }
        finally { BridgeNative.OnFrame = null; RenderPassContext.IsActive = RenderPassContext.IsPresentation = false; }
    }
    internal uint QueueImage(float alpha = .5f)
    {
        uint stamp = *(uint*)(Bridge.Application + 0x260);
        var oldFrame = BridgeNative.OnFrame;
        var oldFlip = BridgeNative.OnFlip;
        BridgeNative.OnFlip = renderer =>
        {
            Bridge.Slot();
            *(uint*)(renderer + 0x13c) = stamp + 1;
            return 0;
        };
        BridgeNative.OnFrame = _ =>
        {
            Module.Animate(Bridge.Application);
            Assert.True(Module.MarkAdmitted());
            Module.ActorDrawBegin(true, alpha);
            Module.ActorDrawComplete();
            Module.AnimationEnd(Bridge.Application);
            Bridge.Ordinary();
            Bridge.Flip();
            return 0;
        };
        try { Bridge.Frame(); }
        finally
        {
            BridgeNative.OnFrame = oldFrame; BridgeNative.OnFlip = oldFlip;
            RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
        }
        Assert.Equal(stamp + 1, *(uint*)(Bridge.Application + 0x260));
        return stamp;
    }
    internal uint DrawQueued(uint stamp) => Module.ActorDrawInvoke(Bridge.Renderer, Instance, 222, 333, unchecked((int)stamp));
    internal void Pose(float root, float bone)
    {
        ActorDrawNative.NativeRoot = Matrix4x4.CreateTranslation(root, 0, 0);
        ActorDrawNative.NativeBone = Matrix4x4.CreateTranslation(bone, 0, 0);
    }
    internal void Construct() => Assert.Equal(Instance, Module.ActorDrawConstruct(Instance, Mesh));
    internal uint Draw() => Module.ActorDrawInAction(Bridge.Application,
        () => Module.ActorDrawInvoke(Bridge.Renderer, Instance, 222, 333, unchecked((int)ActorDrawNative.Stamp)));
    public void Dispose() { ActorDrawNative.Reset(); Bridge.Dispose(); }
}

public unsafe sealed partial class VrrModule
{
    internal uint ActorDrawInAction(nint application, Func<uint> invoke)
    {
        var receipt = _renderActorLatestReceipt;
        if (receipt is null) return invoke();
        var previous = _renderImageAction.Value;
        _renderImageAction.Value = new(receipt.Image, application, true, true, receipt.Epoch, true, receipt);
        try { return invoke(); }
        finally { _renderImageAction.Value = previous; }
    }
    internal Action<string, object>? ActorDiagnostic;
    internal bool ActorProbeRequested => ActorObservationRequested;
    internal long ActorDrawBlends => _renderActorBlends;
    internal int ActorDrawTracked => _renderActorStates.Count;
    internal long ActorDrawSkipped => _renderActorSkipped;
    internal bool ActorDrawInitialize() => init_render_actors();
    internal void ActorDrawBegin(bool simulation, float alpha)
    {
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = !simulation;
        render_camera_set_presentation_alpha(alpha);
        if (simulation) { RenderPassContext.BeginSimulation(); render_actors_begin_simulation(); }
        else render_actors_begin_presentation(alpha);
    }
    internal void ActorDrawComplete() => render_actors_complete_simulation();
    internal void ActorDrawRestore() => render_actors_restore();
    internal void ActorDrawReset() => reset_render_actors();
    internal nint ActorDrawConstruct(nint instance, nint mesh)
    {
        var method = GetType().GetMethod("h_render_actor_construct", BindingFlags.NonPublic | BindingFlags.Instance);
        return method is null ? BridgeNative.ActorConstruct(instance, mesh)
            : method.CreateDelegate<Func<nint,nint,nint>>(this)(instance, mesh);
    }
    internal uint ActorDrawDestruct(nint instance)
    {
        var method = GetType().GetMethod("h_render_actor_destruct", BindingFlags.NonPublic | BindingFlags.Instance);
        return method is null ? BridgeNative.ActorDestruct(instance) : method.CreateDelegate<Func<nint,uint>>(this)(instance);
    }
    internal uint ActorDrawInvoke(nint renderer, nint instance, nint materials, int segment, int stamp)
    {
        var method = GetType().GetMethod("h_render_actor_draw", BindingFlags.NonPublic | BindingFlags.Instance);
        return method is null ? BridgeNative.ActorDraw(renderer, instance, materials, segment, stamp)
            : method.CreateDelegate<Func<nint,nint,nint,int,int,uint>>(this)(renderer, instance, materials, segment, stamp);
    }
    internal void ActorDrawHierarchy(nint instance, nint output)
    {
        var method = GetType().GetMethod("h_render_actor_hierarchy", BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null) BridgeNative.ActorHierarchy(instance, output);
        else method.CreateDelegate<Action<nint,nint>>(this)(instance, output);
    }
}

internal static class ActorDrawNative
{
    internal static VrrModule? Module;
    internal static nint Output;
    internal static uint Stamp;
    internal static nint LastRenderer;
    internal static Matrix4x4 NativeRoot, NativeBone;
    internal static Action? BeforeHierarchyReturn, InDraw;
    internal static Exception? ConstructError, DestructError;
    internal static int DrawCalls, HierarchyCalls, Constructs, Destructs, Uploads;
    internal static uint DrawResult = 0xaabbccdd;
    internal static readonly List<string> Order = [];
    internal static void Reset()
    {
        Module = null; Output = 0; Stamp = 0;
        NativeRoot = NativeBone = Matrix4x4.Identity;
        BeforeHierarchyReturn = InDraw = null;
        ConstructError = DestructError = null;
        DrawCalls = HierarchyCalls = Constructs = Destructs = Uploads = 0;
        Order.Clear();
    }
}

internal unsafe static partial class BridgeNative
{
    public static nint ActorConstruct(nint instance, nint mesh) { ActorDrawNative.Constructs++; if (ActorDrawNative.ConstructError is { } error) throw error; *(nint*)instance = mesh; return instance; }
    public static uint ActorDestruct(nint instance) { ActorDrawNative.Destructs++; if (ActorDrawNative.DestructError is { } error) throw error; return 0xdeadbeef; }
    public static void ActorHierarchy(nint instance, nint output)
    {
        ActorDrawNative.HierarchyCalls++;
        ActorDrawNative.Order.Add("hierarchy");
        *(Matrix4x4*)(output - 64) = ActorDrawNative.NativeRoot;
        *(Matrix4x4*)output = ActorDrawNative.NativeBone;
        ActorDrawNative.BeforeHierarchyReturn?.Invoke();
    }
    public static uint ActorDraw(nint renderer, nint instance, nint materials, int segment, int stamp)
    {
        ActorDrawNative.DrawCalls++;
        ActorDrawNative.LastRenderer = renderer;
        Assert.Equal((nint)222, materials); Assert.Equal(333, segment);
        ActorDrawNative.InDraw?.Invoke();
        ActorDrawNative.Module!.ActorDrawHierarchy(instance, ActorDrawNative.Output + 64);
        if (*(int*)(instance + 0x80) != stamp)
        {
            *(int*)(instance + 0x80) = stamp;
            ActorDrawNative.Order.Add("stamp");
            ActorDrawNative.Uploads++;
            ActorDrawNative.Order.Add("upload");
        }
        ActorDrawNative.Order.Add("draw");
        return ActorDrawNative.DrawResult;
    }
}
