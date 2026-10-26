using Xunit;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public sealed class OpuHandlerPolicyTests
{
    [Fact]
    public void HeldMode3AppliesBoundedSemanticsOnEveryDispatch()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        foreach (ushort operandBits in new ushort[] { 0, 0x0200, 0x8000, 0xfe00 })
        for (ushort opcode = 0; opcode < 512; opcode++)
            fixture.Check(3, (ushort)(opcode | operandBits), opcode switch
            {
                0x053 => Outcome.AdvanceWithoutCall,
                0x002 or 0x061 => Outcome.Call,
                0x009 when operandBits < 0x0400 => Outcome.Call,
                _ => Outcome.Yield,
            });
    }

    [Fact]
    public void HeldMode3RejectsEveryWaitOperandAboveOne()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        for (int count = 0; count < 128; count++)
            fixture.Check(3, (ushort)(0x009 | (count << 9)), count < 2 ? Outcome.Call : Outcome.Yield);
    }

    [Fact]
    public void HeldMode3SkipsHistoryAppendAndContinuesDispatch()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        foreach (ushort operandBits in new ushort[] { 0, 0x0200, 0x8000, 0xfe00 })
        {
            fixture.Check(3, (ushort)(0x053 | operandBits), Outcome.AdvanceWithoutCall);
            fixture.Check(3, 0x009, Outcome.Call);
            fixture.Check(3, 0x061, Outcome.Call);
        }
        fixture.Check(3, 0x153, Outcome.Yield, fixture.Target(0x053));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(255)]
    public void OtherModesKeepNativeWaitOperandsAndHistoryHandler(byte hold)
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        for (int count = 0; count < 128; count++)
            fixture.Check(hold, (ushort)(0x009 | (count << 9)), Outcome.Call);
        fixture.Check(hold, 0x053, Outcome.Call);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NativeModesKeepEveryHandler(byte hold)
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        for (ushort opcode = 0; opcode < 512; opcode++)
        {
            fixture.Check(hold, opcode, Outcome.Call);
            fixture.Check(hold, opcode, Outcome.Call, fixture.Target(0));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(255)]
    public void OtherHeldModesKeepExistingDenies(byte hold)
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        for (ushort opcode = 0; opcode < 512; opcode++)
        {
            fixture.Check(hold, opcode, opcode is 0x1f or 0x7f ? Outcome.Yield : Outcome.Call);
            fixture.Check(hold, opcode, opcode is 0x1f or 0x7f ? Outcome.Yield : Outcome.Call, fixture.Target(0));
        }
    }

    [Fact]
    public void ExistingDeniesOverrideCandidates()
    {
        using var fixture = new HandlerFixture([0x002, 0x009, 0x053, 0x061, 0x1f, 0x7f]);
        foreach (ushort opcode in new ushort[] { 0x002, 0x009, 0x053, 0x061, 0x1f, 0x7f })
        {
            fixture.Check(3, opcode, Outcome.Yield);
            fixture.Check(2, opcode, Outcome.Yield);
            fixture.Check(0, opcode, Outcome.Call);
        }
    }

    [Fact]
    public void AllowedDispatchDoesNotAdmitTheFollowingUnsupportedOpcode()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        fixture.Check(3, 0x002, Outcome.Call);
        fixture.Check(3, 0x003, Outcome.Yield);
        fixture.Check(3, 0x061, Outcome.Call);
        fixture.Check(3, 0x161, Outcome.Yield);
    }

    [Fact]
    public void HeldMode3RejectsOtherCandidateTargetsOnEveryDispatch()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        foreach (ushort operandBits in new ushort[] { 0, 0x0200, 0x8000, 0xfe00 })
        foreach (ushort opcode in new ushort[] { 0x002, 0x009, 0x053, 0x061 })
        foreach (ushort other in new ushort[] { 0x002, 0x009, 0x053, 0x061 })
            if (opcode != other) fixture.Check(3, (ushort)(opcode | operandBits), Outcome.Yield, fixture.Target(other));
    }

    [Fact]
    public void HeldMode3RejectsNullAndUnrelatedTargets()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        foreach (ushort opcode in new ushort[] { 0x002, 0x009, 0x053, 0x061 })
        {
            fixture.Check(3, opcode, Outcome.Yield, 0);
            fixture.Check(3, opcode, Outcome.Yield, fixture.Target(0));
        }
    }

    [Fact]
    public void CorrectTargetDoesNotAdmitFollowingWrongTarget()
    {
        using var fixture = new HandlerFixture([0x1f, 0x7f]);
        fixture.Check(3, 0x002, Outcome.Call);
        fixture.Check(3, 0x002, Outcome.Yield, fixture.Target(0x009));
        fixture.Check(3, 0x002, Outcome.Call);
    }

    [Fact]
    public void HandlerTargetsRelocateWithEachAllocatedFixture()
    {
        using var first = new HandlerFixture([0x1f, 0x7f]);
        using var second = new HandlerFixture([0x1f, 0x7f]);
        foreach (ushort opcode in new ushort[] { 0x002, 0x009, 0x053, 0x061 })
        {
            Assert.NotEqual(first.Target(opcode), second.Target(opcode));
            first.Check(3, opcode, opcode == 0x053 ? Outcome.AdvanceWithoutCall : Outcome.Call);
            second.Check(3, opcode, opcode == 0x053 ? Outcome.AdvanceWithoutCall : Outcome.Call);
            first.Check(3, opcode, Outcome.Yield, second.Target(opcode));
            second.Check(3, opcode, Outcome.Yield, first.Target(opcode));
        }
    }

    [Theory]
    [InlineData(0x10000000u)]
    [InlineData(0x65000000u)]
    public void PreflightAcceptsRelocatedExpectedEntriesAndRejectsEachChangedCell(uint imageBase)
    {
        (ushort Opcode, uint Target)[] expected =
        [
            (0x002, imageBase + 0x4168b0), (0x009, imageBase + 0x40c500),
            (0x053, imageBase + 0x4171a0), (0x061, imageBase + 0x419940),
        ];
        byte[] table = new byte[0x188];
        BitConverter.GetBytes(imageBase + 0x4168b0).CopyTo(table, 0x008);
        BitConverter.GetBytes(imageBase + 0x40c500).CopyTo(table, 0x024);
        BitConverter.GetBytes(imageBase + 0x4171a0).CopyTo(table, 0x14c);
        BitConverter.GetBytes(imageBase + 0x419940).CopyTo(table, 0x184);
        Assert.True(OpuHandlerTableMatches(table, expected));
        foreach (int cell in new[] { 0x008, 0x024, 0x14c, 0x184 })
        {
            table[cell] ^= 1;
            Assert.False(OpuHandlerTableMatches(table, expected));
            table[cell] ^= 1;
            Assert.False(OpuHandlerTableMatches(table.AsSpan(0, cell + 3), expected));
        }
        Assert.False(OpuHandlerTableMatches([], expected));
        Assert.False(OpuHandlerTableMatches(table, [(0x002, imageBase + 0x40c500)]));
    }

    private enum Outcome { Call, Yield, AdvanceWithoutCall }

    private unsafe sealed class HandlerFixture : IDisposable
    {
        private const uint Pc = 0x13572468, NativeResult = 0x24681357;
        private const uint Carrier = 0x12345678, ReturnedCarrier = 0x76543210;
        private const uint Bx = 0x11223344, Bp = 0x55667788;
        private readonly nint _data, _code;
        private readonly delegate* unmanaged[Cdecl]<void> _run;
        private byte* Scratch => (byte*)_data + 0x400;
        private uint* State => (uint*)_data;

        internal HandlerFixture(ushort[] denied)
        {
            Assert.Equal(4, IntPtr.Size);
            _data = VirtualAlloc(0, 0x1000, 0x3000, 4);
            Assert.NotEqual(0, _data);
            try
            {
                _code = VirtualAlloc(0, 0x1000, 0x3000, 4);
                Assert.NotEqual(0, _code);
                uint code = (uint)_code, data = (uint)_data;
                uint continuation = code + 0x100, stubs = code + 0x500;
                var wrapper = new Emitter(code);
                wrapper.Raw(0x55, 0x53, 0x56, 0x57, 0xbe).U32((uint)Scratch)
                    .Raw(0xbf).U32(Pc).Raw(0xbb).U32(Bx).Raw(0xbd).U32(Bp)
                    .Raw(0xb9).U32(Carrier).Raw(0xa1).U32(data + 52)
                    .Raw(0x89, 0x25).U32(data + 24);
                byte[] generated = OpuStubs(stubs,
                    new(continuation, continuation, continuation, continuation, continuation, continuation, 0, 0),
                    new(data, data + 64, data + 65, data + 68, data + 72, data + 76), denied,
                    [(0x002, Target(0x002)), (0x009, Target(0x009)), (0x053, Target(0x053)), (0x061, Target(0x061))], out var offsets);
                wrapper.Jump(Jmp, stubs + (uint)offsets.Handler);
                Copy(code, wrapper.ToArray());
                Copy(stubs, generated);

                var tail = new Emitter(continuation);
                tail.Raw(0xa3).U32(data + 8).Raw(0x89, 0x0d).U32(data + 12)
                    .Raw(0x89, 0x35).U32(data + 16).Raw(0x89, 0x3d).U32(data + 20)
                    .Raw(0x89, 0x25).U32(data + 28).Raw(0x89, 0x1d).U32(data + 44)
                    .Raw(0x89, 0x2d).U32(data + 48)
                    .Raw(0x8b, 0x25).U32(data + 24).Raw(0x5f, 0x5e, 0x5b, 0x5d, 0xc3);
                Copy(continuation, tail.ToArray());

                for (int slot = 0; slot < 5; slot++)
                {
                    uint callee = code + 0x200 + (uint)slot * 64;
                    var callback = new Emitter(callee);
                    callback.Raw(0xff, 0x05).U32(data + 4)
                        .Raw(0x8b, 0x54, 0x24, 0x04, 0x89, 0x15).U32(data + 32)
                        .Raw(0x8b, 0x54, 0x24, 0x08, 0x89, 0x15).U32(data + 36)
                        .Raw(0x89, 0x0d).U32(data + 40)
                        .Raw(0xb9).U32(ReturnedCarrier).Raw(0xb8).U32(NativeResult).Raw(0xc3);
                    Copy(callee, callback.ToArray());
                }
                Assert.True(VirtualProtect(_code, 0x1000, 0x20, out _));
                Assert.True(FlushInstructionCache(GetCurrentProcess(), _code, 0x1000));
                _run = (delegate* unmanaged[Cdecl]<void>)_code;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal uint Target(ushort opcode) => (uint)_code + 0x200 + (opcode switch
        {
            0x002 => 0u,
            0x009 => 64u,
            0x053 => 128u,
            0x061 => 192u,
            _ => 256u,
        });

        internal void Check(byte hold, ushort instruction, Outcome outcome, uint? target = null)
        {
            new Span<byte>((void*)_data, 0x1000).Clear();
            State[13] = target ?? Target((ushort)(instruction & 0x1ff));
            *(byte*)_data = hold;
            *(ushort*)(Scratch + 0x21a) = instruction;
            byte[] expectedScratch = new ReadOnlySpan<byte>(Scratch, 0xc00).ToArray();
            if (outcome == Outcome.Yield) expectedScratch[0x210] = 1;
            _run();
            bool accepted = outcome == Outcome.Call;
            string context = $"hold={hold}, instruction=0x{instruction:x4}";
            Assert.True(State[1] == (accepted ? 1u : 0u), context);
            Assert.Equal(outcome switch { Outcome.Call => NativeResult, Outcome.AdvanceWithoutCall => Pc + 4, _ => Pc }, State[2]);
            Assert.Equal(accepted ? ReturnedCarrier : Carrier, State[3]);
            Assert.Equal((uint)Scratch, State[4]);
            Assert.Equal(Pc, State[5]);
            Assert.Equal(State[6], State[7]);
            Assert.Equal(Bx, State[11]);
            Assert.Equal(Bp, State[12]);
            Assert.Equal(expectedScratch, new ReadOnlySpan<byte>(Scratch, 0xc00).ToArray());
            Assert.Equal(instruction, *(ushort*)(Scratch + 0x21a));
            if (accepted)
            {
                Assert.Equal((uint)Scratch, State[8]);
                Assert.Equal(Pc, State[9]);
                Assert.Equal(Carrier, State[10]);
            }
            else
            {
                Assert.Equal(0u, State[8]);
                Assert.Equal(0u, State[9]);
                Assert.Equal(0u, State[10]);
            }
        }

        private static void Copy(uint address, byte[] bytes) => Marshal.Copy(bytes, 0, (nint)address, bytes.Length);

        public void Dispose()
        {
            if (_code != 0) VirtualFree(_code, 0, 0x8000);
            if (_data != 0) VirtualFree(_data, 0, 0x8000);
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint previous);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
        [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    }
}
