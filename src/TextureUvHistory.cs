namespace Fahrenheit.Mods.Vrr;

internal readonly record struct TextureUvTarget(int Slot, int Record, int Set, uint Descriptor, uint Work, uint Renderer, short Height);

internal sealed class TextureUvHistory
{
    private sealed class Sample(long serial, float u, float v, double phase)
    {
        internal long Serial = serial;
        internal float U = u, V = v, PreviousV = v;
        internal double Phase = phase, PreviousPhase = phase;
    }
    private readonly Dictionary<TextureUvTarget, Sample> _samples = [];
    private readonly Dictionary<(int, int, uint, uint, uint, short), (long Serial, double Phase)> _phases = [];
    private long _image = -1;

    internal void Capture(TextureUvTarget target, long simulation, float u, float v, float delta)
    {
        if (!float.IsFinite(u) || !float.IsFinite(v) || !float.IsFinite(delta) || v < 0 || v > 1)
        {
            _samples.Remove(target);
            return;
        }
        var key = (target.Slot, target.Record, target.Descriptor, target.Work, target.Renderer, target.Height);
        double phase = v;
        if (_phases.TryGetValue(key, out var previous) && simulation >= previous.Serial && simulation <= previous.Serial + 1)
            phase += Math.Round(previous.Phase + delta - v);
        _phases[key] = (simulation, phase);
        if (!_samples.TryGetValue(target, out var sample))
        {
            _samples[target] = new(simulation, u, v, phase);
            return;
        }
        if (sample.Serial != simulation)
        {
            sample.PreviousV = sample.Serial + 1 == simulation ? sample.V : v;
            sample.PreviousPhase = sample.Serial + 1 == simulation ? sample.Phase : phase;
        }
        sample.Serial = simulation;
        sample.U = u;
        sample.V = v;
        sample.Phase = phase;
    }

    internal void Invalidate(int slot)
    {
        foreach (var key in _samples.Keys.Where(key => key.Slot == slot).ToArray()) _samples.Remove(key);
        foreach (var key in _phases.Keys.Where(key => key.Item1 == slot).ToArray()) _phases.Remove(key);
    }

    internal void Reset()
    {
        _samples.Clear();
        _phases.Clear();
        _image = -1;
    }

    internal void Present(long image, long simulation, float alpha, Func<TextureUvTarget, bool> valid,
        Action<TextureUvTarget, float, float> output)
    {
        if (_image == image) return;
        _image = image;
        foreach (var pair in _samples.ToArray())
        {
            if (!valid(pair.Key))
            {
                _samples.Remove(pair.Key);
                var target = pair.Key;
                var key = (target.Slot, target.Record, target.Descriptor, target.Work, target.Renderer, target.Height);
                if (!_samples.Keys.Any(other => (other.Slot, other.Record, other.Descriptor, other.Work, other.Renderer, other.Height) == key))
                    _phases.Remove(key);
                continue;
            }
            var sample = pair.Value;
            float v = sample.V;
            if (sample.Serial == simulation && alpha < 1)
            {
                double phase = sample.PreviousPhase + (sample.Phase - sample.PreviousPhase) * alpha;
                v = alpha <= 0 ? sample.PreviousV : (float)(phase - Math.Ceiling(phase) + 1);
            }
            output(pair.Key, sample.U, v);
        }
    }
}
