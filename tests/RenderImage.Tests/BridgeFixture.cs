global using Debug = System.Diagnostics.Debug;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
using System.Numerics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private readonly PresentationAdmission _presentationAdmission = new();
    private readonly TestConfig _config = new();
    private readonly TestLogger _logger = new();
    private bool _presenter_active = true;
    internal bool ModeReady = true;
    internal long? CurrentImage => _renderImageAction.Value?.Image?.Serial;
    private bool RenderSchedulerReady => ModeReady;
    internal string? Failure => _presentationAdmission.Failure;
    internal bool InitImages() => init_render_images();
    internal bool AdmitReady() => render_image_admission_ready();
    internal bool MarkAdmitted() => render_image_admission_begin();
    internal void Animate(nint app) => render_image_animation_begin(app);
    internal void AnimationEnd(nint app, bool normal = true) => render_image_animation_end(app, normal);
    internal bool Reject(uint flags = 0) => render_image_rejected_for_present(flags);
    internal void Refuse(string reason) => refuse_independent_presentation(reason);
    internal void Disable() => _config.VrrEnabled = false;
    internal void Presenter(bool active) => _presenter_active = active;
    private bool hook_or_log(string name, nint rva, Func<bool> install) => install();
    private void refuse_independent_presentation(string reason, Exception? error = null)
        => _presentationAdmission.Refuse(reason);
    internal object ActorGate => _renderActorLock;
    internal int ActorCallbacks => Volatile.Read(ref _renderActorCallbacks);
    internal bool ActorSampling => _renderActorsSampling;
    internal bool ActorActive => _renderActorsActive;
    internal void ActorBegin() => render_actors_begin_simulation();
    internal void ActorComplete() => render_actors_complete_simulation();
    internal void ActorCallback() => h_render_actor_instance(0, null);
    internal void CaptureActor(float x) => _renderActorHistory.Capture(1, new(1, 2, 3, 4),
        Matrix4x4.CreateTranslation(x, 0, 0), []);
    internal bool HasActorPair => _renderActorHistory.TryInterpolateCurrent(1, new(1, 2, 3, 4), [],
        .5f, out _, []);
    private float _renderCameraAlpha;
    private void render_camera_set_presentation_alpha(float alpha) => _renderCameraAlpha = alpha;
    private void diagnostic_actor_pose(nint actor, Matrix4x4 root, ReadOnlySpan<Matrix4x4> bones, bool rendered) { }
    internal sealed class TestConfig { internal bool VrrEnabled = true; }
    internal sealed class TestLogger { internal void Error(string message) { } }
}

internal readonly record struct FhCallLocation(nint Address, int Offset = 0);
internal unsafe ref struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
{
    private T? _original;
    internal T? fnptr => BridgeNative.Initializing ? throw new InvalidOperationException("early fnptr") : _original;
    internal bool hook(VrrModule owner, T callback)
    {
        BridgeNative.Callbacks.Add(location.Address, callback);
        return location.Address != BridgeNative.FailHook;
    }
    internal FhCallHandle<T> chain_from(T callback)
    {
        if (BridgeNative.Initializing) throw new InvalidOperationException("early resolution");
        BridgeNative.Resolutions++;
        string name = location.Address switch
        {
            0x227940 => nameof(BridgeNative.Frame), 0x1b6af0 => nameof(BridgeNative.Flip),
            0x214c00 => nameof(BridgeNative.Slot), 0x193760 => nameof(BridgeNative.Action),
            0x26d810 => nameof(BridgeNative.ActorInstance),
            0x242bb0 => nameof(BridgeNative.Ordinary), 0x2419d0 => nameof(BridgeNative.Initialize),
            0x2403e0 => nameof(BridgeNative.FinalizeGraphics), 0x2fa70 => nameof(BridgeNative.ResetGraphics),
            _ => throw new InvalidOperationException("unknown target")
        };
        if (BridgeNative.NativeGraphicsOriginal && location.Address is 0x242bb0 or 0x2419d0 or 0x2403e0 or 0x2fa70)
        {
            nint pointer = location.Address == 0x2fa70
                ? (nint)(delegate* unmanaged[Cdecl]<void>)&BridgeNative.NativeReset
                : (nint)(delegate* unmanaged[Cdecl]<uint>)&BridgeNative.NativeResult;
            _original = Marshal.GetDelegateForFunctionPointer<T>(pointer);
            return this;
        }
        _original = BridgeNative.NativeActionOriginal && location.Address == 0x193760
            ? Marshal.GetDelegateForFunctionPointer<T>((nint)(delegate* unmanaged[Thiscall]<nint,uint>)&BridgeNative.NativeAction)
            : (T)Delegate.CreateDelegate(typeof(T), typeof(BridgeNative).GetMethod(name)!);
        return this;
    }
}

