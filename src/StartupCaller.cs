using System;
using System.Buffers.Binary;

namespace Fahrenheit.Mods.Vrr;

internal static class StartupCaller
{
    internal static bool Matches(ReadOnlySpan<byte> callerCode, nuint returnAddress, nuint stackReturn, nuint cookieTarget, nuint entryContinuation)
    {
        if (callerCode.Length != 10 || returnAddress < 5 || returnAddress > uint.MaxValue - 5
            || stackReturn != returnAddress
            || cookieTarget == 0 || cookieTarget > uint.MaxValue
            || entryContinuation == 0 || entryContinuation > uint.MaxValue
            || callerCode[0] != 0xE8 || callerCode[5] != 0xE9)
            return false;

        uint address = (uint)returnAddress;
        // x86 rel32 destinations wrap at 32 bits, including signed displacements.
        return unchecked(address + BinaryPrimitives.ReadUInt32LittleEndian(callerCode.Slice(1, 4))) == (uint)cookieTarget
            && unchecked(address + 5u + BinaryPrimitives.ReadUInt32LittleEndian(callerCode.Slice(6, 4))) == (uint)entryContinuation;
    }
}
