global using System.Diagnostics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[CollectionDefinition("Particle presentation", DisableParallelization = true)]
public sealed class ParticlePresentationCollection;

public unsafe sealed partial class VrrModule
{
    private const nint GameOverMagicId = 0x864ca0;
    private readonly PilotConfig _config = new();
    private readonly PilotLogger _logger = new();
    internal bool LiveManagers;
    internal int FieldCount;
    private const int FieldManagerCapacity = 128, FieldManagerStride = 0x80;
    private int field_manager_count() => FieldCount;
    private static int live_objects(byte* manager) => *(uint*)(manager + 0x30) == 0 ? 0 : 1;
    private bool advance_this_frame() => true;
    private bool battle_effect_presentation_is_qualified() => FhUtil.get_at<nint>(EngineAddresses.PlayerChrs) == 0;
    private bool _battleEffectPauseInstalled;
    private void opu_held_walk(d_pilot_worker original, ushort family, byte mode) => original(family, mode);
    private const int VanillaAgeStep = 0x1000;
    private readonly Dictionary<nint, (int Original, int Applied)> _timeline_manager_steps = [];
    private int _timeline_step = 0x800;
    private long _frames = 0;
    internal static float TargetFramerate = 60;
    internal void ConfigureParticles(bool vrr, bool timeline) { _config.VrrEnabled = vrr; _config.ParticleTimeline = timeline; }
    internal void EnableSpriteClock() => _config.ParticleSpriteClock = true;
    internal nint CreateSprite(nint manager, nint entry) => h_particle_pilot_create(manager, entry);
    internal void DeleteSprite(nint obj) => h_particle_pilot_delete(obj);
    internal void LoopSprite(nint obj) => h_particle_pilot_reset(obj);
    internal void FreeSprites(nint manager) => h_particle_pilot_free(manager);
    internal void DrawSprite(nint obj, nint operand, nint program, bool extended)
    {
        if (extended) h_particle_shape_x(obj, operand, program);
        else h_particle_shape(obj, operand, program);
    }
    internal bool ScaleSprite(nint obj, nint program, int offset, short authored, out short applied)
        => _particle_sprite_clock.TryScale(obj, program, offset, authored, (int)TargetFramerate, out applied);
    internal bool InitializeParticleOwners() => init_particle_hooks() && init_particle_presentation();
    internal uint RunSharedManager(nint manager) => h_ppp_run_part(manager, 0x30);
    internal void StartSharedManager(nint manager, int step, nint data, int flags) => h_ppp_start_part(manager, step, data, flags);
    internal int ManagedStepCount => _timeline_manager_steps.Count;
    private int? render_pause_particles()
    {
        if (!RenderPassContext.IsPresentation) return null;
        int previous = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
        return previous;
    }
    private void render_restore_particles(int? previous)
    {
        if (previous.HasValue) FhUtil.set_at(EngineAddresses.PpvUserStopPartF, previous.Value);
    }
    private bool timeline_has_live_objects() => LiveManagers;
    private bool hook_or_log(string name, nint address, Func<bool> install) => install();
    internal bool InitializeSpriteClock() => init_particle_sprite_clock();
    internal bool InitializePilot() => init_particle_presentation();
    internal bool QualifyPilot() => particle_presentation_is_qualified();
    internal void EnterParticleLoop() => particle_presentation_enter_loop();
    internal void ResetPilot() => reset_particle_presentation();
    internal void RunWorker(ushort family, byte mode) => h_particle_pilot_worker(family, mode);
    internal uint RunManager(nint manager) => particle_pilot_run(manager, 0x30, PilotNative.Run);
    internal nint DdForTest(nint memory)
    {
        _particle_pilot_epoch = 1;
        _particle_pilot_dll = 0x10000000;
        _particle_pilot_dd = new(memory, memory + 0x100, memory + 0x400, memory + 0x500, 1);
        try { return h_particle_pilot_dd(memory + 0x6000, memory + 0x7000); }
        finally { _particle_pilot_dd = null; }
    }
    internal uint ReplayForTest(nint memory)
    {
        _particle_pilot_epoch = 1;
        _particle_pilot_dll = 0x10000000;
        var owner = new ParticlePilotOwner(memory, memory + 0x100, memory + 0x400, memory + 0x500, 1);
        owner.Capture.BeginRealRun(owner.Manager, 1, Environment.CurrentManagedThreadId);
        owner.Capture.Record(memory + 0x1000, memory + 0x4000, memory + 0x5000, 0, ParticlePresentationKernel.MatrixXyz);
        owner.Capture.Record(memory + 0x1000, memory + 0x4000, memory + 0x5010, 1, ParticlePresentationKernel.DrawMatrix);
        owner.Capture.CompleteRealRun();
        _particle_pilot_kernels[ParticlePresentationKernel.MatrixXyz] = PilotNative.Matrix;
        _particle_pilot_kernels[ParticlePresentationKernel.DrawMatrix] = PilotNative.Draw;
        return pilot_replay_calls(owner);
    }
}

