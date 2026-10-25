namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private sealed class TextureFrameOwner(int slot, uint descriptor, uint work, uint renderer)
    {
        internal readonly int Slot = slot;
        internal readonly uint Descriptor = descriptor, Work = work, Renderer = renderer;
        internal readonly TextureFramePhase Phase = new();
        internal long Serial;
        internal uint NativeFrame;
    }
    private readonly Dictionary<uint, TextureFrameOwner> _textureFrameOwners = [];
    private readonly TextureFrameBlend?[] _textureFramePublished = new TextureFrameBlend?[8];
    private readonly bool[] _textureFrameAmbiguous = new bool[8];
    private long _textureFrameEpoch = -1, _textureFrameImage = -1;
    private bool _textureFramePhaseBound;
    private int _textureFrameAvailableMask;
    private long _textureFrameAvailableImage = -1, _textureFrameAvailableEpoch = -1;
    private readonly bool[] _textureFrameReported = new bool[TextureSlotCount];

    private void observe_texture_frame_epoch()
    {
        long epoch = RenderPassContext.HistoryEpoch;
        if (_textureFrameEpoch == epoch) return;
        System.Threading.Volatile.Write(ref _textureFrameAvailableMask, 0);
        _textureFrameOwners.Clear();
        Array.Clear(_textureFramePublished);
        Array.Clear(_textureFrameAmbiguous);
        _textureFrameImage = -1;
        _textureFrameEpoch = epoch;
    }

    private void invalidate_texture_frame(int slot)
    {
        lock (_textureUvLock)
        {
            System.Threading.Volatile.Write(ref _textureFrameAvailableMask, 0);
            foreach (uint key in _textureFrameOwners.Where(pair => pair.Value.Slot == slot).Select(pair => pair.Key).ToArray())
                _textureFrameOwners.Remove(key);
            Array.Clear(_textureFramePublished);
            if ((uint)slot < TextureSlotCount) _textureFrameReported[slot] = false;
            _textureFrameImage = -1;
        }
    }

    private void capture_texture_frame_phase(int slot, nint model, nint renderer, float scale)
    {
        Span<byte> text = stackalloc byte[64];
        bool target = false;
        if (MemoryProbe.TryCopy(model, text))
        {
            int end = text.IndexOf((byte)0);
            if (end >= 0) target = TextureBlendProbeState.IsTarget(System.Text.Encoding.ASCII.GetString(text[..end]));
        }
        capture_texture_frame_phase_data(FhUtil.ptr_at<byte>(TextureAnimationSlots + slot * TextureSlotStride), slot,
            (uint)(nuint)renderer, target, scale > 1 ? _hold_carry : 0);
    }

    private void capture_texture_frame_phase_data(byte* slot, int index, uint renderer, bool target, double fraction)
    {
        lock (_textureUvLock)
        {
            observe_texture_frame_epoch();
            if (!target || (uint)index >= TextureSlotCount || renderer == 0)
            {
                _textureFrameOwners.Remove(renderer);
                return;
            }
            bool qualified = try_texture_frame_record(slot, out int frame, out int counter, out int period, out int step);
            if (!_textureFrameReported[index])
            {
                _textureFrameReported[index] = true;
                uint address = *(uint*)(slot + 4);
                byte* observedWork = (byte*)(nuint)address;
                bool readable = address != 0 && MemoryProbe.IsReadable((nint)observedWork, 8 + TextureRecordStride);
                diagnostic_event("texture_frame_phase", new { slot = index, renderer, qualified,
                    work_count = readable ? *(int*)observedWork : -1,
                    selector = readable ? *(short*)(observedWork + 8 + 0x1c) : -1,
                    frame, counter, period, step, authored_fraction = fraction });
            }
            if (!qualified)
            {
                _textureFrameOwners.Remove(renderer);
                return;
            }
            uint descriptor = *(uint*)slot, work = *(uint*)(slot + 4);
            if (!_textureFrameOwners.TryGetValue(renderer, out var owner) || owner.Slot != index
                || owner.Descriptor != descriptor || owner.Work != work)
                _textureFrameOwners[renderer] = owner = new(index, descriptor, work, renderer);
            long serial = RenderPassContext.SimulationSerial;
            if (!owner.Phase.Capture(serial, frame, counter, period, step, fraction))
            {
                _textureFrameOwners.Remove(renderer);
                return;
            }
            owner.Serial = serial;
            owner.NativeFrame = (uint)frame;
        }
    }

    private static bool try_texture_frame_record(byte* slot, out int frame, out int counter, out int period, out int step)
    {
        frame = counter = period = step = 0;
        uint descriptorAddress = *(uint*)slot, workAddress = *(uint*)(slot + 4);
        if (descriptorAddress == 0 || workAddress == 0 || slot[0xc] != 2 || slot[0xd] != 1) return false;
        byte* descriptor = (byte*)(nuint)descriptorAddress;
        byte* work = (byte*)(nuint)workAddress;
        if (!MemoryProbe.IsReadable((nint)descriptor, 12) || !MemoryProbe.IsReadable((nint)work, 8 + TextureRecordStride)
            || *(int*)work != 1 || !MemoryProbe.IsReadable((nint)(work + 0xd08), 1) || work[0xd08] == 0xff) return false;
        byte* record = work + 8;
        short selector = *(short*)(record + 0x1c);
        if (selector is not (0 or 3 or 5)) return false;
        frame = *(int*)(record + 4);
        counter = *(short*)(record + 0x14);
        int hold = *(short*)(record + 0x16);
        step = *(sbyte*)(slot + 0xe);
        if ((uint)frame >= 8 || hold < 0 || step is < 0 or > 1) return false;
        int current = *(int*)(slot + 8);
        Span<int> sets = stackalloc int[] { 1, 2, 0x15, current };
        byte* sequence = null;
        for (int i = 0; i < sets.Length; i++)
        {
            int set = sets[i];
            if ((uint)set >= 32 || (i == 3 && (set == 1 || set == 2 || set == 0x15)) || (slot[0xf + set] & 2) != 0) continue;
            byte* entry = descriptor + *(int*)(descriptor + 8) + set * 8;
            if (!MemoryProbe.IsReadable((nint)(entry + 0x10), 6)) return false;
            short count = *(short*)(entry + 0x14);
            if (count == 0) continue;
            if (count != 8 || sequence != null) return false;
            sequence = descriptor + *(int*)(entry + 0x10);
        }
        if (sequence == null || !MemoryProbe.IsReadable((nint)sequence, 8 * 4)) return false;
        int duration = *(short*)(sequence + frame * 4);
        if (duration < 0) return false;
        // Selector 3 uses a strict comparison; its terminal integer counter is one tick later.
        period = duration + hold + (selector == 3 ? 1 : 0);
        return period > 0 && counter >= 0 && counter < period;
    }

    private void publish_texture_frame_phase(Func<TextureFrameOwner, bool> valid)
    {
        lock (_textureUvLock)
        {
            observe_texture_frame_epoch();
            long image = RenderPassContext.ImageSerial;
            if (_textureFrameImage == image) return;
            System.Threading.Volatile.Write(ref _textureFrameAvailableMask, 0);
            _textureFrameImage = image;
            Array.Clear(_textureFramePublished);
            Array.Clear(_textureFrameAmbiguous);
            foreach (var pair in _textureFrameOwners.ToArray())
            {
                var owner = pair.Value;
                if (owner.Serial != RenderPassContext.SimulationSerial || !valid(owner))
                {
                    _textureFrameOwners.Remove(pair.Key);
                    continue;
                }
                var blend = owner.Phase.Render(RenderPassContext.PresentationAlpha, RenderPassContext.IsActive);
                uint frame = owner.NativeFrame;
                if (_textureFramePublished[frame] is { } previous && previous != blend) _textureFrameAmbiguous[frame] = true;
                _textureFramePublished[frame] = blend;
            }
            int available = 0;
            for (int frame = 0; frame < 8; frame++)
                if (_textureFramePublished[frame].HasValue && !_textureFrameAmbiguous[frame]) available |= 1 << frame;
            System.Threading.Volatile.Write(ref _textureFrameAvailableImage, image);
            System.Threading.Volatile.Write(ref _textureFrameAvailableEpoch, _textureFrameEpoch);
            System.Threading.Volatile.Write(ref _textureFrameAvailableMask, available);
        }
    }

    private bool texture_frame_blend_available()
        => _config.TextureFrameInterpolation && System.Threading.Volatile.Read(ref _textureFrameAvailableMask) != 0
            && System.Threading.Volatile.Read(ref _textureFrameAvailableImage) == RenderPassContext.ImageSerial
            && System.Threading.Volatile.Read(ref _textureFrameAvailableEpoch) == RenderPassContext.HistoryEpoch;

    private bool try_texture_frame_blend(uint nativeFrame, out uint firstFrame, out uint nextFrame, out float alpha)
    {
        firstFrame = nextFrame = 0;
        alpha = 0;
        lock (_textureUvLock)
        {
            observe_texture_frame_epoch();
            if (!_config.TextureFrameInterpolation || nativeFrame >= 8 || _textureFrameImage != RenderPassContext.ImageSerial
                || _textureFrameAmbiguous[nativeFrame] || _textureFramePublished[nativeFrame] is not { } blend) return false;
            firstFrame = blend.First;
            nextFrame = blend.Next;
            alpha = blend.Alpha;
            return true;
        }
    }

    private static bool texture_frame_owner_live(TextureFrameOwner owner)
    {
        byte* slot = FhUtil.ptr_at<byte>(TextureAnimationSlots + owner.Slot * TextureSlotStride);
        if (*(uint*)slot != owner.Descriptor || *(uint*)(slot + 4) != owner.Work
            || !try_texture_frame_record(slot, out int frame, out _, out _, out _) || frame != owner.NativeFrame) return false;
        int count = FhUtil.get_at<int>(0x1fc44e0);
        byte* actors = (byte*)(nuint)FhUtil.get_at<uint>(0x1fc44e4);
        return texture_uv_actor_matches(actors, count, new(owner.Slot, 0, 0, owner.Descriptor, owner.Work, owner.Renderer, 0));
    }
}
