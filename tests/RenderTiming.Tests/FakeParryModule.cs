namespace Fahrenheit.Mods.Parry;

public sealed class ParryModule
{
    public bool Initialized, BindAccepted = true, ThrowReady;
    public int Calls;
    public long Epoch, Tick;
    public float Delta;
    public Action? DuringTick;
    private Func<float, bool>? _defer;
    public bool TryBindVrrSimulation(int version, object owner, Func<float, bool> defer,
        out Func<bool>? ready, out Action<long, long, float>? tick)
    {
        _defer = defer;
        ready = () => ThrowReady ? throw new InvalidOperationException("ready") : Initialized;
        tick = (epoch, serial, delta) => { Calls++; Epoch = epoch; Tick = serial; Delta = delta; DuringTick?.Invoke(); };
        return BindAccepted && version == 1;
    }
    public bool PreUpdate(float delta) => _defer!(delta);
}
