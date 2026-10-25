using Fahrenheit.Mods.Shared;
using Xunit;

public sealed class MenuWaterExpansionTests
{
    static byte[] Read(int rva, int length) => rva switch {
        0x25b62f => [0xff,0x34,0x85,0x40,0x9c,0xc5,0],
        0x27cba1 => [0xbe,0x40,0x9c,0xc5,0],
        0x27cbc0 => [0x81,0xfe,0x58,0x9d,0xc5,0],
        0x27efb5 => [0xbe,0x40,0x9c,0xc5,0],
        0x27f015 => [0x81,0xfe,0x58,0x9d,0xc5,0],
        0x25b6d9 => [0x80,0xf9,0x46],
        _ => throw new InvalidOperationException() };

    [Fact]
    public void RelocatesDrawPreloadUnloadAndBothExclusiveEndsTogether()
    {
        var writes = MenuWaterExpansion.Plan(0x400000, 0x20000000, Read);
        Assert.Equal(6, writes.Count);
        Assert.Equal(0x20000000u, BitConverter.ToUInt32(writes.Single(w => w.Rva == 0x25b632).Bytes));
        Assert.Equal(0x20000000u, BitConverter.ToUInt32(writes.Single(w => w.Rva == 0x27cba2).Bytes));
        Assert.Equal(0x20000230u, BitConverter.ToUInt32(writes.Single(w => w.Rva == 0x27cbc2).Bytes));
        Assert.Equal(0x20000000u, BitConverter.ToUInt32(writes.Single(w => w.Rva == 0x27efb6).Bytes));
        Assert.Equal(0x20000230u, BitConverter.ToUInt32(writes.Single(w => w.Rva == 0x27f017).Bytes));
        Assert.Equal(new byte[]{140}, writes.Single(w => w.Rva == 0x25b6db).Bytes);
    }

    [Theory]
    [InlineData(0x25b62f)] [InlineData(0x27cba1)] [InlineData(0x27cbc0)]
    [InlineData(0x27efb5)] [InlineData(0x27f015)] [InlineData(0x25b6d9)]
    public void UnexpectedInstructionRefusesEntirePatchPlan(int corrupted)
    {
        Assert.Throws<InvalidDataException>(() => MenuWaterExpansion.Plan(0x400000, 0x20000000,
            (rva,length) => {var bytes=Read(rva,length); if(rva==corrupted)bytes[0]^=1;return bytes;}));
    }

    [Fact]
    public void AslrAdjustsExpectedOriginalOperands()
    {
        var writes = MenuWaterExpansion.Plan(0x500000, 0x20000000,
            (rva,length) => {var bytes=Read(rva,length);if(rva!=0x25b6d9)bytes[^2]+=0x10;return bytes;});
        Assert.Equal(6,writes.Count);
    }
    [Fact]
    public void PackOutsideFrameworkEflIndexIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => MenuWaterExpansion.ValidateEflRoot("C:/pack", ["C:/mod/efl/x"]));
    }
}
