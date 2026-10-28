using System.Buffers;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private nint _bxNodeWalkOriginal;
    private readonly nint[] _bxOppOriginal = new nint[20];
    private readonly byte[] _bxOppBuffer = new byte[0x10000];
    private long _bxHeldWalks, _bxOppRestored, _bxOppEnded, _bxNodeDraws;

    private void opu_held_walk(d_pilot_worker original, ushort family, byte mode)
    {
        byte previous = _bx[1];
        _bx[1] = 3;
        _bxHeldWalks++;
        try { original(family, mode); }
        finally { _bx[1] = previous; }
    }

    // ---- MAG-030: OPP record ops ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void bx_on_opp_1(nint scratch, nint ctx) => bx_opp(1, scratch, ctx);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void bx_on_opp_9(nint scratch, nint ctx) => bx_opp(9, scratch, ctx);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void bx_on_opp_10(nint scratch, nint ctx) => bx_opp(10, scratch, ctx);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void bx_on_opp_17(nint scratch, nint ctx) => bx_opp(17, scratch, ctx);

    private static void bx_opp(int op, nint scratch, nint ctx)
    {
        var module = s_battle_effects;
        if (module is null) { Environment.FailFast("Battle operation has no owning module."); return; }
        if (!module.RuntimeEffectsArmed)
        {
            if ((uint)op >= (uint)module._bxOppOriginal.Length || module._bxOppOriginal[op] == 0)
            { Environment.FailFast("Battle operation native continuation is unavailable."); return; }
            ((delegate* unmanaged[Cdecl]<nint, nint, void>)module._bxOppOriginal[op])(scratch, ctx);
            return;
        }
        var original = (delegate* unmanaged[Cdecl]<nint, nint, void>)module._bxOppOriginal[op];
        nint record = 0;
        byte* saved = stackalloc byte[0x100];
        nint buffer = 0;
        int length = 0;
        try
        {
            if ((RenderPassContext.IsPresentation || module.IsOwnedBattleEffectHold) && Environment.CurrentManagedThreadId == module._particle_pilot_thread)
            {
                record = (nint)(*(uint*)(scratch + 0x220));
                if (!MemoryProbe.TryCopy(record, new Span<byte>(saved, 0x100))) record = 0;
                if (record != 0 && op is 9 or 17)
                {
                    buffer = (nint)(*(uint*)(record + (op == 9 ? 0xa8 : 0xbc)));
                    if (MemoryProbe.IsReadable(buffer + (op == 9 ? 0 : 4), 2))
                        length = OppBufferLength(op, *(ushort*)(buffer + (op == 9 ? 0 : 4)));
                    if (length > module._bxOppBuffer.Length || !MemoryProbe.TryCopy(buffer, module._bxOppBuffer.AsSpan(0, length)))
                        length = 0;
                }
            }
        }
        catch (Exception) { record = 0; length = 0; }
        original(scratch, ctx);
        if (record == 0) return;
        ushort flags = *(ushort*)(record + 0xb0), before = *(ushort*)(saved + 0xb0);
        if (((flags & 0x8000) != 0 && (before & 0x8000) == 0) || *(byte*)(record + 0xbb) != saved[0xbb])
        {
            module._bxOppEnded++;
            return;
        }
        new ReadOnlySpan<byte>(saved, 0x100).CopyTo(new Span<byte>((void*)record, 0x100));
        if (length > 0) module._bxOppBuffer.AsSpan(0, length).CopyTo(new Span<byte>((void*)buffer, length));
        module._bxOppRestored++;
    }

    // ---- MAG-020/026: family-X node walk ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void bx_on_node_walk(nint head)
    {
        var module = s_battle_effects;
        if (module is null) { Environment.FailFast("Battle node walk has no owning module."); return; }
        if (!module.RuntimeEffectsArmed)
        {
            if (module._bxNodeWalkOriginal == 0)
            { Environment.FailFast("Battle node walk native continuation is unavailable."); return; }
            ((delegate* unmanaged[Cdecl]<nint, void>)module._bxNodeWalkOriginal)(head);
            return;
        }
        if (!RenderPassContext.IsPresentation && !module.IsOwnedBattleEffectHold)
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)module._bxNodeWalkOriginal)(head);
            return;
        }
        if (Environment.CurrentManagedThreadId != module._particle_pilot_thread)
        {
            module.refuse_independent_presentation("node_thread_changed");
            return;
        }
        try { module.bx_draw_nodes(head); }
        catch (Exception) { }
    }

    // Draws each node without its state handler; a record's other fields are put back after its draw,
    // the particle program block at +0x280 is left to the particle pause.
    private void bx_draw_nodes(nint head)
    {
        byte[] saved = ArrayPool<byte>.Shared.Rent(0x490);
        try
        {
            int guard = 0;
            for (nint node = head; node != 0 && guard < 4096 && MemoryProbe.IsReadable(node, 0xb4); guard++)
            {
                uint callback = *(uint*)(node + 0xb0);
                if (callback != 0 && (*(uint*)node & 4) == 0)
                {
                    var draw = (delegate* unmanaged[Cdecl]<nint, void>)(nint)callback;
                    bool record = node != head && node != head + 0xbc && MemoryProbe.TryCopy(node, saved.AsSpan(0, 0x490));
                    draw(node);
                    if (record)
                    {
                        saved.AsSpan(0xc, 0x280 - 0xc).CopyTo(new Span<byte>((void*)(node + 0xc), 0x280 - 0xc));
                        saved.AsSpan(0x330, 0x490 - 0x330).CopyTo(new Span<byte>((void*)(node + 0x330), 0x490 - 0x330));
                    }
                    _bxNodeDraws++;
                }
                node = (nint)(*(uint*)(node + 8));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(saved); }
    }
}
