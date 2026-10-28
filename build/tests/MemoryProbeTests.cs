using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class MemoryProbeTests
{
    [Fact]
    public void ReadableHighAllocationIsAcceptedAndCached()
    {
        nint page = 0;
        for (uint candidate = 0x90000000; candidate <= 0xe0000000 && page == 0; candidate += 0x10000000)
            page = VirtualAlloc(unchecked((nint)candidate), 8192, 0x3000, 0x04);
        Assert.NotEqual(0, page);
        try
        {
            MemoryProbe.NextFrame();
            long before = MemoryProbe.QueryCalls;
            Assert.True(MemoryProbe.IsReadable(page, 8192));
            Assert.True(MemoryProbe.IsReadable(page + 128, 64));
            Assert.Equal(before + 1, MemoryProbe.QueryCalls);
            Assert.True(MemoryProbe.TryCopy(page + 128, new byte[64]));
        }
        finally { Assert.True(VirtualFree(page, 0, 0x8000)); MemoryProbe.NextFrame(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void InvalidLengthCannotHitReadableCache(int length)
    {
        WithPage(page =>
        {
            Assert.True(MemoryProbe.IsReadable(page, 4096));
            long before = MemoryProbe.QueryCalls;
            Assert.False(MemoryProbe.IsReadable(page + 128, length));
            Assert.Equal(before, MemoryProbe.QueryCalls);
        });
    }

    [Fact]
    public void WrappingRangeIsRejectedWithoutQuerying()
    {
        WithPage(page =>
        {
            Assert.True(MemoryProbe.IsReadable(page, 4096));
            long before = MemoryProbe.QueryCalls;
            Assert.False(MemoryProbe.IsReadable(unchecked((nint)(nuint.MaxValue - 7)), 16));
            Assert.Equal(before, MemoryProbe.QueryCalls);
        });
    }

    [Fact]
    public void CacheDoesNotAcceptRangePastRegionEnd()
    {
        WithPage(page =>
        {
            Assert.True(VirtualProtect(page + 4096, 4096, 0x01, out _));
            Assert.True(MemoryProbe.IsReadable(page, 4096));
            Assert.False(MemoryProbe.IsReadable(page + 4095, 2));
        });
    }

    [Fact]
    public void NewFrameRechecksChangedProtectionAndCachesMiss()
    {
        WithPage(page =>
        {
            Assert.True(MemoryProbe.IsReadable(page, 16));
            Assert.True(VirtualProtect(page, 8192, 0x01, out _));
            MemoryProbe.NextFrame();
            long before = MemoryProbe.QueryCalls;
            Assert.False(MemoryProbe.IsReadable(page, 16));
            Assert.False(MemoryProbe.IsReadable(page + 128, 16));
            Assert.Equal(before + 1, MemoryProbe.QueryCalls);
        });
    }

    private static void WithPage(Action<nint> test)
    {
        nint page = VirtualAlloc(0, 8192, 0x3000, 0x04);
        Assert.NotEqual(0, page);
        MemoryProbe.NextFrame();
        try { test(page); }
        finally { Assert.True(VirtualFree(page, 0, 0x8000)); MemoryProbe.NextFrame(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
