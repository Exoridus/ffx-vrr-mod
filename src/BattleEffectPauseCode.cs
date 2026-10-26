namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Byte-level contracts and generated code for the battle effect pause (port of the upstream
///     FFX-60fps MAG rows). Every builder returns position-dependent x86 for the address it is given;
///     nothing here touches process memory.
/// </summary>
internal static class BattleEffectPauseCode
{
    internal static readonly byte[] Jmp = [0xe9], Je = [0x0f, 0x84], Jne = [0x0f, 0x85], Jb = [0x0f, 0x82],
        Jae = [0x0f, 0x83], Jl = [0x0f, 0x8c];

    internal sealed class Emitter(uint origin)
    {
        private readonly List<byte> _bytes = [];
        internal uint Here => unchecked(origin + (uint)_bytes.Count);
        internal int Count => _bytes.Count;
        internal Emitter Raw(params byte[] bytes) { _bytes.AddRange(bytes); return this; }
        internal Emitter Raw(ReadOnlySpan<byte> bytes) { foreach (byte b in bytes) _bytes.Add(b); return this; }
        internal Emitter U32(uint value) { _bytes.AddRange(BitConverter.GetBytes(value)); return this; }
        internal Emitter Jump(byte[] opcode, uint target) { Raw(opcode); return U32(unchecked(target - (Here + 4))); }
        internal int Forward(byte[] opcode) { Raw(opcode); int at = _bytes.Count; U32(0); return at; }
        internal void Bind(int at)
        {
            uint rel = unchecked(Here - (origin + (uint)at + 4));
            for (int i = 0; i < 4; i++) _bytes[at + i] = (byte)(rel >> (i * 8));
        }
        internal Emitter CmpByte(uint address, byte value) => Raw(0x80, 0x3d).U32(address).Raw(value);
        internal Emitter CallCell(uint cell) => Raw(0xff, 0x15).U32(cell);
        internal Emitter StoreAl(uint address) => Raw(0xa2).U32(address);
        internal byte[] ToArray() => [.. _bytes];
    }

    /// <summary><c>jmp target</c> padded with nops to <paramref name="length"/> bytes.</summary>
    internal static byte[] JumpPatch(uint site, uint target, int length)
    {
        if (length < 5) throw new ArgumentOutOfRangeException(nameof(length));
        byte[] patch = new byte[length];
        patch[0] = 0xe9;
        BitConverter.GetBytes(unchecked(target - (site + 5))).CopyTo(patch, 1);
        for (int i = 5; i < length; i++) patch[i] = 0x90;
        return patch;
    }

    /// <summary>Space-separated hex bytes; <c>??</c> matches any byte.</summary>
    internal static (byte Value, bool Any)[] Parse(string pattern)
        => [.. pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t == "??" ? ((byte)0, true) : (Convert.ToByte(t, 16), false))];

    internal static bool MatchesAt(ReadOnlySpan<byte> image, int offset, (byte Value, bool Any)[] pattern)
    {
        if (offset < 0 || offset + pattern.Length > image.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (!pattern[i].Any && image[offset + i] != pattern[i].Value) return false;
        return true;
    }

    internal static bool MatchesAt(ReadOnlySpan<byte> image, int offset, string pattern) => MatchesAt(image, offset, Parse(pattern));

    /// <summary>Number of matches of <paramref name="pattern"/> that also pass <paramref name="accept"/>; the first one in <paramref name="first"/>.</summary>
    internal static int Count(ReadOnlySpan<byte> image, string pattern, out int first, Func<int, bool>? accept = null)
    {
        var parsed = Parse(pattern);
        int anchor = Array.FindIndex(parsed, p => !p.Any);
        first = -1;
        if (anchor < 0) return 0;
        int count = 0;
        for (int at = anchor; at < image.Length;)
        {
            int found = image[at..].IndexOf(parsed[anchor].Value);
            if (found < 0) break;
            int candidate = at + found - anchor;
            if (MatchesAt(image, candidate, parsed) && (accept is null || accept(candidate)))
            {
                if (count++ == 0) first = candidate;
            }
            at += found + 1;
        }
        return count;
    }

    internal static uint U32(ReadOnlySpan<byte> image, int offset) => BitConverter.ToUInt32(image.Slice(offset, 4));

    // ---- MAG-025: OPU unit bytecode walker (OpuMain_UnitBytecodeWalk) mid-function stubs ----

    internal readonly record struct OpuSites(uint Next, uint SentryReturn, uint HandlerReturn, uint NextReturn,
        uint GateReturn, uint FollowReturn, uint FollowTable, uint FollowAttach, uint FreshWaitReturn);
    internal readonly record struct OpuData(uint Hold, uint Skip, uint FollowSkip, uint EntryCell, uint NextCell, uint FollowCell);
    internal readonly record struct OpuOffsets(int Sentry, int Handler, int Dec, int Next, int Gate, int Follow, int PcCommit, int FreshWait);

    internal static bool OpuHandlerTableMatches(ReadOnlySpan<byte> table, IReadOnlyList<(ushort Opcode, uint Target)> admitted)
    {
        foreach (var (opcode, target) in admitted)
        {
            int offset = opcode * 4;
            if (offset > table.Length - 4 || U32(table, offset) != target) return false;
        }
        return true;
    }

    /// <summary>
    ///     The walker stubs (upstream MAG-025 mode 3 plus FOLLOWGUARD). The hold byte is 0 on every
    ///     walk that may step, 3 on a held presentation walk: WAIT countdowns keep their count, the
    ///     integration gate reads "paused", rejected opcodes yield at their PC with WAIT 0, and a WAIT-0
    ///     stream runs only when the entry callback allows it. Entry and next callbacks run on every
    ///     walk; they are cdecl (scratch, record) and the entry one returns non-zero to skip the stream.
    ///     Held mode 3 admits only supplied masked opcode and expected handler-entry pairs after existing denies.
    ///     WAIT operands above one yield; history append advances four bytes without its handler.
    ///     Vector copy advances six bytes only for no lanes or self copies without world XYZ.
    ///     Held mode 3 suppresses the shared PC and fresh WAIT commits before native continuations.
    /// </summary>
    internal static byte[] OpuStubs(uint origin, OpuSites s, OpuData d, IReadOnlyList<ushort> denied, IReadOnlyList<(ushort Opcode, uint Target)> admitted, out OpuOffsets offsets)
    {
        var e = new Emitter(origin);

        int sentry = e.Count;                                    // replaces movsx ecx, word [esi+0x216]
        e.Raw(0x60, 0x53, 0x56).CallCell(d.EntryCell).Raw(0x83, 0xc4, 0x08).StoreAl(d.Skip).Raw(0x61)
            .CmpByte(d.Skip, 0).Jump(Jne, s.Next)
            .Raw(0x0f, 0xbf, 0x8e, 0x16, 0x02, 0x00, 0x00).Jump(Jmp, s.SentryReturn);

        int handler = e.Count;                                   // replaces push edi; push esi; call eax; add esp,8
        e.CmpByte(d.Hold, 2);
        int toPass = e.Forward(Jb);
        e.Raw(0x51, 0x0f, 0xb7, 0x8e, 0x1a, 0x02, 0x00, 0x00, 0x81, 0xe1, 0xff, 0x01, 0x00, 0x00);
        var toDeny = new List<int>();
        foreach (ushort op in denied) { e.Raw(0x81, 0xf9).U32(op); toDeny.Add(e.Forward(Je)); }
        e.CmpByte(d.Hold, 3);
        var toAdmit = new List<int> { e.Forward(Jne) };
        foreach (var (op, target) in admitted)
        {
            e.Raw(0x81, 0xf9).U32(op);
            int nextCandidate = e.Forward(Jne);
            e.Raw(0x3d).U32(target);
            toDeny.Add(e.Forward(Jne));
            if (op == 0x009)
            {
                // A fresh WAIT above one writes a countdown before the held decrement guard can run.
                e.Raw(0x66, 0x81, 0xbe, 0x1a, 0x02, 0x00, 0x00, 0x00, 0x04);
                toDeny.Add(e.Forward(Jae));
            }
            if (op == 0x061)
            {
                // World XYZ self-copies use x87 and can change NaN payloads and FP status.
                e.Raw(0x66, 0xf7, 0x47, 0x02, 0x00, 0xf0);
                int noLanes = e.Forward(Je);
                e.Raw(0x66, 0x81, 0x7f, 0x04, 0x00, 0xff);
                toDeny.Add(e.Forward(Jne));
                e.Raw(0xf6, 0x47, 0x02, 0x10);
                int integerCopy = e.Forward(Je);
                e.Raw(0x66, 0xf7, 0x47, 0x02, 0x00, 0x70);
                toDeny.Add(e.Forward(Jne));
                e.Bind(noLanes);
                e.Bind(integerCopy);
                e.Raw(0x59, 0x8d, 0x47, 0x06).Jump(Jmp, s.HandlerReturn);
            }
            else if (op == 0x053)
            {
                // This handler only appends history; its other paths also continue at PC + 4.
                e.Raw(0x59, 0x8d, 0x47, 0x04).Jump(Jmp, s.HandlerReturn);
            }
            else toAdmit.Add(e.Forward(Jmp));
            e.Bind(nextCandidate);
        }
        toDeny.Add(e.Forward(Jmp));
        foreach (int at in toAdmit) e.Bind(at);
        e.Raw(0x59);
        e.Bind(toPass);
        e.Raw(0x57, 0x56, 0xff, 0xd0, 0x83, 0xc4, 0x08).Jump(Jmp, s.HandlerReturn);
        foreach (int at in toDeny) e.Bind(at);
        e.Raw(0x59, 0x8b, 0xc7, 0xc6, 0x86, 0x10, 0x02, 0x00, 0x00, 0x01).Jump(Jmp, s.HandlerReturn);

        int dec = e.Count;                                       // replaces dec al; mov [ecx+ebx+0x10], al
        e.CmpByte(d.Hold, 2).Jump(Jae, s.Next)
            .Raw(0xfe, 0xc8, 0x88, 0x44, 0x19, 0x10).Jump(Jmp, s.Next);

        int pcCommit = e.Count;
        e.CmpByte(d.Hold, 3).Jump(Je, s.Next)
            .Raw(0x89, 0x04, 0x8b).Jump(Jmp, s.Next);

        int freshWait = e.Count;
        e.CmpByte(d.Hold, 3).Jump(Je, s.FreshWaitReturn)
            .Raw(0xfe, 0xc8, 0x00, 0x44, 0x19, 0x10).Jump(Jmp, s.FreshWaitReturn);

        int next = e.Count;                                      // replaces inc word [esi+0x216]
        e.Raw(0x60, 0x53, 0x56).CallCell(d.NextCell).Raw(0x83, 0xc4, 0x08, 0x61)
            .Raw(0x66, 0xff, 0x86, 0x16, 0x02, 0x00, 0x00).Jump(Jmp, s.NextReturn);

        int gate = e.Count;                                      // replaces movsx ecx, word [esi+0x212]
        e.CmpByte(d.Hold, 0);
        int gatePass = e.Forward(Je);
        e.Raw(0xb9, 0x01, 0x00, 0x00, 0x00).Jump(Jmp, s.GateReturn);
        e.Bind(gatePass);
        e.Raw(0x0f, 0xbf, 0x8e, 0x12, 0x02, 0x00, 0x00).Jump(Jmp, s.GateReturn);

        int follow = e.Count;                                    // replaces the follow handler call
        e.Raw(0x50, 0x0f, 0xb6, 0xc0, 0x81, 0x3c, 0x85).U32(s.FollowTable).U32(s.FollowAttach).Raw(0x58);
        int normal = e.Forward(Jne);
        e.Raw(0x60, 0x56).CallCell(d.FollowCell).Raw(0x83, 0xc4, 0x04).StoreAl(d.FollowSkip).Raw(0x61)
            .CmpByte(d.FollowSkip, 0).Jump(Jne, s.FollowReturn);
        e.Bind(normal);
        e.Raw(0x0f, 0xb6, 0xc0, 0x57, 0x8b, 0x04, 0x85).U32(s.FollowTable).Raw(0x56, 0xff, 0xd0, 0x83, 0xc4, 0x08)
            .Jump(Jmp, s.FollowReturn);

        offsets = new(sentry, handler, dec, next, gate, follow, pcCommit, freshWait);
        return e.ToArray();
    }

    /// <summary>
    ///     Remembers WAIT-0 same-PC yields for held walks within one observed history epoch.
    ///     This observation does not establish handler purity or a unit allocation identity.
    /// </summary>
    internal sealed class OpuRepeatTracker
    {
        private const int Limit = 4096;
        private readonly Dictionary<nint, uint[]> _pcs = [];
        private nint _record;
        private int _stream;
        private uint _pc;
        private byte _wait;
        private bool _valid;
        private long _epoch;
        internal long Ran, Blocked;

        /// <summary>True when the stream must not run on this held walk.</summary>
        internal bool Entry(byte hold, nint record, int stream, uint pc, byte wait, long epoch)
        {
            ObserveEpoch(epoch);
            _valid = false;
            bool identityValid = record != 0 && stream is >= 0 and < 4;
            if (hold == 0)
            {
                if (!identityValid) return false;
                if (_pcs.TryGetValue(record, out var prior)) prior[stream] = 0;
                if (pc != 0) (_record, _stream, _pc, _wait, _valid) = (record, stream, pc, wait, true);
                return false;
            }
            if (identityValid && _pcs.TryGetValue(record, out var pcs) && pcs[stream] != pc) pcs[stream] = 0;
            if (wait != 0 || hold != 3) return false;
            // shortcut: same-address/same-PC reuse within an epoch is unqualified; add allocation identity when available.
            if (identityValid && pc != 0 && _pcs.TryGetValue(record, out var learned) && learned[stream] == pc)
            {
                Ran++;
                return false;
            }
            Blocked++;
            return true;
        }

        internal void Next(nint record, int stream, uint pc, long epoch)
        {
            ObserveEpoch(epoch);
            if (!_valid) return;
            _valid = false;
            if (_record != record || _stream != stream || pc == 0) return;
            if (_wait == 0 && pc == _pc)
            {
                if (!_pcs.TryGetValue(record, out var pcs))
                {
                    if (_pcs.Count >= Limit) _pcs.Clear();
                    _pcs[record] = pcs = new uint[4];
                }
                pcs[stream] = pc;
            }
        }

        private void ObserveEpoch(long epoch)
        {
            if (_epoch == epoch) return;
            Clear();
            _epoch = epoch;
        }

        internal void Clear() { _pcs.Clear(); _valid = false; }
    }

    // ---- MAG-030: OPP handler buffers ----

    /// <summary>Byte length of the op's own buffer (op 9 sub-units, op 17 trail ring); zero when the op keeps its state in the record.</summary>
    internal static int OppBufferLength(int op, ushort count) => op switch
    {
        9 => 0x20 + count * 0x50,
        17 => 0x10 + count * 0x20,
        _ => 0,
    };

    // ---- MAG-019: VFX shader setup guard ----

    /// <summary>
    ///     Prologue replacement for the thiscall VFX shader setup (ret 8). It returns 0, the native
    ///     "nothing to set up", while the sub-instance has no material name yet: a paused draw reaches
    ///     it before its materials exist and the native strstr on that name faults.
    /// </summary>
    internal static byte[] ShaderSetupStub(uint origin, uint entry, ReadOnlySpan<byte> prologue)
    {
        var e = new Emitter(origin);
        var skips = new List<int>();
        e.Raw(0x8b, 0x41, 0x24, 0x85, 0xc0); skips.Add(e.Forward(Je));
        e.Raw(0x83, 0xb8, 0x90, 0x00, 0x00, 0x00, 0x01); skips.Add(e.Forward(Jl));
        e.Raw(0x8b, 0x80, 0x94, 0x00, 0x00, 0x00, 0x85, 0xc0); skips.Add(e.Forward(Je));
        e.Raw(0x83, 0x78, 0x20, 0x00); skips.Add(e.Forward(Je));
        e.Raw(prologue).Jump(Jmp, entry + (uint)prologue.Length);
        foreach (int at in skips) e.Bind(at);
        e.Raw(0x33, 0xc0, 0xc2, 0x08, 0x00);
        return e.ToArray();
    }

    // ---- magic DLL rows ----

    internal const string PppDriverInner = "FF 86 18 01 00 00 EB ?? 48 89 86 14 01 00 00";
    internal const string MotionScript = "83 3D ?? ?? ?? ?? 01 75 06 B8 01 00 00 00 C3 66 A1 ?? ?? ?? ?? 66 85 C0 75 ?? 66 FF 05 ?? ?? ?? ?? 66 83 3D ?? ?? ?? ?? 00";
    internal const string ArcNode = "55 8B EC 51 A1 ?? ?? ?? ?? 85 C0 7E ?? 48 A3 ?? ?? ?? ?? 85 C0 0F 8F";
    internal const string PauseSetterCached = "8B 80 F4 03 00 00 FF D0 85 C0 A1 ?? ?? ?? ?? 75 11 C7 00 01 00 00 00 C7 05 ?? ?? ?? ?? 01 00 00 00 C3 C7 00 00 00 00 00 C7 05 ?? ?? ?? ?? 00 00 00 00 C3";
    internal const string PauseSetterDirectA = "8B 80 F4 03 00 00 FF D0 8B 0D ?? ?? ?? ?? FF 35 ?? ?? ?? ?? F7 D8 1B C0 40 89 01";
    internal const string PauseSetterDirectB = "8B 80 F4 03 00 00 FF D0 8B 0D ?? ?? ?? ?? F7 D8 1B C0 40 89 01";

    internal readonly record struct DriverEntry(int Entry, int PrologueLength, int HeadOperand);

    /// <summary>
    ///     The PPP record driver entry for a driver body at <paramref name="inner"/> (upstream MAG-016): the
    ///     nearest <c>(CC|C3) 55 8B EC (81|83) EC</c> before it. Variant A reads the list head from
    ///     <c>[ecx+0x30]</c> (<see cref="DriverEntry.HeadOperand"/> is -1); variant B loads it from a global
    ///     whose address operand sits at <see cref="DriverEntry.HeadOperand"/>.
    /// </summary>
    internal static DriverEntry? FindDriverEntry(ReadOnlySpan<byte> image, int inner)
    {
        for (int p = inner - 4; p > inner - 0x800 && p > 0x1000; p--)
        {
            if ((image[p - 1] != 0xcc && image[p - 1] != 0xc3) || image[p] != 0x55 || image[p + 1] != 0x8b
                || image[p + 2] != 0xec || image[p + 4] != 0xec) continue;
            int n = image[p + 3] switch { 0x81 => 9, 0x83 => 6, _ => 0 };
            if (n == 0) return null;
            for (int k = n; k < 0x20; k++)
                if (image[p + k] == 0x8b && image[p + k + 1] == 0x41 && image[p + k + 2] == 0x30) return new(p, n, -1);
            for (int k = n; k < 0x20; k++)
            {
                if (!MatchesAt(image, p + k, "33 C5 89 45 FC A1")) continue;
                uint head = U32(image, p + k + 6);
                for (int j = k + 10; j < k + 0x20; j++)
                    if (image[p + j] == 0xa3 && U32(image, p + j + 1) == unchecked(head + 8)) return new(p, n, p + k + 6);
                return null;
            }
            return null;
        }
        return null;
    }

    internal readonly record struct PauseSetter(int Offset, uint Cache, uint PointerCell);

    /// <summary>
    ///     The overlay's exec-pause setter (upstream MAG-018): the cached form writes (exec == 0) into a
    ///     DLL global and the pause flag, the direct form only into the flag. <see cref="PauseSetter.Cache"/>
    ///     is zero for the direct form. Operands must lie inside [<paramref name="low"/>, <paramref name="high"/>).
    /// </summary>
    internal static PauseSetter? FindPauseSetter(ReadOnlySpan<byte> image, uint low, uint high)
    {
        int cached = Count(image, PauseSetterCached, out int hit);
        if (cached == 1)
        {
            uint first = U32(image, hit + 25), second = U32(image, hit + 42), pointer = U32(image, hit + 11);
            return first == second && first >= low && first < high && pointer >= low && pointer < high
                ? new(hit, first, pointer) : null;
        }
        if (cached > 1) return null;
        int a = Count(image, PauseSetterDirectA, out int hitA), b = Count(image, PauseSetterDirectB, out int hitB);
        int direct = a == 1 && b == 0 ? hitA : a == 0 && b == 1 ? hitB : -1;
        if (direct < 0) return null;
        uint cell = U32(image, direct + 10);
        return cell >= low && cell < high ? new(direct, 0, cell) : null;
    }

    /// <summary>MAG-023: a presentation pass returns the script's done flag without stepping it.</summary>
    internal static byte[] MotionScriptStub(uint origin, uint presentation, uint entry, ReadOnlySpan<byte> firstInstruction)
    {
        var e = new Emitter(origin);
        uint done = BitConverter.ToUInt32(firstInstruction.Slice(2, 4));
        e.CmpByte(presentation, 0);
        int run = e.Forward(Je);
        e.Raw(0xa1).U32(done).Raw(0xc3);
        e.Bind(run);
        e.Raw(firstInstruction).Jump(Jmp, entry + (uint)firstInstruction.Length);
        return e.ToArray();
    }

    /// <summary>MAG-024: a presentation pass returns from the arc node at once (fastcall, plain ret).</summary>
    internal static byte[] ArcNodeStub(uint origin, uint presentation, uint entry, ReadOnlySpan<byte> prologue)
    {
        var e = new Emitter(origin);
        e.CmpByte(presentation, 0);
        int skip = e.Forward(Jne);
        e.Raw(prologue).Jump(Jmp, entry + (uint)prologue.Length);
        e.Bind(skip);
        e.Raw(0xc3);
        return e.ToArray();
    }

    /// <summary>
    ///     MAG-016: a presentation pass hands the fastcall driver (ecx list, edx param, plain ret) to
    ///     the cdecl callback in <paramref name="cell"/> as (list, param, cookie); any other pass runs it.
    /// </summary>
    internal static byte[] DriverStub(uint origin, uint presentation, uint cell, uint cookie, uint entry, ReadOnlySpan<byte> prologue, out int trampoline)
    {
        var e = new Emitter(origin);
        e.CmpByte(presentation, 0);
        int run = e.Forward(Je);
        e.Raw(0x68).U32(cookie).Raw(0x52, 0x51).CallCell(cell).Raw(0x83, 0xc4, 0x0c, 0xc3);
        e.Bind(run);
        trampoline = e.Count;
        e.Raw(prologue).Jump(Jmp, entry + (uint)prologue.Length);
        return e.ToArray();
    }

    /// <summary>cdecl (ecx, edx, target) to a two-register fastcall target that returns with a plain ret.</summary>
    internal static readonly byte[] FastcallAdapter =
        [0x8b, 0x4c, 0x24, 0x04, 0x8b, 0x54, 0x24, 0x08, 0x8b, 0x44, 0x24, 0x0c, 0xff, 0xe0];
}
