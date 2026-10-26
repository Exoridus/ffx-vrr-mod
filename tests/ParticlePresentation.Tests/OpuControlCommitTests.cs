using System.Runtime.InteropServices;
using Xunit;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public sealed class OpuControlCommitTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCommitInstructionsMutateControlValuesEvenInMode3(bool wait)
    {
        using var fixture = new CommitFixture(wait, native: true);
        fixture.Check(3, 0, 0, held: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldMode3PreservesAllStreamControlValues(bool wait)
    {
        using var fixture = new CommitFixture(wait);
        for (int stream = 0; stream < 4; stream++)
        foreach (byte yield in new byte[] { 0, 1, 2, 128, 255 })
            fixture.Check(3, stream, yield, held: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(255)]
    public void OtherModesReplayNativeCommitAndAlWrap(byte hold)
    {
        foreach (bool wait in new[] { false, true })
        using (var fixture = new CommitFixture(wait))
        for (int stream = 0; stream < 4; stream++)
        foreach (byte yield in new byte[] { 0, 1, 2, 128, 255 })
            fixture.Check(hold, stream, yield, held: false);
    }

    private unsafe sealed class CommitFixture : IDisposable
    {
        private const uint Pc = 0x13572468, Ax = 0xa1b2c300;
        private const uint Dx = 0x11223344, Bp = 0x55667788, Di = 0xabcdef01;
        private readonly nint _data, _code;
        private readonly bool _wait;
        private readonly delegate* unmanaged[Cdecl]<void> _run;
        private byte* Record => (byte*)_data + 0x200;
        private byte* Scratch => (byte*)_data + 0x400;
        private uint* State => (uint*)_data;

        internal CommitFixture(bool wait, bool native = false)
        {
            Assert.Equal(4, IntPtr.Size);
            _wait = wait;
            _data = VirtualAlloc(0, 0x1000, 0x3000, 4);
            Assert.NotEqual(0, _data);
            try
            {
                _code = VirtualAlloc(0, 0x1000, 0x3000, 4);
                Assert.NotEqual(0, _code);
                uint code = (uint)_code, data = (uint)_data, stubs = code + 0x500;
                uint tail = code + 0x100, freshReturn = code + 0x180;
                byte[] generated = OpuStubs(stubs,
                    new(code + 0x140, tail, tail, tail, tail, tail, 0, 0, freshReturn),
                    new(data, data + 1, data + 2, data + 4, data + 8, data + 12), [], [], out var offsets);
                Copy(stubs, generated);
                var next = new Emitter(code + 0x140);
                next.Jump(Jmp, stubs + (uint)offsets.Next);
                Copy(code + 0x140, next.ToArray());
                var callback = new Emitter(code + 0x1c0);
                callback.Raw(0xff, 0x05).U32(data + 64).Raw(0xc3);
                Copy(code + 0x1c0, callback.ToArray());
                State[2] = code + 0x1c0;
                var fresh = new Emitter(freshReturn);
                fresh.Raw(0xc6, 0x86, 0x10, 0x02, 0x00, 0x00, 0x00).Jump(Jmp, tail);
                Copy(freshReturn, fresh.ToArray());
                uint nativeSite = code + 0x200;
                var writer = new Emitter(nativeSite);
                if (wait) writer.Raw(0xfe, 0xc8, 0x00, 0x44, 0x19, 0x10).Jump(Jmp, freshReturn);
                else writer.Raw(0x89, 0x04, 0x8b).Jump(Jmp, code + 0x140);
                Copy(nativeSite, writer.ToArray());
                var wrapper = new Emitter(code);
                wrapper.Raw(0x55, 0x53, 0x56, 0x57, 0xbe).U32((uint)Scratch)
                    .Raw(0xbb).U32((uint)Record).Raw(0xbd).U32(Bp).Raw(0xbf).U32(Di)
                    .Raw(0xba).U32(Dx).Raw(0x8b, 0x0d).U32(data + 16)
                    .Raw(0xa1).U32(data + 20).Raw(0x89, 0x25).U32(data + 24)
                    .Jump(Jmp, native ? nativeSite : stubs + (uint)(wait ? offsets.FreshWait : offsets.PcCommit));
                Copy(code, wrapper.ToArray());
                var capture = new Emitter(tail);
                capture.Raw(0xa3).U32(data + 28).Raw(0x89, 0x0d).U32(data + 32)
                    .Raw(0x89, 0x15).U32(data + 36).Raw(0x89, 0x1d).U32(data + 40)
                    .Raw(0x89, 0x2d).U32(data + 44).Raw(0x89, 0x35).U32(data + 48)
                    .Raw(0x89, 0x3d).U32(data + 52).Raw(0x89, 0x25).U32(data + 56)
                    .Raw(0x5f, 0x5e, 0x5b, 0x5d, 0xc3);
                Copy(tail, capture.ToArray());
                Assert.True(VirtualProtect(_code, 0x1000, 0x20, out _));
                Assert.True(FlushInstructionCache(GetCurrentProcess(), _code, 0x1000));
                _run = (delegate* unmanaged[Cdecl]<void>)_code;
            }
            catch { Dispose(); throw; }
        }

        internal void Check(byte hold, int stream, byte yield, bool held)
        {
            new Span<byte>(Record, 0x300).Fill(0x5a);
            *(byte*)_data = hold;
            State[4] = (uint)stream;
            State[5] = _wait ? Ax | yield : Pc;
            State[16] = 0;
            *(ushort*)(Scratch + 0x216) = (ushort)stream;
            Scratch[0x210] = 0x73;
            byte[] expectedRecord = new ReadOnlySpan<byte>(Record, 0x200).ToArray();
            byte[] expectedScratch = new ReadOnlySpan<byte>(Scratch, 0x300).ToArray();
            if (_wait)
            {
                if (!held) expectedRecord[stream + 0x10] = unchecked((byte)(0x5a + yield - 1));
                expectedScratch[0x210] = 0;
            }
            else
            {
                if (!held) BitConverter.GetBytes(Pc).CopyTo(expectedRecord, stream * 4);
                BitConverter.GetBytes((ushort)(stream + 1)).CopyTo(expectedScratch, 0x216);
            }
            _run();
            Assert.Equal(expectedRecord, new ReadOnlySpan<byte>(Record, 0x200).ToArray());
            Assert.Equal(expectedScratch, new ReadOnlySpan<byte>(Scratch, 0x300).ToArray());
            Assert.Equal(_wait && !held ? (Ax | unchecked((byte)(yield - 1))) : State[5], State[7]);
            Assert.Equal((uint)stream, State[8]);
            Assert.Equal(Dx, State[9]);
            Assert.Equal((uint)Record, State[10]);
            Assert.Equal(Bp, State[11]);
            Assert.Equal((uint)Scratch, State[12]);
            Assert.Equal(Di, State[13]);
            Assert.Equal(State[6], State[14]);
            Assert.Equal(_wait ? 0u : 1u, State[16]);
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