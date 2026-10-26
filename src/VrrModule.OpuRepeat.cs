using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    // Native data block: +0 presentation pass, +1 walker hold mode, +2 stream skip, +3 follow skip,
    // +4 stream entry callback, +8 stream next callback, +0xC follow callback, +0x10 driver callback.
    private byte* _bx;
    private static VrrModule? s_battle_effects;
    private readonly OpuRepeatTracker _bxRepeat = new();

    // ---- MAG-025 callbacks ----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int bx_on_stream_entry(nint scratch, nint record)
    {
        try { return s_battle_effects?.bx_stream_entry(scratch, record) ?? 0; }
        catch (Exception) { return 0; }
    }

    private int bx_stream_entry(nint scratch, nint record)
    {
        if (Environment.CurrentManagedThreadId != _particle_pilot_thread) return 0;
        int stream = *(short*)(scratch + 0x216) & 3;
        if (!_bxRepeat.Entry(_bx[1], record, stream, *(uint*)(record + stream * 4), *(byte*)(record + 0x10 + stream), RenderPassContext.HistoryEpoch)) return 0;
        // Sweep 2 already cleared the stream's latch bit before this point; the skipped stream keeps it.
        if (*(byte*)(scratch + 0x21d) != 0) *(byte*)(record + 0x1f) |= (byte)(1 << stream);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void bx_on_stream_next(nint scratch, nint record)
    {
        try
        {
            var module = s_battle_effects;
            if (module is null || Environment.CurrentManagedThreadId != module._particle_pilot_thread) return;
            int stream = *(short*)(scratch + 0x216) & 3;
            module._bxRepeat.Next(record, stream, *(uint*)(record + stream * 4), RenderPassContext.HistoryEpoch);
        }
        catch (Exception) { }
    }
}
