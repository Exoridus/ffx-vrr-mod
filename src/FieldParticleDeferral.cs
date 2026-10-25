namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Loop restarts and lifetime frees that a paced field logic step would apply after its draw.
///     Native code never draws between that tail and the next logic step, so presentation-only
///     passes must still see the pre-restart, still-linked objects. Entries are applied at the
///     start of the manager's next logic step, before emission and before its programs run.
/// </summary>
internal unsafe sealed class FieldParticleDeferral(int capacity = 256)
{
    private const int ListLimit = 4096;
    private readonly List<(nint Manager, nint Obj)> _restarts = [], _frees = [];

    /// <summary>The field manager whose paced logic dispatch is running; zero outside that scope.</summary>
    internal nint Scope;

    internal int PendingRestarts => _restarts.Count;
    internal int PendingFrees => _frees.Count;
    internal long Applied, Dropped;

    internal bool TryDeferRestart(nint obj) => TryAdd(_restarts, obj);
    internal bool TryDeferFree(nint obj) => TryAdd(_frees, obj);

    private bool TryAdd(List<(nint, nint)> queue, nint obj)
    {
        if (Scope == 0 || obj == 0 || queue.Count >= capacity) return false;
        queue.Add((Scope, obj));
        return true;
    }

    /// <summary>
    ///     Applies this manager's entries in native order: restarts, then unlink-and-free from the
    ///     manager's object list at +0x30. An entry whose object is no longer on that list is dropped.
    /// </summary>
    internal void Flush(nint manager, Action<nint> restart, Action<nint> free)
    {
        if (_restarts.Count == 0 && _frees.Count == 0) return;
        foreach (nint obj in Take(_restarts, manager))
            if (FindLink(manager, obj) != 0) { restart(obj); Applied++; } else Dropped++;
        foreach (nint obj in Take(_frees, manager))
        {
            nint link = FindLink(manager, obj);
            if (link == 0) { Dropped++; continue; }
            *(uint*)link = *(uint*)obj;
            free(obj);
            Applied++;
        }
    }

    /// <summary>The manager ended, was freed or restarted natively; its objects are no longer ours to touch.</summary>
    internal void Drop(nint manager)
    {
        Dropped += Take(_restarts, manager).Count + Take(_frees, manager).Count;
    }

    internal void Forget(nint obj)
    {
        Dropped += _restarts.RemoveAll(x => x.Obj == obj) + _frees.RemoveAll(x => x.Obj == obj);
    }

    private static List<nint> Take(List<(nint Manager, nint Obj)> queue, nint manager)
    {
        var taken = new List<nint>();
        queue.RemoveAll(x => { if (x.Manager != manager) return false; taken.Add(x.Obj); return true; });
        return taken;
    }

    /// <summary>The 32-bit link cell that currently points at <paramref name="obj"/>, or zero.</summary>
    private static nint FindLink(nint manager, nint obj)
    {
        nint link = manager + 0x30;
        for (int i = 0; i < ListLimit; i++)
        {
            nint current = (nint)(nuint)(*(uint*)link);
            if (current == 0) return 0;
            if (current == obj) return link;
            link = current;
        }
        return 0;
    }
}

/// <summary>Byte-level contracts for the field particle pause port (upstream FX-010).</summary>
internal static class FieldParticlePauseCode
{
    /// <summary>Space-separated hex bytes; <c>??</c> matches any byte.</summary>
    internal static bool Matches(ReadOnlySpan<byte> code, string pattern)
    {
        string[] tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (code.Length < tokens.Length) return false;
        for (int i = 0; i < tokens.Length; i++)
            if (tokens[i] != "??" && code[i] != Convert.ToByte(tokens[i], 16)) return false;
        return true;
    }

    internal static int PatternLength(string pattern) => pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    internal static uint RelativeTarget(uint instruction, int length, int displacement) => unchecked(instruction + (uint)length + (uint)displacement);

    internal const int FreeStubLength = 44;

    /// <summary>
    ///     Replacement for <c>cmp [esi+0xC],eax; jb alive</c> in the dispatcher's free pass. A live
    ///     object still takes <paramref name="alive"/>; a dying one asks the cdecl callback stored in
    ///     the stub's last four bytes (argument: object) and stays linked when it returns non-zero.
    ///     eax, ecx and edx are preserved around the call because the native loop keeps eax live.
    /// </summary>
    internal static byte[] FreeStub(uint stub, uint alive, uint die)
    {
        var b = new List<byte>(FreeStubLength);
        void Rel(byte[] opcode, uint target)
        {
            b.AddRange(opcode);
            uint next = stub + (uint)b.Count + 4;
            b.AddRange(BitConverter.GetBytes(unchecked(target - next)));
        }
        b.AddRange([0x39, 0x46, 0x0c]);                // cmp [esi+0xC], eax
        Rel([0x0f, 0x82], alive);                       // jb alive
        b.AddRange([0x50, 0x51, 0x52, 0x56]);           // push eax, ecx, edx, esi
        b.AddRange([0xff, 0x15]);                       // call [cell]
        b.AddRange(BitConverter.GetBytes(stub + FreeStubLength - 4));
        b.AddRange([0x83, 0xc4, 0x04, 0x85, 0xc0]);     // add esp,4; test eax,eax
        b.AddRange([0x5a, 0x59, 0x58]);                 // pop edx, ecx, eax (flags kept)
        Rel([0x0f, 0x85], alive);                       // jnz alive (deferred)
        Rel([0xe9], die);                               // jmp die
        while (b.Count < FreeStubLength - 4) b.Add(0xcc);
        b.AddRange([0, 0, 0, 0]);                       // callback cell
        return [.. b];
    }
}
