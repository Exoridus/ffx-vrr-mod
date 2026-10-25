namespace Fahrenheit.Mods.Vrr;

internal readonly record struct PresentationDescriptor(uint BufferCount, uint SwapEffect, uint Flags,
    uint Samples, uint Quality, uint Format, bool Windowed);

internal static class DxgiPresentationPolicy
{
    internal static bool TryCreate(PresentationDescriptor original, bool tearingSupported,
        out PresentationDescriptor descriptor, out string? reason)
    {
        descriptor = original;
        reason = !tearingSupported ? "DXGI tearing support is unavailable."
            : original.Samples != 1 || original.Quality != 0 ? "MSAA requires a separate resolve target."
            : !original.Windowed ? "Exclusive fullscreen is unsupported; select windowed or borderless mode."
            : original.Format is not (28 or 87 or 10 or 24) ? "The backbuffer format is unsupported by flip presentation."
            : original.SwapEffect != 0 || original.BufferCount != 1 || original.Flags != 2 ? "The engine swapchain descriptor differs from the validated legacy layout."
            : null;
        if (reason is not null) return false;
        descriptor = original with { BufferCount = 2, SwapEffect = 3, Flags = original.Flags | 0x800 };
        return true;
    }

    internal static (uint BufferCount, uint Flags) Resize(uint count, uint flags, uint creationFlags)
        => (count == 0 ? 0 : Math.Max(2, count), flags | creationFlags);
    internal static (uint Interval, uint Flags) Present(bool windowed, uint interval, uint flags)
        => (flags & 1) != 0 ? (interval, flags & ~0x200u) : (0, windowed ? flags | 0x200u : flags & ~0x200u);
}

internal sealed class PresentationLifecycle
{
    internal bool Active { get; private set; } = true;
    internal long Attempts { get; private set; }
    internal long Successes { get; private set; }
    internal long LastSuccessTimestamp { get; private set; }
    internal void CompleteResize(int result)
    {
        if (result < 0) Active = false;
    }
    internal void CompletePresent(int result, uint flags, long timestamp)
    {
        Attempts++;
        if (result == 0 && (flags & 1) == 0)
        {
            Successes++;
            LastSuccessTimestamp = timestamp;
        }
        if (result < 0 && result != unchecked((int)0x887A000A))
            Active = false;
    }
}

internal sealed class PresentationPacer
{
    private double _deadline;
    private double _rate;
    internal long NextDeadline(long now, long frequency, double rate)
    {
        if (rate == 0)
        {
            _deadline = 0;
            _rate = 0;
            return now;
        }
        double period = frequency / rate;
        if (_rate != rate || _deadline == 0 || now > _deadline + period)
            _deadline = now;
        else
            _deadline += period;
        _rate = rate;
        return (long)Math.Round(_deadline);
    }
}

internal sealed class PresentationBindings
{
    internal readonly nint[] Targets = new nint[8];
    internal nint Depth { get; private set; }
    internal uint Count { get; private set; }
    internal void Record(ReadOnlySpan<nint> targets, nint depth, bool overlay, bool resizing)
    {
        if (overlay || resizing) return;
        Clear();
        int count = Math.Min(Targets.Length, targets.Length);
        targets[..count].CopyTo(Targets);
        Count = (uint)count;
        Depth = depth;
    }
    internal void Clear()
    {
        Array.Clear(Targets);
        Count = 0;
        Depth = 0;
    }
}