internal sealed class PilotConfig { internal bool VrrEnabled = true, ParticleTimeline = true, ParticleSpriteClock, FieldParticleRestartHold = true; }
internal sealed class PilotLogger { internal void Error(string message) { } internal void Info(string message) { } }
internal static class NativeBuild
{
    internal static bool TryValidate(out string error) { error = ""; return true; }
}
internal static class EngineAddresses
{
    internal const nint PppRunPart = 0x312250, PppRunPartFp = 0x3122f0, PppStartPart = 0x3123c0,
        PppFpLoop = 0x329ab0, PpvFpManagers = 0x92c260, PpvFpGroupTable = 0x92c258,
        PppPartLoop = 0x362250, PpvPartTimeStep = 0x111100, PpvPartManagerCount = 0x111104, PpvPartManagers = 0x111108;
    internal const nint PlayerChrs = 0xd334d4, GlobalEffectOverlayState = 0xd33364, PpvUserStopPartF = 0x1f0fd34,
        BattleState = 0;
}
internal readonly record struct FhCallLocation(nint Rva, int Unused);
internal readonly struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
{
    public bool hook(object owner, T callback) { PilotNative.Hooks++; PilotNative.HookTargets.Add(location.Rva); return location.Rva != PilotNative.FailHook; }
    public FhCallHandle<T> chain_from(T callback) => this;
    public T fnptr
    {
        get
        {
            if (PilotNative.Initializing) throw new InvalidOperationException("You cannot invoke calls until all module initializers have completed.");
            PilotNative.Resolutions[location.Rva] = PilotNative.Resolutions.GetValueOrDefault(location.Rva) + 1;
            string method = location.Rva switch
            {
                0x40cc00 => nameof(PilotNative.Worker),
                0x316770 => nameof(PilotNative.Create),
                0x3168f0 => nameof(PilotNative.Delete),
                0x312d20 => nameof(PilotNative.Loop),
                0x311f20 => nameof(PilotNative.Free),
                0x340a70 or 0x340f60 => nameof(PilotNative.Sprite),
                0x312250 => nameof(PilotNative.Run),
                0x3123c0 => nameof(PilotNative.Start),
                0x40b800 => nameof(PilotNative.Dd),
                0x32d390 or 0x334170 or 0x334bd0 => nameof(PilotNative.Matrix),
                0x341b90 => nameof(PilotNative.FieldGeometry),
                0x2453d0 => nameof(PilotNative.Backend),
                0x241da0 => nameof(PilotNative.Level),
                0x317010 => nameof(PilotNative.Dispatch),
                0x329ab0 => nameof(PilotNative.FieldLoop),
                0x334b70 => nameof(PilotNative.Draw),
                0x644650 => nameof(PilotNative.Decode),
                _ => throw new InvalidOperationException($"Unexpected native target {location.Rva:X}"),
            };
            return (T)Delegate.CreateDelegate(typeof(T), typeof(PilotNative).GetMethod(method)!);
        }
    }
}
internal static class PilotNative
{
    internal static bool Initializing;
    internal static readonly Dictionary<nint, int> Resolutions = [];
    internal static int Hooks, Workers, Runs, Starts, LastStartStep, LastStartFlags;
    internal static nint LastStartManager, LastStartData;
    internal static readonly List<nint> HookTargets = [];
    internal static readonly List<int> KernelOrder = [];
    internal static Action<nint>? OnDraw;
    internal static Action? OnDd;
    internal static nint CreatedObject, FailHook;
    internal static Action<string, nint>? OnLifecycle;
    internal static Action<nint, nint, nint>? OnSprite;
    public static nint Create(nint manager, nint entry) { OnLifecycle?.Invoke("create", manager); return CreatedObject; }
    public static void Delete(nint obj) => OnLifecycle?.Invoke("delete", obj);
    public static void Loop(nint obj) => OnLifecycle?.Invoke("loop", obj);
    public static void Free(nint manager) => OnLifecycle?.Invoke("free", manager);
    public static void Sprite(nint obj, nint operand, nint program) => OnSprite?.Invoke(obj, operand, program);
    internal static int PacketPublications;
    internal static Action<nint>? OnDispatch;
    internal static Action? OnFieldLoop;
    public static void Dispatch(nint manager) => OnDispatch?.Invoke(manager);
    public static void FieldLoop() => OnFieldLoop?.Invoke();
    public static void FieldGeometry(nint obj, nint data, nint descriptor, nint work) { PacketPublications++; OnDraw?.Invoke(obj); }
    public static nint Backend(int index, nint obj) => 1;
    public static int Level() => 0;
    public static void Worker(ushort family, byte mode) => Workers++;
    public static uint Run(nint manager, byte mode) { Runs++; return 77; }
    public static void Start(nint manager, int step, nint data, int flags) { Starts++; LastStartManager = manager; LastStartStep = step; LastStartData = data; LastStartFlags = flags; }
    public static nint Dd(nint scratch, nint pc) { OnDd?.Invoke(); PacketPublications++; return pc + 4; }
    public static nint Decode(uint pointer) => (nint)(nuint)pointer;
    public static void Matrix(nint obj, nint data, nint descriptor) { KernelOrder.Add(0); OnDraw?.Invoke(obj); }
    public static void Draw(nint obj, nint data, nint descriptor) { KernelOrder.Add(1); OnDraw?.Invoke(obj); }
    internal static void Reset() { Initializing = false; Resolutions.Clear(); Hooks = Workers = Runs = Starts = LastStartStep = LastStartFlags = PacketPublications = 0; LastStartManager = LastStartData = 0; OnDraw = null; OnDd = null; OnDispatch = null; OnFieldLoop = null; CreatedObject = FailHook = 0; OnLifecycle = null; OnSprite = null; VrrModule.TargetFramerate = 60; KernelOrder.Clear(); HookTargets.Clear(); FhUtil.Reset(); RenderPassContext.IsPresentation = false; RenderPassContext.IsActive = false; }
}
internal unsafe static class FhUtil
{
    private static readonly Dictionary<nint, nint> Blocks = [];
    private static readonly Dictionary<nint, object> Values = [];
    internal static nint BadPrefix, BadInstructionTarget;
    internal static int BadInstructionOffset;
    internal static T get_at<T>(nint address) => Values.TryGetValue(address, out var value) ? (T)value : default!;
    internal static void set_at<T>(nint address, T value) => Values[address] = value!;
    internal static T* ptr_at<T>(nint address) where T : unmanaged
    {
        if (!Blocks.TryGetValue(address, out nint memory))
        {
            int size = address == 0x83a508 ? 0x4000 : 0x600;
            memory = Marshal.AllocHGlobal(size);
            new Span<byte>((void*)memory, size).Clear();
            if (address != 0xea40c0 && address != 0xea40c4)
            {
                byte[] prefix = address is 0x340a70 or 0x340f60
                    ? [0x53, 0x8b, 0xdc, 0x83, 0xec, 0x08, 0x83, 0xe4, 0xf0, 0x83, 0xc4, 0x04, 0x55, 0x8b, 0x6b, 0x04, 0x89, 0x6c, 0x24, 0x04, 0x8b, 0xec]
                    : address is 0x3394c0 or 0x339d30 or 0x32e790 or 0x341b90 ? [0x53, 0x8b, 0xdc] : [0x55, 0x8b, 0xec];
                prefix.CopyTo(new Span<byte>((void*)memory, prefix.Length));
                if (address is 0x340a70 or 0x340f60)
                {
                    int pause = address == 0x340a70 ? 0x3b7 : 0x467;
                    int step = address == 0x340a70 ? 0x3c3 : 0x473;
                    byte* bytes = (byte*)memory;
                    bytes[pause] = 0x83; bytes[pause + 1] = 0x3d;
                    *(uint*)(bytes + pause + 2) = (uint)(nuint)ptr_at<byte>(EngineAddresses.PpvUserStopPartF);
                    bytes[pause + 6] = 0;
                    byte[] clock = [0x66, 0x8b, 0x40, 0x08, 0x66, 0x01, 0x06];
                    clock.CopyTo(new Span<byte>(bytes + step, clock.Length));
                }
            }
            Blocks.Add(address, memory);
        }
        if (address == BadPrefix) *(byte*)memory = 0;
        if (address == BadInstructionTarget) *((byte*)memory + BadInstructionOffset) ^= 0xff;
        return (T*)memory;
    }
    internal static void Reset() { Values.Clear(); BadPrefix = BadInstructionTarget = 0; BadInstructionOffset = 0; foreach (var memory in Blocks.Values) Marshal.FreeHGlobal(memory); Blocks.Clear(); }
}
internal unsafe static class MemoryProbe
{
    internal static bool IsReadable(nint address, int length) => address > 0x10000 && length > 0;
    internal static bool TryCopy(nint address, Span<byte> target) { new ReadOnlySpan<byte>((void*)address, target.Length).CopyTo(target); return true; }
}

