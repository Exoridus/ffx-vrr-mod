using Fahrenheit.Mods.Vrr;
using TerraFX.Interop.DirectX;
using Xunit;

namespace Fahrenheit.Mods.Vrr
{
    public unsafe sealed partial class VrrModule
    {
        private readonly TestTextureConfig _config = new();
        private readonly TestTextureLogger _logger = new();
        private readonly object _textureFrameLock = new(), _textureBlendProbeLock = new();
        private TextureFrameBank? _textureFrameBank;
        private nint _textureFrameContext;
        private bool _textureProbeReady = true, _textureProbeStopped;
        internal int ProbeDevices, FrameDevices;
        internal readonly List<string> Events = [];
        private sealed class TextureFrameBank : IDisposable
        {
            internal bool Disposed;
            public void Dispose() => Disposed = true;
        }
        internal bool InitObserver(bool probe = true, bool blend = true)
        { _config.TextureBlendProbe = probe; _config.TextureFrameInterpolation = blend; return init_texture_device_observer(); }
    private bool ensure_test_hook_registration(bool independent = false)
    {
        if (_hookRegistration is not null) return true;
        VrrConfig config = new VrrConfig { TextureBlendProbe = _config.TextureBlendProbe, TextureFrameInterpolation = _config.TextureFrameInterpolation };
        var selections = Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared)
            .Select(site => HookRegistrationPlan.ResolveSelection(site, config, independent, true, false, null));
        return HookRegistrationPlan.TryCreate(selections, HookRegistrationPlan.AvailableSourceContracts,
            out _hookRegistration, out _);
    }

        private bool hook_or_log(HookSiteId site, string label, nint address, Func<bool> install)
            => ensure_test_hook_registration() && register_hook(site, label, address, install);
        private static bool texture_binding_prefix(nint address, ReadOnlySpan<byte> expected)
            => address == 0x195800 && expected.SequenceEqual(TestTextureMemory.Entry);
        private void texture_blend_probe_device(ID3D11DeviceContext* context) { ProbeDevices++; }
        private void texture_frame_interpolation_device(ID3D11DeviceContext* context) { FrameDevices++; _textureFrameContext = (nint)context; }
        private void diagnostic_event(string name, object data) => Events.Add(name);
        internal void SeedBank() => _textureFrameBank = new();
        internal bool IsRetainedUndisposed => _textureDeviceRetainedBank is { Disposed: false } && _textureFrameBank is null;
        internal bool TextureDisabled => !_textureFrameBound && !_textureProbeReady && _textureProbeStopped && _textureFrameContext == 0;
        internal void OrdinaryInvalidation() { lock (_textureFrameLock) { _textureFrameBank?.Dispose(); _textureFrameBank = null; } }
    }
    internal sealed class TestTextureConfig { internal bool TextureBlendProbe = true, TextureFrameInterpolation = true; }
    internal sealed class TestTextureLogger { internal void Warning(string text) { } internal void Error(string text) { } internal void Info(string text) { } }
    internal static class NativeBuild
    {
        internal static bool Valid = true;
        internal static bool TryValidate(out string error) { error = ""; return Valid; }
    }
    internal static class TestTextureMemory
    {
        internal static byte[] Entry = [0x55, 0x8b, 0xec, 0x83, 0xec, 0x40];
    }
    internal static unsafe class MemoryProbe
    {
        internal static bool TryCopy(nint source, Span<byte> destination)
        { new ReadOnlySpan<byte>((void*)source, destination.Length).CopyTo(destination); return true; }
    }
}

[CollectionDefinition("Texture hooks", DisableParallelization = true)]
public sealed class TextureHookCollection { }

[Collection("Texture hooks")]
public unsafe sealed class TextureDeviceTests
{
    private static int _result, _calls;
    private static (nint, uint, uint, uint, uint, uint, nint) _args;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static int NativeCreate(nint owner, uint width, uint height, uint samples, uint windowed, uint mode, nint window)
    { _calls++; _args = (owner, width, height, samples, windowed, mode, window); return _result; }

