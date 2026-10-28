namespace Fahrenheit.Mods.Qol;

public sealed class FocusInputModule
{
    public bool Ready = true, Neutralize;
    public long Generation = 1;

    private Action<long, bool>? _before;
    private Action? _after;
    public bool TryBindVrrInputPublication(int version, object owner, Action<long, bool> before, Action after)
    {
        _before = before;
        _after = after;
        return version == 1;
    }
    public void Publish(Action native, bool neutralize = false)
    {
        _before!(Generation, neutralize);
        try { native(); }
        finally { _after!(); }
    }
    public bool TryGetVrrInputPolicy(int version, out long generation, out bool neutralize)
    {
        generation = Ready && version == 1 ? Generation : 0;
        neutralize = Ready && version == 1 && Neutralize;
        return Ready && version == 1;
    }
}