internal unsafe sealed class PilotMemory : IDisposable
{
    internal readonly nint Address;
    internal PilotMemory()
    {
        for (int i = 0; i < 16 && Address == 0; i++)
            Address = VirtualAlloc((nint)(0x20000000 + i * 0x10000), 0x10000, 0x3000, 4);
        if (Address == 0) throw new InvalidOperationException("Cannot reserve bounded 32-bit test memory.");
        new Span<byte>((void*)Address, 0x10000).Clear();
        *(uint*)(Address + 0x50) = 0x1004a590;
        *(byte*)(Address + 0x100 + 0xbb) = 3;
        *(uint*)(Address + 0x100 + 0xbc) = (uint)(nuint)(Address + 0x400);
        *(uint*)(Address + 0x100 + 0xd0) = (uint)(nuint)(Address + 0x500);
        new Span<byte>((void*)(Address + 0x1000), 0x108).Fill(0xa5);
        *(uint*)(Address + 0x1008) = (uint)(nuint)(Address + 0x2000);
        *(uint*)(Address + 0x2000) = (uint)(nuint)(Address + 0x3000);
        *(int*)(Address + 0x3020) = 0x100;
        *(short*)(Address + 0x3026) = 2;
    }
    internal byte[] ObjectBytes => new ReadOnlySpan<byte>((void*)(Address + 0x1000), 0x108).ToArray();
    public void Dispose() => VirtualFree(Address, 0, 0x8000);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(nint address, nuint size, uint type);
}
