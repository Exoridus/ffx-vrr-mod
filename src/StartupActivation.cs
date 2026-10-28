using System;

namespace Fahrenheit.Mods.Vrr;

internal sealed class StartupActivation
{
    private enum State { Unprepared, Prepared, Applying, Armed, Refused, Stopped }

    private readonly object _gate = new();
    private volatile State _state;
    private uint _ownerThreadId;

    internal bool IsArmed => _state == State.Armed;

    internal void Prepare(uint nativeThreadId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(nativeThreadId);
        lock (_gate)
        {
            if (_state != State.Unprepared)
                throw new InvalidOperationException("Startup activation cannot be prepared again.");
            _ownerThreadId = nativeThreadId;
            _state = State.Prepared;
        }
    }

    internal bool Invoke(uint nativeThreadId, bool callerMatched, Action native, Func<bool> verify, Action apply)
    {
        bool ownsActivation = false;
        lock (_gate)
        {
            if (callerMatched && nativeThreadId == _ownerThreadId)
            {
                if (_state == State.Prepared)
                {
                    _state = State.Applying;
                    ownsActivation = true;
                }
                else if (_state == State.Applying)
                    _state = State.Refused;
            }
        }

        if (!ownsActivation)
        {
            native();
            return false;
        }

        try
        {
            native();
            lock (_gate)
                if (_state != State.Applying) return false;

            if (!verify())
            {
                Refuse();
                return false;
            }
            lock (_gate)
                if (_state != State.Applying) return false;

            apply();
            lock (_gate)
            {
                if (_state != State.Applying) return false;
                _state = State.Armed;
                return true;
            }
        }
        catch
        {
            Refuse();
            throw;
        }
    }

    internal void Stop()
    {
        lock (_gate) _state = State.Stopped;
    }

    private void Refuse()
    {
        lock (_gate)
            if (_state != State.Stopped) _state = State.Refused;
    }
}
