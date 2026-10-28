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

    [Fact]
    public void PreparationCopiesReplacementsAndPreservesOverlapOrder()
    {
        WithPage(page =>
        {
            new Span<byte>((void*)page, 4).Fill(9);
            var journal = new PatchJournal();
            journal.BeginPreparation();
            byte[] replacement = [1, 2, 3];
            journal.Write(page, replacement);
            replacement[0] = 8;
            journal.Write(page + 1, [4, 5, 6]);
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, new ReadOnlySpan<byte>((void*)page, 4).ToArray());
            journal.CommitPrepared();
            Assert.Equal(new byte[] { 1, 4, 5, 6 }, new ReadOnlySpan<byte>((void*)page, 4).ToArray());
            journal.Write(page, [7]);
            Assert.Equal((byte)7, *(byte*)page);
            journal.RestoreAll();
            Assert.Equal(new byte[] { 9, 9, 9, 9 }, new ReadOnlySpan<byte>((void*)page, 4).ToArray());
        });
    }

    [Fact]
    public void StalePreparedBytesRejectWholeSetBeforeFirstWrite()
    {
        WithPage(page =>
        {
            new Span<byte>((void*)page, 2).Fill(9);
            var journal = new PatchJournal();
            journal.BeginPreparation();
            journal.Write(page, [1]);
            journal.Write(page + 1, [2]);
            *(byte*)(page + 1) = 8;
            Assert.Throws<InvalidOperationException>(() => journal.CommitPrepared());
            Assert.Equal(new byte[] { 9, 8 }, new ReadOnlySpan<byte>((void*)page, 2).ToArray());
            Assert.Equal(0, EntryCount(journal));
            *(byte*)(page + 1) = 9;
            journal.CommitPrepared();
            Assert.Equal(new byte[] { 1, 2 }, new ReadOnlySpan<byte>((void*)page, 2).ToArray());
            journal.RestoreAll();
        });
    }

    [Fact]
    public void CleanupBeforeCommitDiscardsWorkWithoutRestoringUntouchedMemory()
    {
        WithPage(page =>
        {
            *(byte*)page = 9;
            var journal = new PatchJournal();
            journal.BeginPreparation();
            journal.Write(page, [1]);
            *(byte*)page = 8;
            journal.RestoreAll();
            Assert.Equal((byte)8, *(byte*)page);
            Assert.Throws<InvalidOperationException>(() => journal.CommitPrepared());
            journal.Write(page, [2]);
            Assert.Equal((byte)2, *(byte*)page);
            journal.RestoreAll();
            Assert.Equal((byte)8, *(byte*)page);
        });
    }

    [Fact]
    public void PartialCommitProtectionFailureRetainsUndoUntilCleanup()
    {
        WithPage(page =>
        {
            nint mapping = CreateFileMappingW(-1, 0, 0x02, 0, 4096, null);
            Assert.NotEqual(0, mapping);
            nint view = MapViewOfFile(mapping, 0x04, 0, 0, 4096);
            Assert.NotEqual(0, view);
            try
            {
                *(byte*)page = 9;
                var journal = new PatchJournal();
                journal.BeginPreparation();
                journal.Write(page, [1]);
                journal.Write(page, [2]);
                journal.Write(view, [3]);
                Assert.Throws<System.ComponentModel.Win32Exception>(() => journal.CommitPrepared());
                Assert.Equal((byte)2, *(byte*)page);
                Assert.Equal((byte)0, *(byte*)view);
                Assert.Equal(2, EntryCount(journal));
                Assert.Throws<InvalidOperationException>(() => journal.CommitPrepared());
                Assert.Throws<InvalidOperationException>(() => journal.Write(page, [4]));
                Assert.True(VirtualFree(page, 4096, 0x4000));
                Assert.Throws<System.ComponentModel.Win32Exception>(journal.RestoreAll);
                Assert.Equal(2, EntryCount(journal));
                Assert.Throws<InvalidOperationException>(() => journal.CommitPrepared());
                Assert.Equal(page, VirtualAlloc(page, 4096, 0x1000, 0x04));
                journal.RestoreAll();
                Assert.Equal((byte)9, *(byte*)page);
                Assert.Equal(0, EntryCount(journal));
            }
            finally
            {
                Assert.True(UnmapViewOfFile(view));
                Assert.True(CloseHandle(mapping));
            }
        });
    }

    [Fact]
    public void UnreadablePreparedRangeRejectsBeforeAnyWrite()
    {
        WithPage(page => WithPage(other =>
        {
            *(byte*)page = 9;
            var journal = new PatchJournal();
            journal.BeginPreparation();
            journal.Write(page, [1]);
            journal.Write(other, [2]);
            Assert.True(VirtualFree(other, 4096, 0x4000));
            Assert.Throws<System.ComponentModel.Win32Exception>(() => journal.CommitPrepared());
            Assert.Equal((byte)9, *(byte*)page);
            Assert.Equal(0, EntryCount(journal));
            journal.RestoreAll();
        }));
    }

    [Fact]
    public void PreparationCannotHideExistingUndoOrRestartPendingWork()
    {
        WithPage(page =>
        {
            *(byte*)page = 9;
            var journal = new PatchJournal();
            journal.Write(page, [1]);
            Assert.Throws<InvalidOperationException>(() => journal.BeginPreparation());
            journal.RestoreAll();
            journal.BeginPreparation();
            journal.Write(page, [2]);
            Assert.Throws<InvalidOperationException>(() => journal.BeginPreparation());
            journal.CommitPrepared();
            Assert.Equal((byte)2, *(byte*)page);
            journal.RestoreAll();
            Assert.Equal((byte)9, *(byte*)page);
        });
    }

    private static void WithPage(Action<nint> test)
    {
        nint page = VirtualAlloc(0, 4096, 0x3000, 0x04);
        Assert.NotEqual(0, page);
        try { test(page); }
        finally { Assert.True(VirtualFree(page, 0, 0x8000)); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMappingW(nint file, nint attributes, uint protection, uint maximumSizeHigh, uint maximumSizeLow, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(nint address);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

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
