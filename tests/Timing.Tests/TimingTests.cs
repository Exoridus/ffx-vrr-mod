global using System.Text.Json;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
using System.Reflection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class TimingTests : IDisposable
{
    private readonly VrrModule _module = new();

    public TimingTests()
    {
        NativeMemory.Clear(FhUtil.Memory, 0x800000);
        VrrModule.TestRate = 60;
        foreach (var site in typeof(VrrModule).GetField("AgeStepSites", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null) as (string, nint)[] ?? [])
        {
            byte* ptr = FhUtil.ptr_at<byte>(site.Item2);
            ptr[0] = 0x81; ptr[1] = 0x40; ptr[2] = 0x0c;
            *(int*)(ptr + 3) = 0x1000;
        }
        byte* blink = FhUtil.ptr_at<byte>(EngineAddresses.BattleCursorBlinkGate);
        blink[0] = 0xf6; blink[1] = 5;
        *(uint*)(blink + 2) = (uint)(nint)FhUtil.ptr_at<byte>(EngineAddresses.SgCount);
        blink[6] = 1;
        byte* dream = FhUtil.ptr_at<byte>(EngineAddresses.DreamStarCounterCompare);
        dream[0] = 0x66; dream[1] = 0x83; dream[2] = 0xf8; dream[3] = 3;
        byte* lead = FhUtil.ptr_at<byte>(EngineAddresses.AtelMoveLeadInLoad);
        lead[0] = 0xb8; lead[1] = 10;
        new byte[] { 0x66, 0x89, 0x86, 0x16, 0x0b, 0, 0 }.CopyTo(new Span<byte>(lead + 0x25, 7));
    }

    [Theory]
    [InlineData("patch_particle_timeline", "_timeline_step", 0x1000)]
    [InlineData("patch_battle_cursor_blink", "_blink_mask", 1)]
    [InlineData("patch_dream_overlay_stars", "_dream_star_threshold", 3)]
    [InlineData("patch_move_lead_in", "_move_lead_in", 10)]
    public void ReturningToAuthoredRateRestoresImage(string method, string field, int expected)
    {
        Call(method);
        VrrModule.TestRate = 30;
        Call(method);
        Assert.Equal(expected, typeof(VrrModule).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_module));
        Assert.Equal(expected, _module._patches.LastValue);
    }

    [Theory]
    [InlineData(50)] [InlineData(75)] [InlineData(90)] [InlineData(144)]
    public void UnsupportedParticleRateDoesNotInstallWrongClock(float rate)
    {
        VrrModule.TestRate = rate;
        Call("patch_particle_timeline");
        Assert.Empty(_module._patches.Values);
    }

    [Fact]
    public void ManagerPreviouslyCorrectedFollowsFinerClockButCustomStepRemains()
    {
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 2);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, 0x1000);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x90, 123);
        Call("patch_particle_timeline");
        VrrModule.TestRate = 120;
        Call("patch_particle_timeline");
        Assert.Equal(0x400, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x10));
        Assert.Equal(123, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x90));
    }

    [Fact]
    public void LimitTimerAccumulatesEachRateSegmentWithoutReinterpretingHistory()
    {
        FhUtil.set_at(EngineAddresses.LimitTimerMode, 2u);
        FhUtil.set_at(EngineAddresses.LimitTimerBase, 10f);
        for (int i = 0; i < 120; i++) Call("h_limit_timer");
        Assert.Equal(8f, FhUtil.get_at<float>(EngineAddresses.LimitTimerRaw), 4);
        VrrModule.TestRate = 120;
        for (int i = 0; i < 120; i++) Call("h_limit_timer");
        Assert.Equal(7f, FhUtil.get_at<float>(EngineAddresses.LimitTimerRaw), 4);
        FhUtil.set_at(EngineAddresses.LimitTimerFrames, 0f);
        Call("h_limit_timer");
        Assert.Equal(10f - 1f / 120f, FhUtil.get_at<float>(EngineAddresses.LimitTimerRaw), 4);
    }

    [Fact]
    public void CoarserParticleStepIsRefusedWhileAnOffPhaseObjectCanSurvive()
    {
        VrrModule.TestRate = 120;
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 1);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, 0x1000);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x18, 1);
        Call("patch_particle_timeline");
        VrrModule.TestRate = 60;
        Call("patch_particle_timeline");
        Assert.Equal(0x400, _module._patches.LastValue);
        Assert.Equal(0x400, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x10));
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x18, 0);
        Call("patch_particle_timeline");
        Assert.Equal(0x800, _module._patches.LastValue);
    }

    [Fact]
    public void DirectlyInitializedAtelDefaultsUseAuthoredWallDuration()
    {
        byte* move = FhUtil.ptr_at<byte>(0x50000);
        byte* rotation = move + 0x80;
        *(short*)(move + 4) = 90;
        *(short*)(rotation + 4) = 90;
        for (int i = 0; i < 4; i++) *(float*)(rotation + 0x10 + i * 4) = MathF.PI / 64;
        var method = typeof(VrrModule).GetMethod("retime_atel_defaults", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [(nint)move, (nint)rotation, 2f]);
        Assert.Equal(180, *(short*)(move + 4));
        Assert.Equal(180, *(short*)(rotation + 4));
        for (int i = 0; i < 4; i++) Assert.Equal(MathF.PI / 128, *(float*)(rotation + 0x10 + i * 4));
    }

    [Theory]
    [InlineData(1, null, 60)]
    [InlineData(2, null, 30)]
    [InlineData(0, 120d, 120)]
    [InlineData(0, null, 0)]
    public void StartupHasOneCorrectionClockBeforeAnyWorkerStarts(uint interval, double? chosen, float expected)
    {
        var method = typeof(VrrModule).GetMethod("initial_correction_rate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, method.Invoke(null, [interval, chosen]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementMetadataRequiresExactOutputHash(bool tamper)
    {
        string video = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
        try
        {
            File.WriteAllBytes(video, [0x1a, 0x45, 0xdf, 0xa3, 0x86, 0x85, 0x56, 0x5f, 0x56, 0x50, 0x38]);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(video)));
            File.WriteAllText(video + ".manifest.json", System.Text.Json.JsonSerializer.Serialize(new
            {
                schema_version = 1, output_sha256 = hash, width = 1920, height = 1080,
                frame_rate_numerator = 60000, frame_rate_denominator = 1001, frame_count = 20,
                validation_status = "automated_passed", visual_status = "unreviewed", runtime_status = "untested"
            }));
            if (tamper) File.AppendAllText(video, "changed");
            Type? type = typeof(VrrModule).Assembly.GetType("Fahrenheit.Mods.Vrr.FmvReplacementMetadata");
            Assert.NotNull(type);
            var method = type.GetMethod("LoadValidated", BindingFlags.Public | BindingFlags.Static)!;
            if (tamper)
                Assert.IsType<InvalidDataException>(Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [video])).InnerException);
            else
            {
                object metadata = method.Invoke(null, [video])!;
                Assert.Equal(1920, type.GetProperty("Width")!.GetValue(metadata));
                Assert.Equal(60000d / 1001, type.GetProperty("FrameRate")!.GetValue(metadata));
            }
        }
        finally
        {
            File.Delete(video);
            File.Delete(video + ".manifest.json");
        }
    }

    [Fact]
    public void FmvSurfaceSelectionRestoresOriginalOperandsBetweenReplacements()
    {
        var sites = new (nint Operand, nint Original)[]
        {
            (0x2D9E74, 0x74A1AC), (0x2D9E80, 0x74A1A0),
            (0x2D9E88, 0x74A1A8), (0x2D9E94, 0x74A198)
        };
        foreach (var site in sites)
        {
            byte* operand = FhUtil.ptr_at<byte>(site.Operand);
            operand[-2] = 0xd9; operand[-1] = 0x05;
            *(uint*)operand = (uint)(nint)FhUtil.ptr_at<float>(site.Original);
        }
        var method = typeof(VrrModule).GetMethod("select_fmv_surface", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Type type = typeof(VrrModule).Assembly.GetType("Fahrenheit.Mods.Vrr.FmvReplacementMetadata")!;
        object metadata = Activator.CreateInstance(type, 1920, 1080, 60000d / 1001, "unreviewed", "untested")!;
        method.Invoke(_module, [metadata]);
        Assert.Equal(0, _module.TestVideoCarry);
        foreach (var site in sites) Assert.NotEqual((uint)(nint)FhUtil.ptr_at<float>(site.Original), FhUtil.get_at<uint>(site.Operand));
        method.Invoke(_module, [null]);
        foreach (var site in sites) Assert.Equal((uint)(nint)FhUtil.ptr_at<float>(site.Original), FhUtil.get_at<uint>(site.Operand));
        Call("release_fmv_surfaces");
    }

    [Theory]
    [InlineData(30000d / 1001, 60, 500)]
    [InlineData(60000d / 1001, 60, 1000)]
    [InlineData(60000d / 1001, 60000d / 1001, 1000)]
    [InlineData(60000d / 1001, 120, 1000)]
    public void MovieUploadsFollowAssetCadence(double source, double present, int expected)
    {
        var method = typeof(VrrModule).GetMethod("advance_source_frame", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        object[] args = [0d, source, present];
        int uploads = 0;
        for (int i = 0; i < 1000; i++) if ((bool)method.Invoke(null, args)!) uploads++;
        Assert.Equal(expected, uploads);
    }

    [Fact]
    public void TimingOwnershipRefusesSecondModuleOnSameThreadAndReleasesAfterDispose()
    {
        Type? type = typeof(VrrModule).Assembly.GetType("Fahrenheit.Mods.Vrr.TimingOwnership");
        Assert.NotNull(type);
        var method = type.GetMethod("TryAcquire", BindingFlags.Public | BindingFlags.Static)!;
        object?[] first = [null];
        object?[] second = [null];
        Assert.True((bool)method.Invoke(null, first)!);
        try { Assert.False((bool)method.Invoke(null, second)!); }
        finally { ((IDisposable)first[0]!).Dispose(); }
        Assert.True((bool)method.Invoke(null, second)!);
        ((IDisposable)second[0]!).Dispose();
    }

    [Fact]
    public void UnownedCustomManagerStepIsNotMistakenForPreviousAgeStep()
    {
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 2);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, 0x1000);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x90, 0x800);
        Call("patch_particle_timeline");
        VrrModule.TestRate = 120;
        Call("patch_particle_timeline");
        Assert.Equal(0x400, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x10));
        Assert.Equal(0x800, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x90));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(2048, 1024, 512)]
    public void TrackedManagerRetainsAuthoredStepAndPause(int authored, int applied, int expected)
    {
        VrrModule.TestRate = 120;
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount, 1);
        FhUtil.set_at(EngineAddresses.PpvPartManagers + 0x10, applied);
        var tracked = (Dictionary<nint, (int, int)>)typeof(VrrModule).GetField("_timeline_manager_steps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_module)!;
        tracked[(nint)FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers)] = (authored, applied);
        Call("patch_particle_timeline");
        Assert.Equal(expected, FhUtil.get_at<int>(EngineAddresses.PpvPartManagers + 0x10));
    }

    [Fact]
    public void LowerDreamThresholdKeepsLiveCounterWithinItsCycle()
    {
        Call("patch_dream_overlay_stars");
        FhUtil.set_at(EngineAddresses.DreamStarCounter, (ushort)5);
        VrrModule.TestRate = 30;
        Call("patch_dream_overlay_stars");
        Assert.Equal(2, FhUtil.get_at<ushort>(EngineAddresses.DreamStarCounter));
    }

    [Theory]
    [InlineData("V_VP8", true)]
    [InlineData("V_VP9", false)]
    [InlineData("V_AV1", false)]
    public void VideoCodecGateRefusesEveryUnsupportedTrack(string codec, bool expected)
    {
        string video = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
        try
        {
            File.WriteAllBytes(video, new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x86, 0x85 }.Concat(System.Text.Encoding.ASCII.GetBytes(codec)).ToArray());
            Type type = typeof(VrrModule).Assembly.GetType("Fahrenheit.Mods.Vrr.FmvReplacementMetadata")!;
            var method = type.GetMethod("IsVp8", BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);
            Assert.Equal(expected, method.Invoke(null, [video]));
        }
        finally { File.Delete(video); }
    }

    private void Call(string name) => typeof(VrrModule).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(_module, null);
    public void Dispose() { }
}

