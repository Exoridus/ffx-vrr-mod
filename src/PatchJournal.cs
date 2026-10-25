namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Records the original bytes of every in-process patch so shutdown can restore
///     the image exactly. Restoring in reverse order matters: two patches may overlap
///     when one widens an instruction the other rewrote.
///
///     Addresses are RVAs, the same convention <see cref="EngineAddresses"/> and FhUtil use.
///     The image base is added here; passing an absolute address would write outside the image.
/// </summary>
public sealed unsafe class PatchJournal
{
    private readonly List<(nint Address, byte[] Original, uint Protection)> _entries = new();

    public void Write(nint rva, ReadOnlySpan<byte> replacement)
    {
        nint address = (nint)FhUtil.ptr_at<byte>(rva);

        byte[] original = new byte[replacement.Length];
        new Span<byte>((void*)address, replacement.Length).CopyTo(original);

        // The engine's .text is read-only; the window is reopened for the copy only.
        if (!VirtualProtect(address, (nuint)replacement.Length, PAGE_EXECUTE_READWRITE, out uint prev))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"VirtualProtect failed at 0x{address:X8}");

        _entries.Add((address, original, prev));
        try
        {
            replacement.CopyTo(new Span<byte>((void*)address, replacement.Length));
            if (!FlushInstructionCache((nint)(-1), address, (nuint)replacement.Length))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"FlushInstructionCache failed at 0x{address:X8}");
        }
        finally
        {
            if (!VirtualProtect(address, (nuint)replacement.Length, prev, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Restoring protection failed at 0x{address:X8}");
        }
    }

    public void RestoreAll()
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            (nint address, byte[] original, uint protection) = _entries[i];
            if (!VirtualProtect(address, (nuint)original.Length, PAGE_EXECUTE_READWRITE, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"VirtualProtect failed at 0x{address:X8}");
            try
            {
                original.CopyTo(new Span<byte>((void*)address, original.Length));
                if (!FlushInstructionCache((nint)(-1), address, (nuint)original.Length))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"FlushInstructionCache failed at 0x{address:X8}");
            }
            finally
            {
                if (!VirtualProtect(address, (nuint)original.Length, protection, out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Restoring protection failed at 0x{address:X8}");
            }
            // Keep failed entries and earlier overlapping writes available for an ordered retry.
            _entries.RemoveAt(i);
        }
    }

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);
}
