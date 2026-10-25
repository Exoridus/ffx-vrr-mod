namespace Fahrenheit.Mods.Vrr;

internal sealed class ParticleSpriteClock(int objectLimit = 16384, int slotLimit = 256)
{
    private sealed class ObjectClock(nint manager)
    {
        internal readonly nint Manager = manager;
        internal readonly Dictionary<(nint Program, int Offset), int> Carry = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<nint, ObjectClock> _objects = [];

    internal bool Created(nint manager, nint obj)
    {
        if (obj == 0) return true;
        lock (_gate)
        {
            _objects.Remove(obj);
            if (_objects.Count >= objectLimit) return false;
            _objects.Add(obj, new(manager));
            return true;
        }
    }

    internal void Deleted(nint obj) { lock (_gate) _objects.Remove(obj); }
    internal void LoopReset(nint obj)
    {
        lock (_gate)
            if (_objects.TryGetValue(obj, out var clock)) clock.Carry.Clear();
    }

    internal void ResetManager(nint manager)
    {
        lock (_gate)
            foreach (nint obj in _objects.Where(pair => pair.Value.Manager == manager).Select(pair => pair.Key).ToArray())
                _objects.Remove(obj);
    }

    internal bool TryScale(nint obj, nint program, int offset, short authored, int simulationHz, out short applied)
    {
        applied = authored;
        int quarters = simulationHz switch { 30 => 4, 60 => 2, 120 => 1, _ => 0 };
        if (quarters == 0) return false;
        lock (_gate)
        {
            int units = authored * quarters;
            if (!_objects.TryGetValue(obj, out var clock))
            {
                if (units % 4 != 0) return false;
                applied = (short)(units / 4);
                return true;
            }
            var key = (program, offset);
            bool known = clock.Carry.TryGetValue(key, out int carry);
            if (!known && units % 4 != 0 && clock.Carry.Count >= slotLimit) return false;
            units += carry;
            applied = (short)(units / 4);
            int remainder = units % 4;
            if (remainder != 0) clock.Carry[key] = remainder;
            else clock.Carry.Remove(key);
            return true;
        }
    }
}
