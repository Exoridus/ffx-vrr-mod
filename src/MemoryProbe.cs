using System.Threading;

namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Whether an engine pointer can be dereferenced, asked of the memory manager rather than
///     guessed from the value. Used where a pointer is read from a global that may hold a stale
///     or not yet initialised value and the bytes behind it are read without any other guard.
/// </summary>
internal static unsafe class MemoryProbe
{
    /// <summary>Copies readable bytes without dereferencing a pointer that may become stale during scene teardown.</summary>
    internal static bool TryCopy(nint address, Span<byte> destination)
    {
        if (destination.IsEmpty || address == 0) return false;
        fixed (byte* target = destination)
            return ReadProcessMemory((nint)(-1), address, target, (nuint)destination.Length, out nuint read)
                && read == (nuint)destination.Length;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint read);

    /// <summary>
    ///     True when every byte of [address, address + length) lies in one committed region whose
    ///     protection allows reads. Null, the first 64 KiB, a reserved or free region, a guard page
    ///     and PAGE_NOACCESS all read as false.
    /// </summary>
    public static bool IsReadable(nint address, int length)
    {
        if (address <= 0x10000) return false;

        // The particle qualification asks this tens of thousands of times per frame about the same few
        // heap regions; a system call each made field scenes cost milliseconds per pass. A readable
        // region is trusted only until the next frame boundary, when teardown may have released it.
        long epoch = Volatile.Read(ref _epoch);
        nuint[] bases = _cacheBase ??= new nuint[CacheSlots], ends = _cacheEnd ??= new nuint[CacheSlots];
        nuint[] misses = _missPage ??= new nuint[CacheSlots];
        if (_cacheEpoch != epoch) { _cacheCount = 0; _missCount = 0; _cacheEpoch = epoch; }
        for (int i = 0; i < _cacheCount; i++)
            if ((nuint)address >= bases[i] && (nuint)address + (nuint)length <= ends[i]) return true;
        // Unreadable answers are asked again just as often, and each one was a VirtualQuery under the
        // address-space lock the loader needs: scene transitions slowed down by seconds.
        nuint page = (nuint)address & ~(nuint)0xFFF;
        for (int i = 0; i < _missCount; i++)
            if (misses[i] == page) return false;

        // A constant upper bound is wrong here. FFX.exe is routinely patched large-address-aware,
        // so a legitimate heap address can sit above 0x7FFF0000, and Fahrenheit revisions before
        // afbc37d reserved top-down on purpose, which put every allocation there. Only the memory
        // manager knows whether the page is there.
        long started = Stopwatch.GetTimestamp();
        nuint queried = VirtualQuery(address, out MEMORY_BASIC_INFORMATION info, (nuint)sizeof(MEMORY_BASIC_INFORMATION));
        Interlocked.Increment(ref QueryCalls);
        Interlocked.Add(ref QueryTicks, Stopwatch.GetTimestamp() - started);
        nuint region_end = (nuint)info.BaseAddress + info.RegionSize;
        if (queried == 0 || info.State != MEM_COMMIT
            || (info.Protect & PAGE_GUARD) != 0 || (info.Protect & PAGE_READABLE) == 0)
        {
            // The page itself is unreadable, so every range starting in it is too.
            Interlocked.Increment(ref QueryMisses);
            misses[_missCount < CacheSlots ? _missCount++ : (_missNext++ % CacheSlots)] = page;
            return false;
        }
        if ((nuint)address + (nuint)length > region_end) return false;
        int slot = _cacheCount < CacheSlots ? _cacheCount++ : (_cacheNext++ % CacheSlots);
        bases[slot] = (nuint)info.BaseAddress;
        ends[slot] = region_end;
        return true;
    }

    /// <summary>Drops cached regions; call once per frame.</summary>
    internal static void NextFrame() => Interlocked.Increment(ref _epoch);

    // Telemetry: VirtualQuery calls, those that answered unreadable, and the time spent in them since start.
    internal static long QueryCalls, QueryMisses, QueryTicks;

    private const int CacheSlots = 64;
    private static long _epoch;
    [ThreadStatic] private static long _cacheEpoch;
    [ThreadStatic] private static int _cacheCount, _cacheNext, _missCount, _missNext;
    [ThreadStatic] private static nuint[]? _missPage;
    [ThreadStatic] private static nuint[]? _cacheBase;
    [ThreadStatic] private static nuint[]? _cacheEnd;

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
