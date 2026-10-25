namespace Fahrenheit.Mods.Vrr;

internal readonly record struct TextureFrameBlend(uint First, uint Next, float Alpha);

internal sealed class TextureFramePhase
{
    private bool _hasSample;
    private long _serial;
    private int _frame, _counter, _period;
    private double _previous, _current;

    internal bool Capture(long serial, int frame, int counter, int period, int step, double fraction)
    {
        if ((uint)frame >= 8 || counter < 0 || period <= 0 || counter >= period || step is < 0 or > 1
            || !double.IsFinite(fraction) || fraction < 0 || fraction >= 1) return false;
        double position = frame + (counter + (step == 0 ? 0 : fraction)) / period;
        bool adjacent = _hasSample && serial >= _serial && serial <= _serial + 1
            && (frame == _frame || frame == (_frame + 1) % 8) && (frame != _frame || counter >= _counter);
        if (adjacent)
        {
            if (step == 0 && frame == _frame && counter == _counter && period == _period) position = _current;
            else position += Math.Round((_current - position) / 8) * 8;
            if (serial != _serial) _previous = _current;
        }
        else _previous = position;
        _current = position;
        _hasSample = true;
        _serial = serial;
        _frame = frame;
        _counter = counter;
        _period = period;
        return true;
    }

    internal TextureFrameBlend Render(float alpha, bool interpolate)
    {
        double position = interpolate ? _previous + (_current - _previous) * Math.Clamp(alpha, 0, 1) : _current;
        double whole = Math.Floor(position);
        uint first = (uint)((long)whole % 8);
        return new(first, (first + 1) % 8, (float)(position - whole));
    }
}