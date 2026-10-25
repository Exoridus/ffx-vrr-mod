namespace Fahrenheit.Mods.Vrr;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Bit-preserving records in independent gzip blocks. Producers never wait for disk or queue space.</summary>
internal sealed class BinaryTraceSession : IDisposable
{
    [InlineArray(128)]
    private struct Words { private uint _first; }
    private struct Entry
    {
        internal long Sequence, Timestamp, Frame, Loop;
        internal int Thread;
        internal ushort Channel, Count;
        internal Words Data;
    }

    private readonly BlockingCollection<Entry> _queue;
    private readonly object _gate = new();
    private readonly BinaryWriter _output;
    private readonly Task _worker;
    private readonly long _maxBytes;
    private long _sequence, _dropped, _written;
    private bool _closed;
    private int _saturated;
    private string? _failure;

    internal long Dropped => Interlocked.Read(ref _dropped);
    internal string? Failure => Volatile.Read(ref _failure);
    internal bool IsCapturing => !Volatile.Read(ref _closed) && Volatile.Read(ref _saturated) == 0 && Failure is null;

    internal BinaryTraceSession(string path, long maxBytes, int capacity, string metadata)
    {
        byte[] header = Encoding.UTF8.GetBytes(metadata);
        if (maxBytes < 4096 || header.Length > Math.Min(65536, maxBytes - 1024))
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _queue = new BlockingCollection<Entry>(capacity);
        _maxBytes = maxBytes;
        _output = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _output.Write(Encoding.ASCII.GetBytes("FFXTRACE"));
        _output.Write(1);
        _output.Write(header.Length);
        _output.Write(header);
        _output.Flush();
        _worker = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal bool TryWrite(ushort channel, long frame, long loop, ReadOnlySpan<uint> data)
    {
        if (data.Length > 128) throw new ArgumentOutOfRangeException(nameof(data));
        lock (_gate)
        {
            if (!IsCapturing) { Interlocked.Increment(ref _dropped); return false; }
            Entry entry = new() { Sequence = ++_sequence, Timestamp = Stopwatch.GetTimestamp(), Frame = frame, Loop = loop,
                Thread = Environment.CurrentManagedThreadId, Channel = channel, Count = (ushort)data.Length };
            data.CopyTo(entry.Data);
            if (_queue.TryAdd(entry)) return true;
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    private void WriteLoop()
    {
        using var block = new MemoryStream(65536);
        using var records = new BinaryWriter(block, Encoding.UTF8, leaveOpen: true);
        int count = 0;
        long flushed = Stopwatch.GetTimestamp();
        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out Entry entry, 100))
                {
                    if (Volatile.Read(ref _saturated) != 0) { Interlocked.Increment(ref _dropped); continue; }
                    records.Write(entry.Sequence); records.Write(entry.Timestamp); records.Write(entry.Frame); records.Write(entry.Loop);
                    records.Write(entry.Thread); records.Write(entry.Channel); records.Write(entry.Count);
                    for (int i = 0; i < entry.Count; i++) records.Write(entry.Data[i]);
                    count++;
                }
                if (count >= 64 || count > 0 && Stopwatch.GetElapsedTime(flushed).TotalSeconds >= 1)
                {
                    FlushBlock(block, count); count = 0; block.SetLength(0); block.Position = 0;
                    flushed = Stopwatch.GetTimestamp();
                }
            }
            if (count > 0) FlushBlock(block, count);
            _output.Write(0); _output.Write(0);
            _output.Write(_written); _output.Write(Dropped);
            _output.Flush();
        }
        catch (Exception e) { Volatile.Write(ref _failure, $"{e.GetType().Name}: {e.Message}"); }
        finally { _output.Dispose(); }
    }

    private void FlushBlock(MemoryStream block, int count)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(block.GetBuffer().AsSpan(0, (int)block.Length));
        // Independent blocks preserve completed seconds if a crash interrupts the next block.
        if (_output.BaseStream.Position + 8 + compressed.Length + 24 > _maxBytes)
        {
            Volatile.Write(ref _saturated, 1);
            Interlocked.Add(ref _dropped, count);
            return;
        }
        _output.Write((int)compressed.Length); _output.Write((int)block.Length);
        _output.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
        _output.Flush();
        _written += count;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_closed) { _closed = true; _queue.CompleteAdding(); }
        }
        _worker.Wait(TimeSpan.FromSeconds(2));
    }
}