public unsafe sealed partial class VrrModule
{
    public VrrModule() : this(true) { }

    public VrrModule(bool armSyntheticRuntime)
    {
        if (armSyntheticRuntime) ArmSyntheticRuntimeForTest();
    }

    internal void ArmSyntheticRuntimeForTest()
    {
        // Synthetic fixture activation does not qualify native startup.
        _startupActivation.Prepare(1);
        if (!_startupActivation.Invoke(1, true, () => { }, () => true, () => { }))
            throw new InvalidOperationException("Synthetic fixture activation failed.");
    }

    internal void StopSyntheticRuntimeForTest() => _startupActivation.Stop();


    internal readonly PatchJournal _patches = new();
    private readonly VrrConfig _config = new();
    private readonly Logger _logger = new();
    internal static float TestRate = 60;
    private static float Scale => TestRate / 30;
    private static float TargetFramerate => TestRate;
    private double _video_carry;
    internal double TestVideoCarry => _video_carry;
    private const int ParticleManagerStride = 0x80, ParticleStepOffset = 0x10, FieldManagerCapacity = 128;
    private const nint TexAnimWorkBase = 0x60000;
    private const int TexAnimSlotCount = 64, TexAnimSlotStride = 0x30, TexAnimFormatOffset = 0x0d, TexAnimTimerOffset = 0x0e;
    private bool advance_this_frame() => false;
    private bool ensure_test_hook_registration()
    {
        if (_hookRegistration is not null) return true;
        VrrConfig config = _config;
        var selections = Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared)
            .Select(site => HookRegistrationPlan.ResolveSelection(site, config, false, true, false, null));
        return HookRegistrationPlan.TryCreate(selections, HookRegistrationPlan.AvailableSourceContracts,
            out _hookRegistration, out _);
    }

    private bool hook_or_log(HookSiteId site, string name, nint address, Func<bool> hook)
        => ensure_test_hook_registration() && register_hook(site, name, address, hook);
    private static int scale_deadline(int frames, float scale) => Math.Clamp((int)MathF.Round(frames * scale), short.MinValue, short.MaxValue);
    private static int live_objects(byte* manager) => *(int*)(manager + 0x18);
}

