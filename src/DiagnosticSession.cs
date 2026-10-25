namespace Fahrenheit.Mods.Vrr;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>A bounded JSONL session. Producers never wait for disk or queue space.
/// A missing session_end means shutdown did not finish; loss counters are explicit.</summary>
internal sealed class DiagnosticSession : IDisposable
{
    private readonly BlockingCollection<object> _queue;
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly long _maxBytes;
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly Task _worker;
    private long _sequence, _dropped, _written, _bytes;
    private int _budgetReached;
    private bool _closed;
    private string? _failure;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal string Path { get; }
    internal string? Failure => Volatile.Read(ref _failure);
    internal bool Saturated => Volatile.Read(ref _budgetReached) != 0;
    internal bool IsCapturing => !Volatile.Read(ref _closed) && !Saturated && Failure is null;

    internal DiagnosticSession(string path, long maxBytes, int capacity, object metadata)
    {
        if (maxBytes < 4096) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _maxBytes = maxBytes;
        _queue = new BlockingCollection<object>(capacity);
        Path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        TryWrite("session_start", 0, new { schema_version = 1, utc = DateTimeOffset.UtcNow, stopwatch_frequency = Stopwatch.Frequency,
            stopwatch_origin = _origin, max_bytes = maxBytes, queue_capacity = capacity, metadata });
        _worker = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal bool TryWrite(string kind, long frame, object? data = null)
    {
        lock (_gate)
        {
            if (_closed || Failure is not null) return false;
            if (Volatile.Read(ref _budgetReached) != 0) { Interlocked.Increment(ref _dropped); return false; }
            var entry = new { sequence = ++_sequence, elapsed_ticks = Stopwatch.GetTimestamp() - _origin,
                thread = Environment.CurrentManagedThreadId, kind, frame, data };
            if (_queue.TryAdd(entry)) return true;
            Interlocked.Increment(ref _dropped);
            return false;
        }
    }

    private void WriteLoop()
    {
        long flushed = Stopwatch.GetTimestamp();
        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out object? entry, 1000))
                {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry, Json);
                    // Keep space for a valid terminal record even when capture reaches its cap.
                    if (_bytes + bytes.Length + 1 <= _maxBytes - 1024)
                    {
                        Write(bytes);
                        _written++;
                    }
                    else { Volatile.Write(ref _budgetReached, 1); Interlocked.Increment(ref _dropped); }
                }
                if (Stopwatch.GetElapsedTime(flushed).TotalSeconds >= 1)
                {
                    _stream.Flush();
                    flushed = Stopwatch.GetTimestamp();
                }
            }
            Write(JsonSerializer.SerializeToUtf8Bytes(new { kind = "session_end", written = _written,
                dropped = Interlocked.Read(ref _dropped), budget_reached = Volatile.Read(ref _budgetReached) != 0,
                elapsed_ticks = Stopwatch.GetTimestamp() - _origin }, Json));
            _stream.Flush();
        }
        catch (Exception e) { Volatile.Write(ref _failure, $"{e.GetType().Name}: {e.Message}"); }
        finally { _stream.Dispose(); }
    }

    private void Write(byte[] bytes)
    {
        _stream.Write(bytes);
        _stream.WriteByte((byte)'\n');
        _bytes += bytes.Length + 1;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _queue.CompleteAdding();
        }
        // ProcessExit must not wait indefinitely for a stalled drive.
        _worker.Wait(TimeSpan.FromSeconds(2));
    }
}
