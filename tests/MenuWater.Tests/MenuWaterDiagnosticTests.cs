using System.Text;
using Fahrenheit.Mods.Shared;
using Xunit;

public sealed class MenuWaterDiagnosticTests
{
    [Fact]
    public void RepeatedTitleLoadStopsBeforeOldAddressesCanBeReusedAsEvidence()
    {
        var capture = new MenuWaterDiagnosticWindow(3);
        capture.BeginLoad(0);
        capture.BeginLoad(1);
        Assert.True(capture.IsCapturing);
        capture.BeginDraw();
        capture.BeginLoad(0);
        Assert.False(capture.IsCapturing);
        Assert.False(capture.TryRecord());
        Assert.Equal("subsequent_title_load", capture.StopReason);
    }

    [Fact]
    public void StartupPreloadRetriesAreAllowedBeforeTheFirstWaterDraw()
    {
        var capture = new MenuWaterDiagnosticWindow(3);
        capture.BeginLoad(0);
        capture.BeginLoad(0);
        Assert.True(capture.IsCapturing);
    }

    [Fact]
    public void EventBudgetHasAnExplicitTerminalReason()
    {
        var capture = new MenuWaterDiagnosticWindow(2);
        Assert.True(capture.TryRecord());
        Assert.True(capture.TryRecord());
        Assert.False(capture.IsCapturing);
        Assert.False(capture.TryRecord());
        Assert.Equal("event_limit", capture.StopReason);
    }

    [Theory]
    [InlineData("/FFX_Data/GameData/PS3Data/menu/titl/D3D11/Water_00.dds.phyre", 0)]
    [InlineData("/ffx_data/gamedata/ps3data/menu/titl/d3d11/water_139.dds.phyre", 139)]
    [InlineData("/FFX_Data/GameData/PS3Data/menu/titl/D3D11/Water_140.dds.phyre", -1)]
    [InlineData("/FFX_Data/GameData/PS3Data/map/foo/Water_00.dds.phyre", -1)]
    [InlineData("/FFX_Data/GameData/PS3Data/menu/titl/D3D11/Water_+1.dds.phyre", -1)]
    public void IdentifiesOnlyBoundedTitleWaterSlots(string path, int slot)
        => Assert.Equal(slot, MenuWaterDiagnostic.Slot(Encoding.ASCII.GetBytes(path + "\0")));

    [Fact]
    public void UnterminatedPathIsUnavailable()
        => Assert.Equal(-1, MenuWaterDiagnostic.Slot("/FFX_Data"u8));

    [Fact]
    public void ReadsResourceOnlyFromCompleteMatchingTextureHeader()
    {
        byte[] bytes = new byte[0x2c];
        BitConverter.GetBytes(1024u).CopyTo(bytes, 0x1c);
        BitConverter.GetBytes(256u).CopyTo(bytes, 0x20);
        BitConverter.GetBytes(0x87654321u).CopyTo(bytes, 0x28);
        Assert.True(MenuWaterDiagnostic.Texture(bytes, out uint resource, out uint width, out uint height));
        Assert.Equal(0x87654321u, resource);
        Assert.Equal(1024u, width);
        Assert.Equal(256u, height);
        Assert.False(MenuWaterDiagnostic.Texture(bytes.AsSpan(0, 0x28), out _, out _, out _));
        bytes[0x1d] = 0;
        Assert.False(MenuWaterDiagnostic.Texture(bytes, out _, out _, out _));
    }
}