internal sealed class Logger
{
    public void Info(string value) { }
    public List<string> Errors { get; } = [];
    public void Error(string value) => Errors.Add(value);
    public void Warning(string value) { }
}

internal unsafe static class FhUtil
{
    internal static readonly void* Memory = NativeMemory.AllocZeroed(0x800000);
    public static T* ptr_at<T>(nint offset) where T : unmanaged => (T*)((byte*)Memory + offset);
    public static T get_at<T>(nint offset) where T : unmanaged => *ptr_at<T>(offset);
    public static void set_at<T>(nint offset, T value) where T : unmanaged => *ptr_at<T>(offset) = value;
}

internal sealed class PatchJournal
{
    private readonly List<(nint Offset, byte[] Original, byte[] Replacement)> _prepared = [];
    internal bool IsPreparing { get; private set; }
    public List<int> Values { get; } = [];
    public int LastValue => Values[^1];

    internal void BeginPreparation() => IsPreparing = true;

    internal unsafe void CommitPrepared()
    {
        foreach (var patch in _prepared)
            if (!new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(patch.Offset), patch.Original.Length).SequenceEqual(patch.Original))
                throw new InvalidOperationException("Prepared fixture baseline changed.");
        IsPreparing = false;
        foreach (var patch in _prepared) Write(patch.Offset, patch.Replacement);
        _prepared.Clear();
    }

    internal void RestoreAll()
    {
        _prepared.Clear();
        IsPreparing = false;
    }

    public unsafe void Write(nint offset, byte[] value)
    {
        if (IsPreparing)
        {
            _prepared.Add((offset, new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(offset), value.Length).ToArray(), value.ToArray()));
            return;
        }
        value.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(offset), value.Length));
        Values.Add(value.Length switch { 1 => value[0], 2 => BitConverter.ToUInt16(value), _ => BitConverter.ToInt32(value) });
    }
}

