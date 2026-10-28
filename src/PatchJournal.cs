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
    private readonly List<(nint Address, byte[] Original, byte[] Replacement)> _prepared = new();
    private bool _preparing;
    private bool _commitStarted;
    internal bool IsPreparing => _preparing;

    /// <summary>Queues copied replacements on an empty journal until commit or cleanup.</summary>
    internal void BeginPreparation()
    {
        if (_preparing || _entries.Count != 0)
            throw new InvalidOperationException("Preparation requires an empty journal.");
        _preparing = true;
    }

    /// <summary>
    /// Rechecks every native snapshot before applying the ordered set. The caller must
    /// serialize access and keep the image quiescent during validation and publication. After an apply
    /// failure, RestoreAll must succeed before reuse; undo and pending work are retained.
    /// Successful commit returns later writes to immediate mode.
    /// </summary>
    internal void CommitPrepared()
    {
        ValidatePrepared();

        _commitStarted = true;
        foreach (var patch in _prepared)
            Apply(patch.Address, patch.Replacement);
        _prepared.Clear();
        _preparing = false;
        _commitStarted = false;
    }

    internal void ValidatePrepared()
    {
        if (!_preparing || _commitStarted)
            throw new InvalidOperationException("No prepared set is available for commit.");
        foreach (var patch in _prepared)
            if (!ReadBytes(patch.Address, patch.Original.Length).AsSpan().SequenceEqual(patch.Original))
                throw new InvalidOperationException($"Prepared patch bytes changed at 0x{patch.Address:X8}.");
    }

    internal bool HasPreparedOverlap(PatchJournal other)
    {
        foreach (var first in _prepared)
            foreach (var second in other._prepared)
            {
                if (first.Replacement.Length == 0 || second.Replacement.Length == 0) continue;
                nuint a = (nuint)first.Address, b = (nuint)second.Address;
                if (a <= b ? b - a < (nuint)first.Replacement.Length : a - b < (nuint)second.Replacement.Length)
                    return true;
            }
        return false;
    }

    public void Write(nint rva, ReadOnlySpan<byte> replacement)
    {
        if (_commitStarted)
            throw new InvalidOperationException("Restore the failed prepared set before writing again.");
        nint address = (nint)FhUtil.ptr_at<byte>(rva);
        if (_preparing)
        {
            _prepared.Add((address, ReadBytes(address, replacement.Length), replacement.ToArray()));
            return;
        }
        Apply(address, replacement);
    }

    private static byte[] ReadBytes(nint address, int length)
    {
        byte[] bytes = new byte[length];
        if (!ReadProcessMemory((nint)(-1), address, bytes, (nuint)length, out nuint read) || read != (nuint)length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"Reading patch bytes failed at 0x{address:X8}");
        return bytes;
    }

    private void Apply(nint address, ReadOnlySpan<byte> replacement)
    {
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

    /// <summary>Restores applied patches in reverse order, then discards unpublished work. Failures retain undo for retry.</summary>
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
        _prepared.Clear();
        _preparing = false;
        _commitStarted = false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint size, out nuint bytesRead);

    private const uint PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualProtect(nint lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);
}
