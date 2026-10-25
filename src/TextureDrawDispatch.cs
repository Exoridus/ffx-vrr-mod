namespace Fahrenheit.Mods.Vrr;

internal delegate void TextureDrawNext(nint context, uint count, uint start, int baseVertex);

internal sealed class TextureDrawDispatch(bool indexed, Func<TextureDrawNext> resolve,
    Func<bool> qualified, Func<nint, nint> prepare, Action<nint, nint> restore,
    Action<nint, bool, uint> probe)
{
    private readonly Lazy<TextureDrawNext> _next = new(resolve);
    internal void Draw(nint context, uint count, uint start, int baseVertex)
    {
        TextureDrawNext next = _next.Value;
        probe(context, indexed, count);
        nint native = qualified() ? prepare(context) : 0;
        try { next(context, count, start, baseVertex); }
        finally { if (native != 0) restore(context, native); }
    }
}

internal sealed class TextureDrawHookRegistry<T> where T : class
{
    private readonly Dictionary<(bool, nint), T> _hooks = [];
    internal bool Install(bool indexed, nint target, Func<T> create, Func<T, bool> install)
    {
        if (_hooks.ContainsKey((indexed, target))) return true;
        T hook = create();
        if (!install(hook)) return false;
        _hooks.Add((indexed, target), hook);
        return true;
    }
}
