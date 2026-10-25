using System.Buffers.Binary;

namespace Fahrenheit.Mods.Vrr;

internal enum ParticlePresentationKernel
{
    MatrixXyz = 0x32d390,
    DrawMatrix = 0x334b70,
    StandardMatrix = 0x334ef0,
    DepthOffset = 0x335160,
    SimpleGeometry = 0x3394c0,
    BlendedGeometry = 0x339d30,
    GeometryBlur = 0x32e790,
}

internal readonly record struct ParticlePresentationCall(
    nint Object, long Generation, nint Data, nint Descriptor, int ProgramIndex,
    ParticlePresentationKernel Kernel);

internal sealed class ParticlePresentationCapture(int maxCalls = 4096)
{
    private readonly int _maxCalls = maxCalls > 0 ? maxCalls : throw new ArgumentOutOfRangeException(nameof(maxCalls));
    private readonly Dictionary<nint, long> _generations = new();
    private readonly List<ParticlePresentationCall> _calls = new();
    private long _nextGeneration;
    private nint _manager;
    private long _overlayEpoch;
    private int _thread;
    private bool _running, _complete, _rejected;

    internal IReadOnlyList<ParticlePresentationCall> Calls => _calls;

    internal void BeginRealRun(nint manager, long overlayEpoch, int thread)
    {
        if (_running) throw new InvalidOperationException("Particle capture cannot nest real runs.");
        if (manager == 0) throw new ArgumentOutOfRangeException(nameof(manager));
        if (_manager != manager || _overlayEpoch != overlayEpoch) _generations.Clear();
        _manager = manager;
        _overlayEpoch = overlayEpoch;
        _thread = thread;
        _calls.Clear();
        _running = true;
        _complete = false;
        _rejected = false;
    }

    internal void Record(nint obj, nint data, nint descriptor, int programIndex, ParticlePresentationKernel kernel)
    {
        if (!_running) throw new InvalidOperationException("Particle callbacks require an active real run.");
        if (obj == 0 || data == 0 || descriptor == 0 || programIndex < 0)
            throw new ArgumentException("Particle callback identity is incomplete.");
        if (!Enum.IsDefined(kernel)) throw new ArgumentOutOfRangeException(nameof(kernel));
        if (!_generations.TryGetValue(obj, out long generation))
        {
            generation = ++_nextGeneration;
            _generations.Add(obj, generation);
        }
        if (_calls.Count >= _maxCalls)
        {
            _rejected = true;
            throw new InvalidOperationException("Particle callback capture exceeded its qualified bound.");
        }
        _calls.Add(new(obj, generation, data, descriptor, programIndex, kernel));
    }

    internal void Created(nint obj)
    {
        if (obj == 0) return;
        _calls.RemoveAll(call => call.Object == obj);
        _generations[obj] = ++_nextGeneration;
    }

    internal void Deleted(nint obj)
    {
        _calls.RemoveAll(call => call.Object == obj);
        _generations.Remove(obj);
    }

    internal void LoopReset(nint obj)
    {
        // Constructors can replace private visual work as well as resetting the timeline cursor.
        _rejected = true;
    }

    internal void CompleteRealRun()
    {
        if (!_running) throw new InvalidOperationException("No particle real run is active.");
        _running = false;
        _complete = !_rejected;
    }

    internal bool CanPresent(nint manager, long overlayEpoch, int thread)
        => !_running && _complete && !_rejected && _calls.Count != 0
        && _manager == manager && _overlayEpoch == overlayEpoch && _thread == thread;

    internal bool StillOwns(nint obj, long generation)
        => _generations.TryGetValue(obj, out long current) && current == generation;

    internal void Invalidate()
    {
        _calls.Clear();
        _generations.Clear();
        _running = _complete = false;
    }
}

internal static class ParticlePresentationOwnerScope
{
    internal static bool Run(Span<byte> owner, uint encodedProgram, Func<bool> stillOwned, Action producer)
    {
        if (owner.Length < 0xb3) throw new ArgumentException("Particle owner is truncated.", nameof(owner));
        if (encodedProgram == 0) throw new ArgumentOutOfRangeException(nameof(encodedProgram));
        ArgumentNullException.ThrowIfNull(stillOwned);
        ArgumentNullException.ThrowIfNull(producer);
        if (!stillOwned()) return false;
        Span<byte> saved = stackalloc byte[22];
        owner[..20].CopyTo(saved);
        saved[20] = owner[0x1f];
        saved[21] = owner[0xb2];
        owner[..20].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(owner, encodedProgram);
        owner[0x1f] = 0;
        // Both motion integration and the independent fin callback must be suppressed.
        owner[0xb2] |= 6;
        bool restored = false;
        try { producer(); }
        finally
        {
            if (stillOwned())
            {
                saved[..20].CopyTo(owner);
                owner[0x1f] = saved[20];
                owner[0xb2] = saved[21];
                restored = true;
            }
        }
        return restored;
    }
}
