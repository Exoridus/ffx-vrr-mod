using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class FarplaneDiagnosticsTests
{
    [Fact]
    public void Renderer_record_preserves_native_pointer_and_descriptor_bits()
    {
        byte[] header = new byte[0x60];
        byte[] targets = new byte[0x54];
        BitConverter.TryWriteBytes(header.AsSpan(4), 0x80000001u);
        BitConverter.TryWriteBytes(header.AsSpan(0x48), 0xf1234567u);
        BitConverter.TryWriteBytes(header.AsSpan(0x5c), 0xe1234567u);
        uint[] descriptors = Enumerable.Range(0, 20).Select(i => 0xd0000000u + (uint)i).ToArray();
        MemoryMarshal.AsBytes(descriptors.AsSpan()).CopyTo(targets);
        BitConverter.TryWriteBytes(targets.AsSpan(0x50), 0x11220103u);
        uint[] words = new uint[FarplaneDiagnosticPacket.WordCount];

        FarplaneDiagnosticPacket.CaptureRenderer(words, header, targets);

        Assert.InRange(words.Length, 40, 128);
        Assert.Equal(3u, words[11]);
        Assert.Equal(0x80000001u, words[12]);
        Assert.Equal(0xf1234567u, words[13]);
        Assert.Equal(0xe1234567u, words[14]);
        Assert.Equal(0x11220103u, words[15]);
        Assert.Equal(descriptors, words[16..36]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0x5f, 0x53)]
    public void Unreadable_or_partial_snapshots_clear_previous_native_fields(int headerBytes, int targetBytes)
    {
        uint[] words = Enumerable.Repeat(0xfeedbeefu, FarplaneDiagnosticPacket.WordCount).ToArray();
        FarplaneDiagnosticPacket.CaptureRenderer(words, new byte[headerBytes], new byte[targetBytes]);
        Assert.All(words[11..36], value => Assert.Equal(0u, value));
        Assert.All(words[..11], value => Assert.Equal(0xfeedbeefu, value));
        Assert.All(words[36..], value => Assert.Equal(0xfeedbeefu, value));
    }

    [Theory]
    [InlineData("h_farplane_begin_scene")]
    [InlineData("h_farplane_end_scene")]
    [InlineData("h_farplane_fmv_draw")]
    public void Disabled_capture_forwards_original_arguments_and_native_error_once(string callback)
    {
        NativeCalls.FarplaneCallCount = 0;
        nint instance = unchecked((nint)0x81234567u);
        object argument = callback == "h_farplane_fmv_draw" ? (object)unchecked((nint)0xe1234567u) : uint.MaxValue;
        var method = typeof(VrrModule).GetMethod(callback, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object? result = method.Invoke(new VrrModule(), [instance, argument]);
        Assert.Equal(0xfedcba98u, result);
        Assert.Equal(1, NativeCalls.FarplaneCallCount);
        Assert.Equal(instance, NativeCalls.FarplaneInstance);
        Assert.Equal(callback == "h_farplane_fmv_draw" ? 0xe1234567u : uint.MaxValue, NativeCalls.FarplaneArguments[0]);
    }

    [Fact]
    public void Clear_color_forwards_nan_and_signed_zero_bits_without_conversion()
    {
        NativeCalls.FarplaneCallCount = 0;
        uint[] values = [0x7fc12345u, 0x80000000u, 0xff800000u, 0x3f800000u];
        var method = typeof(VrrModule).GetMethod("h_farplane_clear_color", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object? result = method.Invoke(new VrrModule(), [(nint)1234, values[0], values[1], values[2], values[3]]);
        Assert.Equal(0xfedcba98u, result);
        Assert.Equal(1, NativeCalls.FarplaneCallCount);
        Assert.Equal((nint)1234, NativeCalls.FarplaneInstance);
        Assert.Equal(values, NativeCalls.FarplaneArguments);
    }
}

public unsafe sealed partial class VrrModule
{
    private BinaryTraceSession? _binaryDiagnostics = null;
    private long _frames = 0;
    private long _diagnosticLoopCounter = 0;
    private bool diagnostic_hook(HookSiteId site, string name, nint rva, byte[] expected, Func<bool> install) => HookSites.IsDeclared(site);
}

internal static class NativeBuild
{
    internal static bool TryValidate(out string error) { error = string.Empty; return true; }
}

internal unsafe static partial class NativeCalls
{
    internal static int FarplaneCallCount;
    internal static nint FarplaneInstance;
    internal static uint[] FarplaneArguments = [];
    public static uint FarplaneScene(nint instance, uint flags)
    {
        FarplaneCallCount++;
        FarplaneInstance = instance;
        FarplaneArguments = [flags];
        return 0xfedcba98u;
    }
    public static uint FarplaneDraw(nint manager, nint renderer) => FarplaneScene(manager, unchecked((uint)renderer));
    public static uint FarplaneColor(nint instance, uint red, uint green, uint blue, uint alpha)
    {
        FarplaneScene(instance, red);
        FarplaneArguments = [red, green, blue, alpha];
        return 0xfedcba98u;
    }
}