internal readonly record struct FhCallLocation(nint Rva, int Unused);
internal readonly struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
{
    public T fnptr { get { _ = location; return (T)Delegate.CreateDelegate(typeof(T), typeof(NativeCalls).GetMethod(typeof(T).Name switch { "d_limit_timer" => nameof(NativeCalls.NativeLimitTimer), "d_lens_shape_update" => nameof(NativeCalls.LensClock), "d_buoyancy_barrier" => nameof(NativeCalls.BuoyancyBarrier), "d_atel_wait_init" => nameof(NativeCalls.WaitInit), "d_atel_pop_int" => nameof(NativeCalls.PopInt), "d_atel_wait_exec" => nameof(NativeCalls.WaitExec), "d_fmv_initialize" => nameof(NativeCalls.InitializeMovie), "d_fmv_get_name" => nameof(NativeCalls.GetMovieName), "d_fmv_play_start" => nameof(NativeCalls.PlayMovie), "d_farplane_scene" => nameof(NativeCalls.FarplaneScene), "d_farplane_fmv_draw" => nameof(NativeCalls.FarplaneDraw), "d_farplane_clear_color" => nameof(NativeCalls.FarplaneColor), _ => nameof(NativeCalls.Rng) })!); } }
    public bool hook(object owner, T callback) => true;
    public FhCallHandle<T> chain_from(T callback) => this;
}
internal unsafe static partial class NativeCalls
{
    public static uint Rng() => 5;
    internal static int InitializedFrames, InitCalls;
    internal static bool ThrowInExec;
    internal static nint LastWork, LastStack;
    public static int PopInt(nint work, nint stack) => 10;
    public static void WaitInit(nint work, int* storage, nint stack) { InitCalls++; LastWork = work; LastStack = stack; *storage = InitializedFrames; }
    public static int WaitExec(nint work, int* storage) => ThrowInExec ? throw new InvalidOperationException("native exec probe") : 7;
}
internal static partial class EngineAddresses
{
    public const nint AtelWaitInit = 0x30500;
    public const nint AtelWaitExec = 0x30510;
    public const nint AtelPopStackInteger = 0x30520;
    public const nint MovieFrameDelta = 0x30530;
    public const nint PobjCounter = 0x100;
    public const nint PpvFpGroupTable = 0x110;
    public const nint PpvFpManagers = 0x200;
    public const nint PpvPartManagerCount = 0x120;
    public const nint PpvPartManagers = 0x10000;
    public const nint BattleCursorBlinkGate = 0x30000;
    public const nint SgCount = 0x30010;
    public const nint DreamStarCounterCompare = 0x30020;
    public const nint DreamStarCounter = 0x30030;
    public const nint AtelMoveLeadInLoad = 0x30100;
    public const nint TOBtlCtrlLimitTimer = 0x30300;
    public const nint LimitTimerMode = 0x30400;
    public const nint LimitTimerFrames = 0x30410;
    public const nint LimitTimerBase = 0x30420;
    public const nint LimitTimerRaw = 0x30430;
    public const nint LimitTimerRounded = 0x30440;
    public const nint Rnd = 0x30450;
    public const nint AtelInitMotionRecords = 0x30460;
}
