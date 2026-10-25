using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class FmvLifecycleTests
{
    private const string Original = "../../../FFX_Data/GameData/PS3Data/Video/OPK_us.webm";
    private const string Replacement = "C:/Movies/OPK_us.webm";
    private readonly VrrModule _module = new();
    private readonly nint _manager = (nint)FhUtil.ptr_at<byte>(0x70000);

    public FmvLifecycleTests()
    {
        foreach (var site in new (nint Operand, nint Original)[]
        {
            (0x2D9E74, 0x74A1AC), (0x2D9E80, 0x74A1A0),
            (0x2D9E88, 0x74A1A8), (0x2D9E94, 0x74A198)
        })
        {
            byte* operand = FhUtil.ptr_at<byte>(site.Operand);
            operand[-2] = 0xd9; operand[-1] = 0x05;
            *(uint*)operand = (uint)(nint)FhUtil.ptr_at<float>(site.Original);
        }
        _module._overrides["FFX_Data/GameData/PS3Data/Video/OPK_us.webm"] = Replacement;
        Field<Dictionary<string, FmvReplacementMetadata>>("_fmv_metadata")[Replacement]
            = new(1920, 1080, 60000d / 1001, "unreviewed", "untested");
        NativeCalls.MovieName = Original;
        NativeCalls.InitializeMovieProbe = null;
        NativeCalls.PlayMovieProbe = null;
    }

    [Fact]
    public void InitializeChoosesDimensionsBeforeAllocationAndPlayResumeRetainsThem()
    {
        int nativeInitializations = 0;
        NativeCalls.InitializeMovieProbe = () =>
        {
            nativeInitializations++;
            nint dimensions = Field<nint>("_fmv_surface_values");
            Assert.NotEqual(0, dimensions);
            Assert.Equal(1920f, *(float*)dimensions);
            Assert.Equal(1080f, *(float*)(dimensions + 4));
            Assert.Equal(60000d / 1001, Field<double>("_active_fmv_rate"));
        };
        Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
        Assert.Equal(1, nativeInitializations);
        Assert.Equal("replacement selected; decode unverified", Field<string>("_fmv_last_outcome"));
        NativeCalls.PlayMovieProbe = () =>
        {
            Assert.Equal(Replacement, Marshal.PtrToStringAnsi(_manager + EngineAddresses.FmvPathOffset));
            Assert.Equal(60000d / 1001, Field<double>("_active_fmv_rate"));
        };
        Call("h_fmv_play_start", _manager, 0);
        Assert.Equal("replacement path applied; decode unverified", Field<string>("_fmv_last_outcome"));
        Call("h_fmv_play_start", _manager, 0);
        Assert.Equal(60000d / 1001, Field<double>("_active_fmv_rate"));

        NativeCalls.MovieName = "../../../FFX_Data/GameData/PS3Data/Video/ORIGINAL.webm";
        NativeCalls.InitializeMovieProbe = () =>
        {
            Assert.Equal(30000d / 1001, Field<double>("_active_fmv_rate"));
            Assert.Equal((uint)(nint)FhUtil.ptr_at<float>(0x74A1A8), FhUtil.get_at<uint>(0x2D9E88));
        };
        Call("h_fmv_initialize", _manager, 8, (byte)0, (byte)1);
        NativeCalls.PlayMovieProbe = () => Assert.EndsWith("ORIGINAL.webm", Marshal.PtrToStringAnsi(_manager + EngineAddresses.FmvPathOffset));
        Call("h_fmv_play_start", _manager, 0);
        Call("release_fmv_surfaces");
    }

    [Theory]
    [InlineData("C:/Movies/über.webm", false)]
    [InlineData("C:/Movies/refused.webm", true)]
    [InlineData("C:/Movies/valid.webm", false, 260)]
    [InlineData("C:/Movies/valid.webm", false, 120)]
    [InlineData("C:/Movies/valid.webm", false, 128)]
    public void IneligibleReplacementKeepsRetailSurfacesAndOriginalPath(string path, bool refused, int pad = 0)
    {
        if (pad > 0) path = "C:/" + new string('a', pad) + ".webm";
        _module._overrides["FFX_Data/GameData/PS3Data/Video/OPK_us.webm"] = path;
        Field<Dictionary<string, FmvReplacementMetadata>>("_fmv_metadata")[path]
            = new(1920, 1080, 60000d / 1001, "unreviewed", "untested");
        if (refused) Field<HashSet<string>>("_fmv_refused").Add(path);
        NativeCalls.InitializeMovieProbe = () =>
        {
            Assert.Equal(0, Field<nint>("_fmv_surface_values"));
            Assert.Equal(30000d / 1001, Field<double>("_active_fmv_rate"));
        };
        Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
        Assert.Equal("override rejected; retail", Field<string>("_fmv_last_outcome"));
        NativeCalls.PlayMovieProbe = () => Assert.Equal(Original, Marshal.PtrToStringAnsi(_manager + EngineAddresses.FmvPathOffset));
        Call("h_fmv_play_start", _manager, 0);
    }

    [Theory]
    [InlineData(1280, 720, 30)]
    [InlineData(1920, 1080, 30)]
    [InlineData(2560, 1440, 16)]
    [InlineData(3840, 2160, 6)]
    public void ReplacementBoundsDecodedQueueBeforeAllocationAndKeepsItOnResume(int width, int height, int expectedFrames)
    {
        Field<Dictionary<string, FmvReplacementMetadata>>("_fmv_metadata")[Replacement]
            = new(width, height, 60000d / 1001, "unreviewed", "untested");
        Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
        NativeCalls.PlayMovieProbe = () =>
        {
            Assert.Equal(expectedFrames, *(int*)(_manager + 0x38 + 0x158));
            *(int*)(_manager + 0x38 + 0xe4) = 1;
        };
        Call("h_fmv_play_start", _manager, 0);
        Call("h_fmv_play_start", _manager, 0);
        Call("release_fmv_surfaces");
    }

    [Fact]
    public void FailedAv1PlaybackStopsOnceOnOwnerThreadButDoesNotStopNextRetailMovie()
    {
        Field<Dictionary<string, FmvReplacementMetadata>>("_fmv_metadata")[Replacement]
            = new(1920, 1080, 60000d / 1001, "unreviewed", "untested") { VideoCodec = "av1" };
        Call("h_fmv_initialize", _manager, 7, (byte)0, (byte)1);
        Call("h_fmv_play_start", _manager, 0);
        var pending = typeof(VrrModule).GetField("_av1FailedPlayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        pending.SetValue(_module, _manager + 0x38);
        int before = FhCall.graphicDestroyFmv.Calls;
        Assert.Equal(true, Call("abort_failed_av1_movie"));
        Assert.Equal(before + 1, FhCall.graphicDestroyFmv.Calls);
        Assert.Equal(false, Call("abort_failed_av1_movie"));
        NativeCalls.MovieName = "../../../FFX_Data/GameData/PS3Data/Video/ORIGINAL.webm";
        Call("h_fmv_initialize", _manager, 8, (byte)0, (byte)1);
        pending.SetValue(_module, _manager + 0x38);
        Assert.Equal(false, Call("abort_failed_av1_movie"));
        Assert.Equal(before + 1, FhCall.graphicDestroyFmv.Calls);
        Call("release_fmv_surfaces");
    }
    [Fact]
    public void Av1ClockPreservesAuthoredDurationWhenContainerFallsBackToThirtyFps()
    {
        nint player = _manager + 0x38;
        *(double*)(player + 0x108) = 1000d / (30000d / 1001);
        var metadata = new FmvReplacementMetadata(1920, 1080, 60000d / 1001, "unreviewed", "untested");
        Call("configure_av1_clock", player, metadata);
        double elapsed = 480 * *(double*)(player + 0x108);
        Assert.Equal(8008d, elapsed, 6);
        Assert.Equal(240d, elapsed * (30000d / 1001) / 1000, 6);
    }
    private T Field<T>(string name) => (T)typeof(VrrModule).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_module)!;
    private object? Call(string name, params object[] args)
    {
        MethodInfo? method = typeof(VrrModule).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(_module, args);
    }
}

