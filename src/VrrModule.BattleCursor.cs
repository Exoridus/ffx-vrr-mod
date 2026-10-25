namespace Fahrenheit.Mods.Vrr;

/// <summary>
/// Retimes the multi-target cursor blink without changing the shared main-loop counter.
/// Native-owned scenes retain the scaled counter-bit test. Independent presentation
/// redirects only this instruction's read to a private phase advanced at 30 ticks per
/// second from image elapsed time, preserving a 15 Hz blink at variable image cadence.
/// The command window and its single-target cursor always retain their native draw path.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     <c>F6 05 &lt;sg_count&gt; imm8</c>. The two opcode bytes are a build-time constant, but the
    ///     four-byte address operand carries a base relocation like every other absolute reference in
    ///     the image, so it is resolved and compared at runtime against
    ///     <see cref="EngineAddresses.SgCount"/> rather than against the address the linker baked in
    ///     here.
    /// </summary>
    private static ReadOnlySpan<byte> BlinkGateOpcode => [0xF6, 0x05];
    private const int BlinkGateOperandOffset = 2;
    private const int BlinkMaskOffset = 6;
    private const byte VanillaBlinkMask = 0x01;

    private int _blink_mask;
    private byte* _blink_render_phase;
    private double _blink_render_carry;
    private bool _blink_render_owned;
    private uint _blink_operand;

    /// <summary>
    ///     Updates the private clock once per image from <c>apply_rate_patches</c>. Native image patches change
    ///     only when the mask or clock owner changes. Native-owned scenes still follow Scale when
    ///     recorded syncdata pacing starts or ends.
    /// </summary>
    private void patch_battle_cursor_blink()
    {
        if (!_config.BattleCursorBlink) return;

        // The mask has to be a single bit, so the scale is rounded in log space rather than
        // arithmetically: a 90 Hz scale of 3 asks for a period of six frames that no single bit can
        // express, and bit 2 (eight frames) is closer to it than bit 1 (four).
        int exponent = (int)Math.Round(Math.Log2(Math.Max(1f, Scale)));
        int nativeMask = 1 << Math.Clamp(exponent, 0, 7);
        uint nativeOperand = (uint)(nuint)FhUtil.ptr_at<byte>(EngineAddresses.SgCount);
        bool renderClock = RenderPassContext.IsActive;
        if (renderClock)
        {
            // Keep storage alive until the journal has restored every instruction operand.
            if (_blink_render_phase == null) _blink_render_phase = (byte*)NativeMemory.AllocZeroed(1);
            if (!_blink_render_owned)
            {
                _blink_render_carry = 0;
                *_blink_render_phase = (byte)((FhUtil.get_at<uint>(EngineAddresses.SgCount) & nativeMask) != 0 ? 1 : 0);
            }
            _blink_render_carry += RenderPassContext.ImageSeconds * 30;
            int ticks = (int)Math.Floor(_blink_render_carry + 1e-9);
            _blink_render_carry = Math.Max(0, _blink_render_carry - ticks);
            *_blink_render_phase = unchecked((byte)(*_blink_render_phase + ticks));
        }
        _blink_render_owned = renderClock;
        int mask = renderClock ? 1 : nativeMask;
        uint operand = renderClock ? (uint)(nuint)_blink_render_phase : nativeOperand;

        if (mask == _blink_mask && operand == _blink_operand) return;

        // Recorded before the verification rather than after it. This field is the only thing that
        // stops the derived mask being re-checked, and the function is asked once per presented
        // frame - so leaving it unset on a mismatch would log the same error every frame.
        _blink_mask = mask;
        _blink_operand = operand;

        byte* site = FhUtil.ptr_at<byte>(EngineAddresses.BattleCursorBlinkGate);
        byte current = site[BlinkMaskOffset];

        // The operand is resolved at runtime rather than compared against the linked constant,
        // because it carries a base relocation like every other absolute reference in the image and
        // holds a different value on every load that is not at the preferred base.
        uint expectedOperand = nativeOperand;
        uint actualOperand   = *(uint*)(site + BlinkGateOperandOffset);

        // Either untouched or carrying a mask this module wrote earlier; a mask with more than one
        // bit set is not the instruction we think it is.
        bool matches = site[0] == BlinkGateOpcode[0] && site[1] == BlinkGateOpcode[1]
                    && (actualOperand == expectedOperand || (_blink_render_phase != null && actualOperand == (uint)(nuint)_blink_render_phase))
                    && current != 0 && (current & (current - 1)) == 0;

        if (!matches)
        {
            _logger.Error($"[Vrr] Battle cursor blink gate at RVA 0x{EngineAddresses.BattleCursorBlinkGate:X} " +
                          $"does not carry the expected instruction; nothing written. Expected " +
                          $"{BlinkGateOpcode[0]:X2} {BlinkGateOpcode[1]:X2} operand 0x{expectedOperand:X8}, found " +
                          $"{site[0]:X2} {site[1]:X2} operand 0x{actualOperand:X8} mask=0x{current:X2}.");
            return;
        }

        if (actualOperand != operand)
            _patches.Write(EngineAddresses.BattleCursorBlinkGate + BlinkGateOperandOffset, BitConverter.GetBytes(operand));
        _patches.Write(EngineAddresses.BattleCursorBlinkGate + BlinkMaskOffset, [(byte)mask]);
        _logger.Info($"[Vrr] Battle cursor blink mask set to 0x{mask:X2}, a period of {mask * 2} " +
                     (renderClock ? "render-clock ticks." : "native main-loop passes."));
    }
}
