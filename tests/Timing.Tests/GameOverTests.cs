using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class GameOverTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, 4)]
    public void Descriptor_header_preserves_pointer_bits_and_bounds_native_count(int declared, int captured)
    {
        byte[] bytes = new byte[0x98];
        BitConverter.TryWriteBytes(bytes.AsSpan(0x90), declared);
        BitConverter.TryWriteBytes(bytes.AsSpan(0x94), 0xf1234567u);
        var header = GameOverVfxHeader.Read(bytes);
        Assert.Equal(declared, header.DeclaredCount);
        Assert.Equal(0xf1234567u, header.Address);
        Assert.Equal(captured, header.CaptureCount);
        Assert.Equal(default, GameOverVfxHeader.Read(bytes.AsSpan(0, 0x97)));
    }

    [Fact]
    public void Capture_starts_at_game_over_and_expires_by_presented_frame()
    {
        var window = new GameOverProbeWindow(3, 20);
        Assert.False(window.TryRecord(669, 100));
        Assert.True(window.TryRecord(670, 200));
        Assert.True(window.TryRecord(670, 200));
        Assert.True(window.TryRecord(670, 202));
        Assert.False(window.TryRecord(670, 203));
        Assert.False(window.TryRecord(670, 300));
    }

    [Fact]
    public void Event_budget_bounds_multiple_calls_in_one_frame()
    {
        var window = new GameOverProbeWindow(180, 2);
        Assert.True(window.TryRecord(670, 1));
        Assert.True(window.TryRecord(670, 1));
        Assert.False(window.TryRecord(670, 1));
        Assert.False(window.TryRecord(670, 2));
    }

    [Fact]
    public void Leaving_game_over_stops_the_first_capture_permanently()
    {
        var window = new GameOverProbeWindow(180, 20);
        Assert.True(window.TryRecord(670, 1));
        Assert.False(window.TryRecord(-1, 2));
        Assert.False(window.TryRecord(670, 3));
    }
}
