namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_init_records(nint worker, nint thread, int level);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_atel_init_records(nint worker, nint thread, int level)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_atel_init_records>(new FhCallLocation(EngineAddresses.AtelInitMotionRecords, 0)).chain_from(h_atel_init_records).fnptr)(worker, thread, level);
            return;
        }

        new FhCallHandle<d_atel_init_records>(new FhCallLocation(EngineAddresses.AtelInitMotionRecords, 0))
            .chain_from(h_atel_init_records).fnptr!(worker, thread, level);

        retime_atel_defaults(*(nint*)(thread + 0x44), *(nint*)(thread + 0x48), Scale);
    }

    private static void retime_atel_defaults(nint move, nint rotation, float scale)
    {
        if (scale == 1f) return;

        *(short*)(move + 4) = (short)scale_deadline(*(short*)(move + 4), scale);
        *(short*)(rotation + 4) = (short)scale_deadline(*(short*)(rotation + 4), scale);
        for (int i = 0; i < 4; i++) *(float*)(rotation + 0x10 + i * 4) /= scale;
    }
}
