namespace Fahrenheit.Mods.FpsUnlock;

/// <summary>
///     Whether an engine pointer can be dereferenced, asked of the memory manager rather than
///     guessed from the value. Used where a pointer is read from a global that may hold a stale
///     or not yet initialised value and the bytes behind it are read without any other guard.
/// </summary>
internal static unsafe class MemoryProbe
{
    /// <summary>
    ///     True when every byte of [address, address + length) lies in one committed region whose
    ///     protection allows reads. Null, the first 64 KiB, a reserved or free region, a guard page
    ///     and PAGE_NOACCESS all read as false.
    /// </summary>
    public static bool IsReadable(nint address, int length)
    {
        if (address <= 0x10000) return false;

        // A constant upper bound is wrong here. FFX.exe is routinely patched large-address-aware,
        // so a legitimate heap address can sit above 0x7FFF0000, and Fahrenheit revisions before
        // afbc37d reserved top-down on purpose, which put every allocation there. Only the memory
        // manager knows whether the page is there.
        if (VirtualQuery(address, out MEMORY_BASIC_INFORMATION info, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0)
            return false;

        if (info.State != MEM_COMMIT) return false;
        if ((info.Protect & PAGE_GUARD) != 0 || (info.Protect & PAGE_READABLE) == 0) return false;

        nuint region_end = (nuint)info.BaseAddress + info.RegionSize;
        return (nuint)address + (nuint)length <= region_end;
    }

    private const uint MEM_COMMIT    = 0x1000;
    private const uint PAGE_GUARD    = 0x100;
    // PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE
    // | PAGE_EXECUTE_WRITECOPY. PAGE_NOACCESS and PAGE_EXECUTE are the two that fault on a read.
    private const uint PAGE_READABLE = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint  BaseAddress;
        public nint  AllocationBase;
        public uint  AllocationProtect;
        public nuint RegionSize;
        public uint  State;
        public uint  Protect;
        public uint  Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nuint dwLength);
}
