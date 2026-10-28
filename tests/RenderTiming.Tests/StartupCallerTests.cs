using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class StartupCallerTests
{
    private static readonly byte[] Trampoline = Convert.FromHexString("E8FB0F40F0E9FB1F40F0");
    private const uint TrampolineReturn = 0x10001005;
    private const uint CookieTarget = 0x00402000;
    private const uint EntryContinuation = 0x00403005;

    [Theory]
    [InlineData("E8FB0F40F0E9FB1F40F0", 0x10001005u, 0x00402000u, 0x00403005u)]
    [InlineData("E88BC91B8FE98BA94B8F", 0x72345675u, 0x01502000u, 0x01800005u)]
    [InlineData("E8ABBAFC0EE99BBADCAE", 0xE1234565u, 0xF0200010u, 0x90000005u)]
    [InlineData("E8EBFEFFFFE9F6FDFFFF", 0x00000105u, 0xFFFFFFF0u, 0xFFFFFF00u)]
    [InlineData("E80B020000E9F6020000", 0xFFFFFF05u, 0x00000110u, 0x00000200u)]
    [InlineData("E810000000E910000000", 5u, 0x15u, 0x1Au)]
    [InlineData("E8F0FFFFFFE9F1FFFFFF", 0xFFFFFFFAu, 0xFFFFFFEAu, 0xFFFFFFF0u)]
    [InlineData("E800000080E9FFFFFF7F", 0x70000005u, 0xF0000005u, 0xF0000009u)]
    public void Exact_trampoline_matches_at_relocated_LAA_and_wrapping_x86_addresses(string hex, uint address, uint cookie, uint entry)
    {
        Assert.True(StartupCaller.Matches(Convert.FromHexString(hex), address, address, cookie, entry));
    }

    [Fact]
    public void Original_entry_return_cannot_replace_the_actual_trampoline_return()
    {
        Assert.False(StartupCaller.Matches(Trampoline, EntryContinuation, EntryContinuation, CookieTarget, EntryContinuation));
        Assert.False(StartupCaller.Matches(Trampoline, TrampolineReturn, EntryContinuation, CookieTarget, EntryContinuation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(11)]
    public void Caller_code_must_be_exactly_ten_bytes(int length)
    {
        var code = new byte[length];
        Trampoline.AsSpan(0, Math.Min(length, Trampoline.Length)).CopyTo(code);
        Assert.False(StartupCaller.Matches(code, TrampolineReturn, TrampolineReturn, CookieTarget, EntryContinuation));
    }

    [Theory]
    [InlineData(0, 0x90)]
    [InlineData(0, 0xE9)]
    [InlineData(5, 0x90)]
    [InlineData(5, 0xE8)]
    public void Call_and_jump_must_have_the_exact_relative_branch_opcodes(int offset, int opcode)
    {
        var code = (byte[])Trampoline.Clone();
        code[offset] = (byte)opcode;
        Assert.False(StartupCaller.Matches(code, TrampolineReturn, TrampolineReturn, CookieTarget, EntryContinuation));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Every_relative_displacement_byte_affects_target_qualification(int offset)
    {
        var code = (byte[])Trampoline.Clone();
        code[offset] ^= 1;
        Assert.False(StartupCaller.Matches(code, TrampolineReturn, TrampolineReturn, CookieTarget, EntryContinuation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Stack_word_and_both_expected_destinations_must_match(int mismatch)
    {
        Assert.False(StartupCaller.Matches(Trampoline, TrampolineReturn,
            mismatch == 0 ? TrampolineReturn + 1 : TrampolineReturn,
            mismatch == 1 ? CookieTarget + 1 : CookieTarget,
            mismatch == 2 ? EntryContinuation + 1 : EntryContinuation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Zero_pointers_are_rejected(int zeroPointer)
    {
        var code = zeroPointer == 2 ? Convert.FromHexString("E8FBEFFFEFE9FB1F40F0")
            : zeroPointer == 3 ? Convert.FromHexString("E8FB0F40F0E9F6EFFFEF") : Trampoline;
        Assert.False(StartupCaller.Matches(code,
            zeroPointer == 0 ? 0 : TrampolineReturn,
            zeroPointer == 1 ? 0 : TrampolineReturn,
            zeroPointer == 2 ? 0 : CookieTarget,
            zeroPointer == 3 ? 0 : EntryContinuation));
    }

    [Fact]
    public void Pointer_width_is_checked_before_truncating_to_x86()
    {
        if (nuint.Size == 4) return;
        nuint highBits = unchecked((nuint)0x1_0000_0000UL);
        Assert.False(StartupCaller.Matches(Trampoline, highBits + TrampolineReturn, highBits + TrampolineReturn, CookieTarget, EntryContinuation));
        Assert.False(StartupCaller.Matches(Trampoline, TrampolineReturn, highBits + TrampolineReturn, CookieTarget, EntryContinuation));
        Assert.False(StartupCaller.Matches(Trampoline, TrampolineReturn, TrampolineReturn, highBits + CookieTarget, EntryContinuation));
        Assert.False(StartupCaller.Matches(Trampoline, TrampolineReturn, TrampolineReturn, CookieTarget, highBits + EntryContinuation));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void Code_range_cannot_start_below_zero(uint address)
    {
        var code = Convert.FromHexString("E810000000E910000000");
        Assert.False(StartupCaller.Matches(code, address, address, address + 0x10u, address + 0x15u));
    }

    [Theory]
    [InlineData(0xFFFFFFFBu)]
    [InlineData(0xFFFFFFFCu)]
    [InlineData(0xFFFFFFFDu)]
    [InlineData(0xFFFFFFFEu)]
    [InlineData(0xFFFFFFFFu)]
    public void Code_range_cannot_cross_the_x86_address_limit(uint address)
    {
        var code = Convert.FromHexString("E8F0FFFFFFE9F1FFFFFF");
        Assert.False(StartupCaller.Matches(code, address, address, address - 16u, unchecked(address + 5u - 15u)));
    }
}
