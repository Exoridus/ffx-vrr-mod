using System.Text;

namespace Fahrenheit.Mods.Shared;

internal sealed class MenuWaterDiagnosticWindow(int limit)
{
    private int _drawn, _events;
    private string? _stopReason;
    internal bool IsCapturing => StopReason is null;
    internal string? StopReason => System.Threading.Volatile.Read(ref _stopReason);
    internal void BeginLoad(int slot)
    {
        if (slot == 0 && System.Threading.Volatile.Read(ref _drawn) != 0)
            System.Threading.Interlocked.CompareExchange(ref _stopReason, "subsequent_title_load", null);
    }
    internal void BeginDraw() => System.Threading.Interlocked.Exchange(ref _drawn, 1);
    internal bool TryRecord()
    {
        if (!IsCapturing) return false;
        int count = System.Threading.Interlocked.Increment(ref _events);
        if (count >= limit) System.Threading.Interlocked.CompareExchange(ref _stopReason, "event_limit", null);
        return count <= limit;
    }
}

internal static class MenuWaterDiagnostic
{
    internal static int Slot(ReadOnlySpan<byte> path)
    {
        int end = path.IndexOf((byte)0);
        if (end < 0) return -1;
        string text = Encoding.ASCII.GetString(path[..end]).Replace('\\', '/');
        const string prefix = "/ffx_data/gamedata/ps3data/menu/titl/d3d11/water_";
        const string suffix = ".dds.phyre";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return -1;
        var digits = text.AsSpan(prefix.Length, text.Length - prefix.Length - suffix.Length);
        if (digits.Length is < 2 or > 3) return -1;
        int slot = 0;
        foreach (char digit in digits)
        {
            if (digit is < '0' or > '9') return -1;
            slot = slot * 10 + digit - '0';
        }
        return slot < 140 ? slot : -1;
    }

    internal static bool Texture(ReadOnlySpan<byte> bytes, out uint resource, out uint width, out uint height)
    {
        resource = width = height = 0;
        if (bytes.Length < 0x2c) return false;
        width = BitConverter.ToUInt32(bytes[0x1c..]);
        height = BitConverter.ToUInt32(bytes[0x20..]);
        resource = BitConverter.ToUInt32(bytes[0x28..]);
        return width == 1024 && height == 256 && resource != 0;
    }
}