public unsafe sealed partial class VrrModule
{
    internal readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private static string normalise(string path) => path.Replace('\\', '/').StartsWith("../../../", StringComparison.Ordinal)
        ? path.Replace('\\', '/')[9..] : path.Replace('\\', '/');
}

internal static class MemoryProbe
{
    public static bool IsReadable(nint address, int length) => address != 0 && length > 0;
    internal static unsafe bool TryCopy(nint address, Span<byte> destination)
    {
        nuint offset = unchecked((nuint)address - (nuint)FhUtil.Memory);
        if (offset > 0x800000 || (nuint)destination.Length > 0x800000 - offset) return false;
        new ReadOnlySpan<byte>((void*)address, destination.Length).CopyTo(destination);
        return true;
    }
}

internal unsafe static partial class NativeCalls
{
    internal static string MovieName = "";
    internal static Action? InitializeMovieProbe, PlayMovieProbe;
    public static byte GetMovieName(nint manager, int movieId, byte* buffer)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(MovieName);
        bytes.CopyTo(new Span<byte>(buffer, 128));
        buffer[bytes.Length] = 0;
        return 1;
    }
    public static int InitializeMovie(nint manager, int movieId, byte highResolution, byte arg3)
    {
        InitializeMovieProbe?.Invoke();
        *(byte*)(manager + 0x38 + 0xc4) = 1;
        *(int*)(manager + 0x38 + 0xe4) = 0;
        *(int*)(manager + 0x38 + 0x158) = 30;
        byte[] path = Encoding.ASCII.GetBytes(MovieName + '\0');
        Marshal.Copy(path, 0, manager + EngineAddresses.FmvPathOffset, path.Length);
        return 1;
    }
    public static int PlayMovie(nint manager, int arg)
    {
        PlayMovieProbe?.Invoke();
        return 1;
    }
}

internal static partial class EngineAddresses
{
    public const nint FmvPlayStart = 0x30600;
    public const nint FmvInitialize = 0x30610;
    public const nint FmvGetVideoName = 0x30620;
    public const int FmvPathOffset = 0x4f0;
}

internal static class FhCall
{
    internal static class graphicDestroyFmv
    {
        internal static int Calls;
        internal static Action fnptr = () => Calls++;
    }
}