using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class CharacterTextureClockTests
{
    [Fact]
    public void Disabled_correction_leaves_the_native_operand_untouched()
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        typeof(VrrModule).GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(module, new VrrConfig { TextureAnimationStep = false });
        Assert.True(module.InitializeTextureClockForTest());
        Assert.Empty(module._patches.Values);
        Assert.Equal((nint)0, module.TextureShadowForTest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_native_step_operand_is_the_only_read_redirected(bool foreignOperand)
    {
        using var fixture = new TimingTests();
        var module = new VrrModule();
        Convert.FromBase64String("VYvsgeyEAAAAodgTxgAzxYlF/ItFCItNDItVEFNWjTQ=").CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x37f360), 32));
        typeof(VrrModule).GetField("_config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(module, new VrrConfig { TextureFrameInterpolation = false });
        byte* step = FhUtil.ptr_at<byte>(0x37f3e3);
        step[0] = 0x0f; step[1] = 0xbe; step[2] = 0x86;
        uint expected = foreignOperand ? 0x12345678u : (uint)(nuint)FhUtil.ptr_at<byte>(0x1f1000e);
        *(uint*)(step + 3) = expected;
        Assert.Equal(!foreignOperand, module.InitializeTextureClockForTest());
        if (foreignOperand)
        {
            Assert.Empty(module._patches.Values);
            Assert.Equal(expected, *(uint*)(step + 3));
        }
        else
        {
            Assert.Single(module._patches.Values);
            Assert.Equal((uint)(nuint)(module.TextureShadowForTest + 0xe), *(uint*)(step + 3));
        }
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(2, true, 2)]
    [InlineData(0, false, 0)]
    public void Shadow_step_preserves_the_engine_timer(int nativeStep, bool advance, int expectedStep)
    {
        using var data = new TextureClockMemory();
        data.Slot[0xe] = (byte)nativeStep;
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, advance, step =>
        {
            Assert.Equal(expectedStep, step);
            Assert.Equal(nativeStep, data.Slot[0xe]);
        });
        Assert.Equal(nativeStep, data.Slot[0xe]);
    }

    [Fact]
    public void Concurrent_pause_is_preserved_and_seen_on_the_next_call()
    {
        using var data = new TextureClockMemory();
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, true, step =>
        {
            Assert.Equal(1, step);
            data.Slot[0xe] = 0;
        });
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, true, step => Assert.Equal(0, step));
        Assert.Equal(0, data.Slot[0xe]);
    }

    [Theory]
    [InlineData(0.5f, 1f, -3f, 9.5f, 8f)]
    [InlineData(0f, 1f, -3f, 1f, 1f)]
    [InlineData(1f, 1f, -3f, 8f, 5f)]
    public void Private_rates_retime_every_intermediate_upload_and_preserve_native_wrap(float elapsed, float start, float delta, float first, float second)
    {
        using var data = new TextureClockMemory();
        data.Record[0x1c] = 3;
        *(float*)(data.Record + 0x24) = delta;
        *(float*)(data.Record + 0x2c) = start;
        int calls = 0;
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, elapsed, false, _ =>
        {
            calls++;
            Assert.Equal(first, data.AdvanceScroll());
            Assert.Equal(second, data.AdvanceScroll());
        });
        Assert.Equal(1, calls);
        Assert.Equal(delta, *(float*)(data.Record + 0x24));
    }

    [Fact]
    public void Shared_descriptor_and_other_instance_rates_are_untouched()
    {
        using var data = new TextureClockMemory();
        data.Record[0x1c] = 3;
        *(float*)(data.Record + 0x24) = 8;
        *(float*)(data.OtherRecord + 0x24) = 11;
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, false, _ =>
        {
            Assert.Equal(4, *(float*)(data.Record + 0x24));
            Assert.Equal(11, *(float*)(data.OtherRecord + 0x24));
            Assert.Equal(12345u, *(uint*)(data.Descriptor + 0x20));
        });
        Assert.Equal(8, *(float*)(data.Record + 0x24));
    }

    [Fact]
    public void Changed_native_rate_is_not_restored_over()
    {
        using var data = new TextureClockMemory();
        data.Record[0x1c] = 3;
        *(float*)(data.Record + 0x24) = 8;
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, false,
            _ => *(float*)(data.Record + 0x24) = 7);
        Assert.Equal(7, *(float*)(data.Record + 0x24));
    }

    [Fact]
    public void Reused_slot_never_restores_a_previous_work_buffer()
    {
        using var data = new TextureClockMemory();
        data.Record[0x1c] = 3;
        *(float*)(data.Record + 0x24) = 8;
        *(float*)(data.OtherRecord + 0x24) = 11;
        VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, false,
            _ => *(uint*)(data.Slot + 4) = (uint)(nuint)(data.OtherRecord - 8));
        Assert.Equal(4, *(float*)(data.Record + 0x24));
        Assert.Equal(11, *(float*)(data.OtherRecord + 0x24));
    }

    [Fact]
    public void Exception_restores_owned_private_rates()
    {
        using var data = new TextureClockMemory();
        data.Record[0x1c] = 3;
        *(float*)(data.Record + 0x24) = 8;
        Assert.Throws<InvalidOperationException>(() => VrrModule.RunTextureClockForTest(data.Slot, data.Shadow, 0.5f, false,
            _ => throw new InvalidOperationException()));
        Assert.Equal(8, *(float*)(data.Record + 0x24));
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool InitializeTextureClockForTest() => init_texture_animation_hook();
    internal nint TextureShadowForTest => (nint)_texture_step_shadow;
    internal static void RunTextureClockForTest(byte* slot, byte* shadow, float elapsed, bool advance, Action<sbyte> original)
        => with_texture_animation_clock(slot, shadow, elapsed, advance, (_, _, _) => original(*(sbyte*)shadow), 0, 0, 0);
}

internal unsafe sealed class TextureClockMemory : IDisposable
{
    private readonly byte* _memory;
    internal byte* Slot => _memory;
    internal byte* Descriptor => _memory + 0x100;
    internal byte* Record => _memory + 0x1008;
    internal byte* OtherRecord => _memory + 0x3008;
    internal byte* Shadow => _memory + 0x6000;

    internal TextureClockMemory()
    {
        for (nuint address = 0x10000000; address < 0x70000000 && _memory == null; address += 0x10000)
            _memory = (byte*)VirtualAlloc((nint)address, 0x10000, 0x3000, 4);
        if (_memory == null) throw new InvalidOperationException("A 32-bit native pointer fixture could not be allocated.");
        *(uint*)Slot = (uint)(nuint)Descriptor;
        *(uint*)(Slot + 4) = (uint)(nuint)(Record - 8);
        Slot[0xc] = 2;
        Slot[0xd] = 1;
        Slot[0xe] = 1;
        Descriptor[0] = 2;
        *(uint*)(Descriptor + 0x20) = 12345;
        *(int*)(Record - 8) = 1;
    }

    internal float AdvanceScroll()
    {
        float value = *(float*)(Record + 0x2c) + *(float*)(Record + 0x24);
        if (value < 0) value += 10;
        if (value >= 10) value -= 10;
        *(float*)(Record + 0x2c) = value;
        return value;
    }

    public void Dispose() => VirtualFree((nint)_memory, 0, 0x8000);
    [DllImport("kernel32.dll")] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
}