    [Fact]
    public void ActualNativeObserverGuardsForwardsAndRetainsOneGeneration()
    {
        TextureDrawBridgeTests.Callbacks.Clear();
        TextureDrawBridgeTests.Originals[0x195800] = (nint)(delegate* unmanaged[Thiscall]<nint,uint,uint,uint,uint,uint,nint,int>)&NativeCreate;
        try
        {
            Assert.True(new VrrModule().InitObserver(false, false));
            Assert.Empty(TextureDrawBridgeTests.Callbacks);
            NativeBuild.Valid = false;
            Assert.False(new VrrModule().InitObserver());
            NativeBuild.Valid = true;
            TestTextureMemory.Entry[5] = 0x41;
            Assert.False(new VrrModule().InitObserver());
            TestTextureMemory.Entry[5] = 0x40;
            var module = new VrrModule();
            Assert.True(module.InitObserver());
            Assert.True(module.InitObserver());
            Assert.Single(TextureDrawBridgeTests.Callbacks);
            var callback = TextureDrawBridgeTests.Callbacks[0x195800];
            var hook = (delegate* unmanaged[Thiscall]<nint,uint,uint,uint,uint,uint,nint,int>)Marshal.GetFunctionPointerForDelegate(callback);
            byte* owner = stackalloc byte[0xb4];
            new Span<byte>(owner, 0xb4).Clear();
            *(uint*)(owner+0x6c) = 11; *(uint*)(owner+0x70) = 22; *(uint*)(owner+0x74) = 33;
            _calls = 0;
            foreach (int result in new[] { 9, 1, -1, unchecked((int)0x80004005) })
            {
                _result = result;
                Assert.Equal(result, hook((nint)owner, 1920, 1080, 4, 0x123401, 0x567802, 0x1234));
                Assert.Equal(((nint)owner,1920u,1080u,4u,0x123401u,0x567802u,(nint)0x1234), _args);
            }
            Assert.Equal(4, _calls); Assert.Equal(0, module.FrameDevices);
            _result = 0;
            Assert.Equal(0, hook(0, 1, 2, 3, 4, 5, 6));
            foreach (int offset in new[] { 0x6c, 0x70, 0x74 })
            {
                uint saved = *(uint*)(owner+offset); *(uint*)(owner+offset) = 0;
                Assert.Equal(0, hook((nint)owner, 1, 2, 3, 4, 5, 6));
                *(uint*)(owner+offset) = saved;
            }
            Assert.Equal(0, module.FrameDevices);
            byte[] descriptor = new ReadOnlySpan<byte>(owner+0x78, 0x3c).ToArray();
            Assert.Equal(0, hook((nint)owner, 1, 2, 3, 4, 5, 6));
            Assert.Equal(1, module.FrameDevices); Assert.Equal(1, module.ProbeDevices);
            Assert.Equal(descriptor, new ReadOnlySpan<byte>(owner+0x78, 0x3c).ToArray());
            module.SeedBank();
            Assert.Equal(0, hook((nint)owner, 1, 2, 3, 4, 5, 6));
            Assert.True(module.TextureDisabled); Assert.True(module.IsRetainedUndisposed);
            module.OrdinaryInvalidation();
            Assert.True(module.IsRetainedUndisposed);
            Assert.Equal(0, hook((nint)owner, 1, 2, 3, 4, 5, 6));
            Assert.True(module.IsRetainedUndisposed); Assert.Equal(1, module.FrameDevices);
            Assert.Single(module.Events, x => x == "texture_device_recreation_refused");
            Assert.Equal(11, _calls);
            GC.KeepAlive(module); GC.KeepAlive(callback);
        }
        finally
        {
            NativeBuild.Valid = true; TestTextureMemory.Entry[5] = 0x40;
            TextureDrawBridgeTests.Callbacks.Clear(); TextureDrawBridgeTests.Originals.Clear();
        }
    }
}