internal static class NativeBuild
{
    internal static bool Supported = true;
    internal static bool TryValidate(out string error) { error = "unsupported test image"; return Supported; }
}

internal unsafe static class FhUtil
{
    internal static nint Base;
    internal static T* ptr_at<T>(nint rva) where T : unmanaged => (T*)(Base + rva);
}

internal unsafe static class MemoryProbe
{
    internal static Func<nint, bool>? CopyAllowed;
    internal static bool IsReadable(nint address, int count)
        => count > 0 && address >= FhUtil.Base && (nuint)(address-FhUtil.Base)+(uint)count <= 0x900000;
    internal static bool TryCopy(nint address, Span<byte> target)
    {
        if (CopyAllowed?.Invoke(address) == false || !IsReadable(address, target.Length)) return false;
        new ReadOnlySpan<byte>((void*)address, target.Length).CopyTo(target);
        return true;
    }
}

internal unsafe static class BridgeNative
{
    internal static readonly Dictionary<nint, Delegate> Callbacks = [];
    internal static bool Initializing, NativeActionOriginal, NativeGraphicsOriginal;
    internal static nint FailHook;
    internal static int Resolutions, Frames, Flips, Slots, Actions, ActorInstances;
    internal static Func<nint,uint>? OnFrame, OnFlip, OnSlot, OnAction;
    internal static Action<nint, nint>? OnActorInstance;
    internal static Func<uint>? OnOrdinary, OnInitialize, OnFinalize;
    internal static Action? OnReset;
    internal static int OrdinaryCalls, InitializeCalls, FinalizeCalls, ResetCalls;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static uint NativeResult() { OrdinaryCalls++; return 0xdeadbeef; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void NativeReset() { ResetCalls++; }
    public static uint Ordinary() { OrdinaryCalls++; return OnOrdinary?.Invoke() ?? 0; }
    public static uint Initialize()
    {
        InitializeCalls++;
        if (OnInitialize is not null) return OnInitialize();
        *(uint*)(FhUtil.Base + 0x8cb45c) = 1;
        return 0;
    }
    public static uint FinalizeGraphics()
    {
        FinalizeCalls++;
        if (OnFinalize is not null) return OnFinalize();
        *(uint*)(FhUtil.Base + 0x8cb45c) = 0;
        return 0;
    }
    public static void ResetGraphics()
    {
        ResetCalls++;
        if (OnReset is not null) OnReset();
        else *(uint*)(FhUtil.Base + 0x8cb45c) = 0;
    }
    public static void ActorInstance(nint instance, Matrix4x4* matrix)
    {
        Interlocked.Increment(ref ActorInstances);
        OnActorInstance?.Invoke(instance, (nint)matrix);
    }
    public static uint Frame(nint self) { Frames++; return OnFrame?.Invoke(self) ?? 0; }
    public static uint Flip(nint self) { Flips++; return OnFlip?.Invoke(self) ?? 0; }
    public static uint Slot(nint self) { Slots++; return OnSlot?.Invoke(self) ?? 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    public static uint NativeAction(nint self) { Interlocked.Increment(ref Actions); return unchecked((uint)(nuint)self) ^ 0xaabbcc00; }
    public static uint Action(nint self) { Actions++; return OnAction?.Invoke(self) ?? 0xaabbcc01; }
    internal static uint Invoke(nint rva, params object[] arguments)
    {
        try { return (uint)(Callbacks[rva].DynamicInvoke(arguments) ?? 0u); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    internal static void Reset()
    {
        Callbacks.Clear(); Initializing = NativeActionOriginal = NativeGraphicsOriginal = false; FailHook = 0;
        Resolutions = Frames = Flips = Slots = Actions = ActorInstances = 0;
        OnFrame = OnFlip = OnSlot = OnAction = null;
        OnActorInstance = null;
        OnOrdinary = OnInitialize = OnFinalize = null; OnReset = null;
        OrdinaryCalls = InitializeCalls = FinalizeCalls = ResetCalls = 0;
        NativeBuild.Supported = true;
        MemoryProbe.CopyAllowed = null;
    }
}

internal unsafe sealed class BridgeFixture : IDisposable
{
    internal readonly VrrModule Module = new();
    internal nint Application => FhUtil.Base + 0x780000;
    internal nint Renderer => Application + 0x124;
    internal nint RenderThread => FhUtil.Base + 0x8c96f8;
    internal const nint Context = 0x7777;
    internal BridgeFixture()
    {
        Assert.Equal(4, IntPtr.Size);
        BridgeNative.Reset();
        FhUtil.Base = Marshal.AllocHGlobal(0x900000);
        new Span<byte>((void*)FhUtil.Base, 0x900000).Clear();
        Bytes(0x8cb45c, [1,0,0,0]);
        Bytes(0x227940, [0x55,0x8b,0xec,0x6a,0xff]);
        Bytes(0x1b6af0, [0x55,0x8b,0xec,0x83,0xec,8,0x56,0x8b,0xf1,0xf6,0x46,4,1]);
        Bytes(0x214c00, [0x81,0xc1,0x64,1,0,0,0xe8,0xa5,0xb6,0xe1,0xff,0x33,0xc0,0xc3]);
        Bytes(0x193760, [0x56,0x8b,0xf1,0x8b,0x56,0x74,0x85,0xd2,0x75,4,0x32,0xc0,0x5e,0xc3,
            0x8b,0x0a,0x33,0xc0,0x38,0x86,0x1c,0x13,0,0,0x6a,0,0x0f,0x95,0xc0,0x50,0x52,0xff,0x51,0x20,0xb0,1,0x5e,0xc3]);
        Bytes(0x21516e,[0x6a,1,0x6a,1,0x8d,0x8b,0x64,1,0,0,0xe8,3,0xaf,0xe1,0xff]);
        Bytes(0x214d86,[0x8b,0x4d,8,0xe8,0xd2,0xe9,0xf7,0xff,0x6a,0,0x6a,1,0x8d,0x8e,0x64,1,0,0,
            0xe8,0x73,0xb4,0xe1,0xff,0x5e,0x5d,0xc2,0x0c,0]);
        Relocated(0x242bb0, [0x8b,0x0d,0,0,0,0,0x56,0xe8,0x74,0x5d,2,0,0x8b,0xf0,0xe8,0x4d,0xbc,0x0d,0,
            0x8b,0x0d,0,0,0,0,0xe8,0x52,0x7a,1,0,0x8b,0xc6,0x5e,0xc3], 2, 0x8cb9d8);
        *(nint*)(FhUtil.Base + 0x242bc5) = FhUtil.Base + 0x8cb9d8;
        Bytes(0x2f963, [0xe8,0x48,0x32,0x21,0]);
        Relocated(0x2419d0,[0x68,0,0,0,0,0x6a,0x0a,0xe8,0x74,0x6e,0,0,0x83,0xc4,8],1,0x7415c4);
        Relocated(0x2403e0,[0x68,0,0,0,0,0x6a,0x0a,0xe8,0x64,0x84,0,0,0x83,0xc4,8],1,0x741604);
        Relocated(0x2fa70,[0x6a,0,0x6a,0,0x6a,0x10,0xff,0x35,0,0,0,0],8,0x8c9ce8);
        Relocated(0x2419e4,[0xa1,0,0,0,0],1,0x8dede4);
        Relocated(0x241a09,[0xa3,0,0,0,0],1,0x8dede4);
        Relocated(0x241b79,[0xc7,5,0,0,0,0,1,0,0,0],2,0x8cb45c);
        Bytes(0x241b85,[0xc3]);
        Relocated(0x2403ef,[0x83,0x3d,0,0,0,0,0],2,0x8cb45c);
        Relocated(0x2403fc,[0x8b,0x0d,0,0,0,0],2,0x8dede4);
        Relocated(0x240403,[0xc7,5,0,0,0,0,0,0,0,0],2,0x8cb45c);
        Relocated(0x240412,[0x8b,0x35,0,0,0,0],2,0x8dede4);
        Relocated(0x24042c,[0xc7,5,0,0,0,0,0,0,0,0],2,0x8dede4);
        Bytes(0x240634,[0xc3]); Bytes(0x240646,[0xc3]);
        Relocated(0x2fa7c,[0xc7,5,0,0,0,0,0,0,0,0],2,0x8cb45c);
        Bytes(0x2fa8c,[0xc3]);
        *(nint*)Application = FhUtil.Base+0x70d998;
        *(nint*)Renderer = FhUtil.Base+0x7363f8;
        *(nint*)(FhUtil.Base+0x70d998+0x10) = FhUtil.Base+0x2f520;
        *(nint*)(FhUtil.Base+0x70d998+0x1c) = FhUtil.Base+0x2f770;
        *(nint*)(FhUtil.Base+0x70d998+0x28) = FhUtil.Base+0x2f4f0;
        *(nint*)(FhUtil.Base+0x7363f8+8) = FhUtil.Base+0x1b6c50;
        BridgeNative.OnFlip = _ => { Slot(); return 0; };
    }
    internal void Bytes(nint rva, ReadOnlySpan<byte> bytes) => bytes.CopyTo(new Span<byte>((void*)(FhUtil.Base+rva),bytes.Length));
    private void Relocated(nint rva, byte[] bytes, int operandOffset, nint target)
    {
        Bytes(rva, bytes);
        *(nint*)(FhUtil.Base + rva + operandOffset) = FhUtil.Base + target;
    }
    internal void Init(bool observeInitialization = true)
    {
        BridgeNative.Initializing = true;
        try { Assert.True(Module.InitImages()); }
        finally { BridgeNative.Initializing = false; }
        if (observeInitialization) Assert.Equal(0u, Initialize());
    }
    internal uint Ordinary() => BridgeNative.Invoke(0x242bb0);
    internal uint Initialize() => BridgeNative.Invoke(0x2419d0);
    internal uint FinalizeGraphics() => BridgeNative.Invoke(0x2403e0);
    internal void ResetGraphics() => BridgeNative.Invoke(0x2fa70);
    internal uint Frame() => BridgeNative.Invoke(0x227940,Application);
    internal uint Flip() => BridgeNative.Invoke(0x1b6af0,Renderer);
    internal uint Slot() => BridgeNative.Invoke(0x214c00,RenderThread);
    internal uint Action() => BridgeNative.Invoke(0x193760,Context);
    internal void Admit()
    {
        Module.Animate(Application);
        Assert.True(Module.AdmitReady());
        Assert.True(Module.MarkAdmitted());
    }
    public void Dispose() { Marshal.FreeHGlobal(FhUtil.Base); FhUtil.Base=0; BridgeNative.Reset(); }
}
