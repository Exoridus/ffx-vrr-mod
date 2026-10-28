global using System.Runtime.InteropServices;
using Fahrenheit.Mods.Vrr;
using System.Text.Json;
using Xunit;

public unsafe sealed class MenuWaterPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "water-preflight-" + Guid.NewGuid());

    public MenuWaterPreflightTests()
    {
        Directory.CreateDirectory(_root);
        FhApi.Mods.Root = _root;
        FhApi.Mods.HasLoader = true;
        NativeMemory.Clear(FhUtil.Memory, 0x300000);
        foreach (var (rva, bytes) in new (int, byte[])[] {
            (0x25b62f, [0xff,0x34,0x85,0x40,0x9c,0xc5,0]),
            (0x27cba1, [0xbe,0x40,0x9c,0xc5,0]),
            (0x27cbc0, [0x81,0xfe,0x58,0x9d,0xc5,0]),
            (0x27efb5, [0xbe,0x40,0x9c,0xc5,0]),
            (0x27f015, [0x81,0xfe,0x58,0x9d,0xc5,0]),
            (0x25b6d9, [0x80,0xf9,0x46]) })
            bytes.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(rva), bytes.Length));
    }

    [Fact]
    public void MissingPackIsSkippedBeforeAnyNativePatchOrAllocation()
    {
        var module = new VrrModule(Path.Combine(_root, "x"));
        Assert.True(module.Preflight());
        Assert.False(module.HasPreparedPaths);
        Assert.False(module.HasNativeStorage);
        Assert.Equal(0, PatchJournal.Writes);
    }

    [Fact]
    public void ValidPackPreparesOnceWithoutChangingNativeGuards()
    {
        string pack = CreatePack();
        var module = new VrrModule(pack);
        Assert.True(module.Preflight());
        Assert.True(module.HasPreparedPaths);
        Assert.False(module.HasNativeStorage);
        Assert.Equal(0, PatchJournal.Writes);
        Assert.Equal((byte)0x46, *FhUtil.ptr_at<byte>(0x25b6db));
    }

    [Fact]
    public void InvalidNativeGuardClearsPreviouslyPreparedPaths()
    {
        var module = new VrrModule(CreatePack());
        Assert.True(module.Preflight());
        *FhUtil.ptr_at<byte>(0x25b6d9) = 0;
        Assert.True(module.Preflight());
        Assert.False(module.HasPreparedPaths);
        Assert.False(module.HasNativeStorage);
        Assert.Equal(0, PatchJournal.Writes);
    }

    [Fact]
    public void InstallationWithoutPreflightLeavesNativeWater()
    {
        var module = new VrrModule(CreatePack());
        Assert.True(module.Initialize());
        Assert.False(module.HasNativeStorage);
        Assert.Equal(0, PatchJournal.Writes);
    }

    private string CreatePack()
    {
        string root = Path.Combine(_root, "x");
        var files = new List<object>();
        for (int slot = 0; slot < 140; slot++)
        {
            string relative = $"ffx_data/gamedata/ps3data/menu/titl/d3d11/water_{slot:00}.dds.phyre";
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [(byte)slot]);
            files.Add(new { slot, path = relative,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([(byte)slot])) });
        }
        File.WriteAllText(Path.Combine(root, "menu-water-pack.json"), JsonSerializer.Serialize(new {
            schema_version = 1, frame_count = 140, fps = 60, width = 1024, height = 256,
            format = "DXT5", mip_count = 1, files }));
        return root;
    }

    public void Dispose() => Directory.Delete(_root, true);
}

namespace Fahrenheit.Mods.Vrr
{
    public unsafe sealed partial class VrrModule
    {
        private readonly TestConfig _config;
        private readonly TestLogger _logger = new();
        internal VrrModule(string root) { _config = new(root); PatchJournal.Writes = 0; }
        internal bool Preflight() => preflight_expanded_menu_water();
        internal bool Initialize() => init_expanded_menu_water();
        internal bool HasPreparedPaths => _expanded_menu_water_prepared_paths is not null;
        internal bool HasNativeStorage => _expanded_menu_water_table != 0 || _expanded_menu_water_paths.Count != 0;
        private static float TargetFramerate => 60;
    }
    internal sealed record TestConfig(string ExpandedMenuWaterPack) { internal bool MenuWater => true; }
    internal sealed class TestLogger { internal void Error(string message) { } internal void Warning(string message) { } internal void Info(string message) { } }
    internal sealed class PatchJournal
    {
        internal static int Writes;
        internal void Write(nint rva, ReadOnlySpan<byte> bytes, bool skipRestoreIfChanged = false) => Writes++;
        internal void RestoreAll() { }
    }
    internal unsafe static class FhUtil
    {
        internal static readonly void* Memory = NativeMemory.AllocZeroed(0x300000);
        internal static T* ptr_at<T>(nint rva) where T : unmanaged => rva == 0 ? (T*)0x400000 : (T*)((byte*)Memory + rva);
        internal static void set_at<T>(nint rva, T value) where T : unmanaged => *ptr_at<T>(rva) = value;
    }
    internal static class EngineAddresses { internal const nint MenuWaterFrame = 0x100; }
    internal static class RenderPassContext { internal static bool IsActive => false; internal static double TakeWaterSeconds() => 0; }
    internal static class FhApi { internal static TestMods Mods { get; } = new(); }
    internal sealed class TestMods
    {
        internal string Root = "";
        internal bool HasLoader;
        internal IEnumerable<TestModuleContext> get_modules() => HasLoader ? [new(new Fahrenheit.Runtime.FhFileLoaderModule())] : [];
        internal IEnumerable<TestMod> get_mods() => [new(new(new DirectoryInfo(Root)))];
    }
    internal sealed record TestModuleContext(object Module);
    internal sealed record TestMod(TestPaths Paths);
    internal sealed record TestPaths(DirectoryInfo EflDir);
}
namespace Fahrenheit.Runtime { public sealed class FhFileLoaderModule { } }
