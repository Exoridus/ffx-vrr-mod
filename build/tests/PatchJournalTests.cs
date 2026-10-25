global using System.Runtime.InteropServices;
using System.Reflection;
using Fahrenheit.Mods.Vrr;
using Xunit;

public unsafe sealed class PatchJournalTests
{
    [Fact]
    public void RestoresOverlappingWritesInReverseOrder()
    {
        nint page = VirtualAlloc(0, 4096, 0x3000, 0x04);
        Assert.NotEqual(0, page);
        try
        {
            new Span<byte>((void*)page, 4).Fill(9);
            var journal = new PatchJournal();
            journal.Write(page, [1, 2, 3]);
            journal.Write(page + 1, [4, 5, 6]);
            Assert.Equal(new byte[] { 1, 4, 5, 6 }, new ReadOnlySpan<byte>((void*)page, 4).ToArray());
            journal.RestoreAll();
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, new ReadOnlySpan<byte>((void*)page, 4).ToArray());
            Assert.Equal(0, EntryCount(journal));
        }
        finally { Assert.True(VirtualFree(page, 0, 0x8000)); }
    }

    [Fact]
    public void ReportsFailedRestoreAndRetainsUndoBytes()
    {
        nint page = VirtualAlloc(0, 4096, 0x3000, 0x04);
        Assert.NotEqual(0, page);
        try
        {
            *(byte*)page = 9;
            var journal = new PatchJournal();
            journal.Write(page, [1]);
            journal.Write(page, [2]);
            Assert.True(VirtualFree(page, 4096, 0x4000));
            Assert.Throws<System.ComponentModel.Win32Exception>(journal.RestoreAll);
            Assert.Equal(2, EntryCount(journal));
            Assert.Equal(page, VirtualAlloc(page, 4096, 0x1000, 0x04));
            journal.RestoreAll();
            Assert.Equal((byte)9, *(byte*)page);
            Assert.Equal(0, EntryCount(journal));
        }
        finally { Assert.True(VirtualFree(page, 0, 0x8000)); }
    }

    private static int EntryCount(PatchJournal journal)
        => ((System.Collections.ICollection)typeof(PatchJournal)
            .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(journal)!).Count;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}

namespace Fahrenheit.Mods.Vrr
{
    internal unsafe static class FhUtil
    {
        internal static T* ptr_at<T>(nint rva) where T : unmanaged => (T*)rva;
    }
}
