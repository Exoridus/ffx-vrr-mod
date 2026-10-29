using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint BxStreamHead = 0x40d4b0, BxSentry = 0x40d528, BxHandler = 0x40d586, BxDec = 0x40d5f4,
        BxPcCommit = 0x40d5ef, BxFreshWait = 0x40d5b8, BxNext = 0x40d5fa,
        BxGate = 0x40d69e, BxFollowSite = 0x40d986, BxFollowCall = 0x40d994, BxFollowReturn = 0x40d9a5;
    private const nint BxFollowAttach = 0x415670, BxFindUnit = 0x401060, BxFollowTable = 0x8492c8;
    private const nint BxRunAfterWalk = 0x4006b1, BxRunBeforeWalk = 0x400644, BxPassTwoWalk = 0x400a54;
    private static readonly ushort[] BxDeniedOpcodes = [0x1f, 0x7f];
    private const nint BxOpuTable = 0x848ec8;
    private static readonly (ushort Opcode, nint TargetRva)[] BxOpuHandlers =
        [(0x002, 0x4168b0), (0x009, 0x40c500), (0x053, 0x4171a0), (0x061, 0x419940)];
    private const string BxHeadPattern = "80 BE 1F 02 00 00 00 74 0D 80 BE 1D 02 00 00 00 0F 84 56 01 00 00 0F BF";
    private const string BxSentryPattern = "0F BF 8E 16 02 00 00 8A 44 19 10";
    private const string BxHandlerPattern = "57 56 FF D0 83 C4 08 80 BE 10 02 00 00 00 8B F8";
    private const string BxPcCommitPattern = "89 04 8B EB 06";
    private const string BxFreshWaitPattern = "FE C8 00 44 19 10";
    private const string BxDecPattern = "FE C8 88 44 19 10";
    private const string BxNextPattern = "66 FF 86 16 02 00 00 66 83 BE 16 02 00 00 04 0F 8C A1 FE FF FF 80 BE 1D 02 00 00 00 0F 85";
    private const string BxGatePattern = "0F BF 8E 12 02 00 00 89 0D";
    private const string BxRunAfterPattern = "68 ?? ?? ?? ?? E8 ?? ?? ?? ?? 6A 01 6A 00 E8 ?? ?? ?? ?? 6A 00 E8";
    private const string StaticVmBeforeBody = "568B35C0402A018B465885C0740F894654C705882E330200000000EB17A18C2E3302894654A1902E33020500000F00A3882E33026A006A00E8B3C5000083C408837E580075088B4654A38C2E330233C0A3882E33025EC3";
    private static byte[] static_vm_before_bytes()
    {
        byte[] bytes = Convert.FromHexString(StaticVmBeforeBody);
        foreach (int offset in new[] { 3, 19, 30, 38, 48, 74, 81 })
        {
            uint preferred = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            uint address = (uint)(nuint)FhUtil.ptr_at<byte>((nint)(preferred - 0x400000));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), address);
        }
        return bytes;
    }

    private static bool static_vm_before_matches()
    {
        byte[] expected = static_vm_before_bytes();
        Span<byte> observed = stackalloc byte[87];
        Span<byte> pointer = stackalloc byte[4];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(EngineAddresses.EternalEffectRunBefore), observed) && observed.SequenceEqual(expected)
            && MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(0x848d88), pointer)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pointer) == (uint)(nuint)FhUtil.ptr_at<byte>(0x848d90)
            && MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(0x848d9c), pointer)
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pointer) == (uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.EternalEffectRunBefore);
    }
    private nint _staticEffectVmCode;

    private static bool static_vm_matches(nint rva, string pattern)
    {
        Span<byte> bytes = stackalloc byte[FieldParticlePauseCode.PatternLength(pattern)];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(rva), bytes) && FieldParticlePauseCode.Matches(bytes, pattern);
    }

    private bool init_static_effect_vm_clock()
    {
        if (_staticEffectVmClockInstalled) return ReferenceEquals(s_battle_effects, this);
        if (IntPtr.Size != 4 || !_particle_pilot_installed || !_patches.IsPreparing || _bx != null || !static_vm_before_matches()
            || (s_battle_effects is not null && !ReferenceEquals(s_battle_effects, this))) return false;
        foreach (var (site, pattern) in new (nint, string)[]
        {
            (BxStreamHead, BxHeadPattern), (BxSentry, BxSentryPattern), (BxHandler, BxHandlerPattern),
            (BxDec, BxDecPattern), (BxPcCommit, BxPcCommitPattern), (BxFreshWait, BxFreshWaitPattern),
            (BxNext, BxNextPattern), (BxGate, BxGatePattern)
        })
            if (!static_vm_matches(site, pattern)) return false;
        Span<byte> table = stackalloc byte[(0x061 + 1) * 4];
        var handlers = BxOpuHandlers.Select(entry => (entry.Opcode, (uint)(nuint)FhUtil.ptr_at<byte>(entry.TargetRva))).ToArray();
        if (!MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(BxOpuTable), table) || !OpuHandlerTableMatches(table, handlers)) return false;

        byte* data = (byte*)NativeMemory.AllocZeroed(0x20);
        nint code = static_vm_alloc(0, 0x1000, 0x3000, 0x04);
        if (data == null || code == 0)
        {
            if (data != null) NativeMemory.Free(data);
            if (code != 0) static_vm_free(code, 0, 0x8000);
            return false;
        }
        uint Va(nint rva) => (uint)(nuint)FhUtil.ptr_at<byte>(rva);
        try
        {
            *(uint*)(data + 4) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&bx_on_stream_entry;
            *(uint*)(data + 8) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&bx_on_stream_next;
            uint d = (uint)(nint)data;
            var sites = new OpuSites(Va(BxNext), Va(BxSentry + 7), Va(BxHandler + 7), Va(BxNext + 7), Va(BxGate + 7),
                0, 0, 0, Va(BxFreshWait + 6));
            byte[] stubs = OpuStubs((uint)code, sites, new OpuData(d + 1, d + 2, d + 3, d + 4, d + 8, 0),
                BxDeniedOpcodes, handlers, out var at);
            if (stubs.Length > 0x1000) throw new InvalidOperationException("Static VM stubs exceed their page.");
            stubs.CopyTo(new Span<byte>((void*)code, stubs.Length));
            if (!static_vm_protect(code, 0x1000, 0x20, out _) || !static_vm_flush(-1, code, 0x1000))
                throw new InvalidOperationException("Static VM stubs could not be made executable.");

            _bx = data;
            _staticEffectVmCode = code;
            s_battle_effects = this;
            foreach (var (site, offset, length) in new (nint, int, int)[]
            {
                (BxSentry, at.Sentry, 7), (BxHandler, at.Handler, 7), (BxDec, at.Dec, 6),
                (BxPcCommit, at.PcCommit, 5), (BxFreshWait, at.FreshWait, 6),
                (BxNext, at.Next, 7), (BxGate, at.Gate, 7)
            })
                _patches.Write(site, JumpPatch(Va(site), (uint)code + (uint)offset, length));
            _staticEffectVmClockInstalled = true;
            return true;
        }
        catch (Exception error)
        {
            // Once queued, startup may publish these pointers; only process teardown proves native quiescence.
            if (_staticEffectVmCode == 0)
            {
                NativeMemory.Free(data);
                static_vm_free(code, 0, 0x8000);
            }
            _logger.Error($"[Vrr] Static effect VM clock preparation failed: {error.Message}");
            return false;
        }
    }

    private void release_static_effect_vm_clock()
    {
        _staticEffectVmClockInstalled = false;
        // Retain code, data and repeat observations: callbacks can already be past their activation check.
    }

    [DllImport("kernel32.dll", EntryPoint = "VirtualAlloc", SetLastError = true)]
    private static extern nint static_vm_alloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32.dll", EntryPoint = "VirtualFree", SetLastError = true)]
    private static extern bool static_vm_free(nint address, nuint size, uint type);
    [DllImport("kernel32.dll", EntryPoint = "VirtualProtect", SetLastError = true)]
    private static extern bool static_vm_protect(nint address, nuint size, uint protection, out uint previous);
    [DllImport("kernel32.dll", EntryPoint = "FlushInstructionCache", SetLastError = true)]
    private static extern bool static_vm_flush(nint process, nint address, nuint size);
}