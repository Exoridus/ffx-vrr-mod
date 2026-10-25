namespace Fahrenheit.Mods.Vrr;

internal enum FieldParticleProducer { None, Matrix, Shape, ShapeX, ShapeField }
internal readonly record struct FieldParticleKernel(nint Update, nint Draw, nint Emitter,
    FieldParticleProducer Producer, nint Target, int OperandBytes);
internal readonly record struct FieldParticleDraw(nint Object, int ObjectBytes, nint Entry,
    nint Data, nint Descriptor, int Index, FieldParticleKernel Kernel, nint Private);

internal unsafe sealed class FieldParticlePresentationPlan(nint manager, long generation,
    int thread, IReadOnlyList<FieldParticleDraw> calls)
{
    internal nint Manager { get; } = manager;
    internal long Generation { get; } = generation;
    internal int Thread { get; } = thread;
    internal IReadOnlyList<FieldParticleDraw> Calls { get; } = calls;

    internal static bool TryBuild(nint manager, long generation,
        IReadOnlyDictionary<nint, FieldParticleKernel> kernels, out FieldParticlePresentationPlan? plan)
    {
        plan = null;
        if (!MemoryProbe.IsReadable(manager, 0x80)) return false;
        int groups = *(int*)(manager + 0x18);
        nint entries = Read(manager + 0x3c);
        if (groups is < 0 or > 256 || (groups > 0 && !MemoryProbe.IsReadable(entries, groups * 16))) return false;
        var calls = new List<FieldParticleDraw>();
        var objects = new HashSet<nint>();
        for (int groupIndex = 0; groupIndex < groups; groupIndex++)
        {
            nint entry = entries + groupIndex * 16;
            int count = *(ushort*)(entry + 12);
            if (count == 0) continue;
            nint group = Read(entry);
            if (!MemoryProbe.IsReadable(group, 0x28)) return false;
            int programs = *(short*)(group + 0x26), bytes = *(int*)(group + 0x20);
            if (programs is < 1 or > 64 || bytes is < 0xa0 or > 0x10000
                || !MemoryProbe.IsReadable(group + 0x28, programs * 16)) return false;
            int objectBytes = bytes + programs * 4;
            var live = new List<nint>();
            nint obj = Read(entry + 4);
            for (int i = 0; i < count; i++)
            {
                if (objects.Count >= 1024 || !objects.Add(obj)
                    || !MemoryProbe.IsReadable(obj, objectBytes) || Read(obj + 8) != entry) return false;
                live.Add(obj);
                obj = Read(obj);
            }
            if (obj != 0) return false;
            for (int index = 0; index < programs; index++)
            {
                nint descriptor = group + 0x28 + index * 16, kernel = Read(descriptor);
                if (!kernels.TryGetValue(kernel, out var policy) || !MemoryProbe.IsReadable(kernel, 0x28)
                    || Read(kernel + 4) != policy.Emitter || Read(kernel + 8) != policy.Update
                    || Read(kernel + 12) != policy.Draw || Read(kernel + 0x14) != 0 || Read(kernel + 0x18) != 0) return false;
                if (policy.Producer == FieldParticleProducer.None || (*(byte*)(group + 12) & 1) != 0) continue;
                foreach (nint current in live)
                {
                    nint data = Read(current + bytes + index * 4);
                    if (*(ushort*)(descriptor + 4) < policy.OperandBytes
                        || !MemoryProbe.IsReadable(data, policy.OperandBytes)) return false;
                    nint privateWork = 0;
                    if (policy.Producer == FieldParticleProducer.ShapeField)
                    {
                        nint offsets = Read(descriptor + 12);
                        if (!MemoryProbe.IsReadable(offsets, 12)) return false;
                        int offset = *(int*)offsets;
                        int positionOffset = *(int*)(offsets + 8);
                        if (positionOffset < 0 || positionOffset > bytes - 0xa0 - 12) return false;
                        if (offset < 0 || offset > bytes - 0xa0 - 0x5c) return false;
                        privateWork = current + 0xa0 + offset;
                        if (!ValidChildren(data, privateWork)) return false;
                    }
                    if (calls.Count >= 4096) return false;
                    calls.Add(new(current, objectBytes, entry, data, descriptor, index, policy, privateWork));
                }
            }
        }
        plan = new(manager, generation, Environment.CurrentManagedThreadId, calls);
        return true;
    }

    internal bool Matches(long generation, IReadOnlyDictionary<nint, FieldParticleKernel> kernels)
    {
        if (generation != Generation || !TryBuild(Manager, generation, kernels, out var fresh)) return false;
        return Calls.SequenceEqual(fresh!.Calls);
    }

    internal void Replay(Action<FieldParticleDraw> draw, Func<bool> stillOwns)
    {
        if (Environment.CurrentManagedThreadId != Thread) throw new InvalidOperationException("Field particle producer thread changed.");
        var saved = new Dictionary<nint, byte[]>();
        foreach (var call in Calls)
            if (!saved.ContainsKey(call.Object)) saved.Add(call.Object, new ReadOnlySpan<byte>((void*)call.Object, call.ObjectBytes).ToArray());
        try { foreach (var call in Calls) draw(call); }
        finally
        {
            if (!stillOwns()) throw new InvalidOperationException("Field particle lifetime changed inside a producer pass.");
            foreach (var pair in saved) pair.Value.CopyTo(new Span<byte>((void*)pair.Key, pair.Value.Length));
        }
    }

    private static bool ValidChildren(nint data, nint work)
    {
        if (*(uint*)(data + 4) == 0xffff) return true;
        int children = *(int*)(data + 8), histories = *(int*)(work + 0x54);
        nint child = Read(work + 0x40), history = Read(work + 0x50);
        if (children is < 1 or > 512 || histories is < 1 or > 32768
            || !MemoryProbe.IsReadable(Read(work + 0x44), 12)
            || !MemoryProbe.IsReadable(child, children * 0xa0) || !MemoryProbe.IsReadable(history, histories * 8)) return false;
        for (int i = 0; i < children; i++)
        {
            nint at = child + i * 0xa0;
            if ((*(byte*)at & 1) != 0 && (*(int*)(at + 8) < 0 || *(int*)(at + 8) >= histories)) return false;
        }
        return true;
    }

    private static nint Read(nint address) => (nint)(nuint)(*(uint*)address);
}
