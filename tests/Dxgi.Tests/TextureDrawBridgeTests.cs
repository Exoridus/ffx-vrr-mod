global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;
using TerraFX.Interop.DirectX;
using Xunit;

namespace Fahrenheit.Mods.Vrr
{
    public unsafe sealed partial class VrrModule
    {
        private bool _textureFrameBound = true;
        internal int Inspections;
        internal bool Bind(nint context) => ensure_test_hook_registration() && texture_draw_device((ID3D11DeviceContext*)context);
        private bool texture_frame_blend_available() => false;
        private ID3D11ShaderResourceView* texture_frame_prepare_draw(ID3D11DeviceContext* context) { Inspections++; return null; }
        private static void texture_frame_restore_draw(ID3D11DeviceContext* context, ID3D11ShaderResourceView* native) => throw new InvalidOperationException();
        private void texture_blend_probe_draw(ID3D11DeviceContext* context, bool indexed, uint count) { }
    }

    internal readonly record struct FhCallLocation(nint Address, int Offset = 0);
    internal ref struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
    {
        internal T? fnptr { get; private set; } = null;
        internal bool hook(VrrModule owner, T callback)
        {
            TextureDrawBridgeTests.Callbacks.Add(location.Address, callback);
            return true;
        }
        internal FhCallHandle<T> chain_from(T callback)
        {
            TextureDrawBridgeTests.Resolutions++;
            fnptr = Marshal.GetDelegateForFunctionPointer<T>(TextureDrawBridgeTests.Originals.GetValueOrDefault(location.Address, location.Address));
            return this;
        }
    }
}

[Collection("Texture hooks")]
public unsafe sealed class TextureDrawBridgeTests
{
    internal static readonly Dictionary<nint, Delegate> Callbacks = [];
    internal static readonly Dictionary<nint, nint> Originals = [];
    internal static int Resolutions;
    private static int _indexed, _plain;
    private static (nint Context, uint Count, uint Start, int Vertex) _last;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OriginalIndexed(nint context, uint count, uint start, int vertex)
    { _indexed++; _last = (context, count, start, vertex); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OriginalPlain(nint context, uint count, uint start)
    { _plain++; _last = (context, count, start, 0); }

    [Fact]
    public void ActualSharedHookClosureForwardsUnmanagedEntriesAndResolvesEachOnce()
    {
        Callbacks.Clear(); Resolutions = _indexed = _plain = 0;
        nint* table = stackalloc nint[14];
        table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, int, void>)&OriginalIndexed;
        table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, void>)&OriginalPlain;
        nint context = (nint)(&table);
        var owner = new VrrModule();
        Assert.True(owner.Bind(context));
        Assert.True(owner.Bind(context));
        Assert.Equal(2, Callbacks.Count);
        var indexed = (delegate* unmanaged[Stdcall]<nint, uint, uint, int, void>)Marshal.GetFunctionPointerForDelegate(Callbacks[table[12]]);
        var plain = (delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Marshal.GetFunctionPointerForDelegate(Callbacks[table[13]]);
        for (int i = 0; i < 1000; i++) indexed(context, 132, 7, -3);
        Assert.Equal((context, 132u, 7u, -3), _last);
        for (int i = 0; i < 1000; i++) plain(context, 9, 2);
        Assert.Equal((context, 9u, 2u, 0), _last);
        Assert.Equal(1000, _indexed); Assert.Equal(1000, _plain);
        Assert.Equal(2, Resolutions); Assert.Equal(0, owner.Inspections);
        GC.KeepAlive(owner);
        Callbacks.Clear();
    }
}
