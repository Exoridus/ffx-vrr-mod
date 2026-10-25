namespace Fahrenheit.Mods.Vrr;

internal readonly record struct TextureBlendProbeSource(nint Model, nint Animation, uint Frame,
    string Name, nint Texture, uint Width, uint Height);

internal sealed class TextureBlendProbeState
{
    private readonly Dictionary<nint, TextureBlendProbeSource> _sources = [];
    private readonly HashSet<(nint Resource, int Stage, uint Slot)> _bindings = [];
    private int _draws;
    internal static bool IsTarget(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name.Replace('\\', '/'));
        return string.Equals(stem, "w001", StringComparison.OrdinalIgnoreCase);
    }
    internal void Reset()
    {
        _sources.Clear();
        _bindings.Clear();
        _draws = 0;
    }
    internal bool Active => _draws < 4096 && _bindings.Count < 64;
    internal bool BeginDraw() => Active && _sources.Count > 0 && ++_draws <= 4096;
    internal bool Track(nint resource, TextureBlendProbeSource source)
    {
        if (!Active || resource == 0 || _sources.Count >= 32 || _sources.ContainsKey(resource)) return false;
        _sources.Add(resource, source);
        return true;
    }
    internal bool Record(nint resource, int stage, uint slot, out TextureBlendProbeSource source)
    {
        source = default;
        return _bindings.Count < 64 && _sources.TryGetValue(resource, out source)
            && _bindings.Add((resource, stage, slot));
    }
}
