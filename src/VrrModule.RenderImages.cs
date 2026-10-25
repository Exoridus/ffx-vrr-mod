using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint RenderImageFrame = 0x227940;
    private const nint RenderImageFlip = 0x1b6af0;
    private const nint RenderImageSlot = 0x214c00;
    private const nint RenderImageAction = 0x193760;
    private readonly ThreadLocal<RenderImageFrameScope?> _renderImageFrame = new();
    private readonly ThreadLocal<RenderImageReservation?> _renderImageReservation = new();
    private readonly ThreadLocal<RenderImageActionScope?> _renderImageAction = new();
    private readonly object _renderImageQueueLock = new();
    private readonly Queue<RenderImageReservation> _renderImageQueue = new();
    private bool _renderImagesInstalled;
    private int _renderImageProducerThread, _renderImageConsumerThread, _renderImageEverAdmitted;
    private int _renderImageAssociationInvalid;

    private sealed class RenderImageFrameScope(nint application, PresentationAdmission.Image image, bool valid)
    {
        internal readonly nint Application = application;
        internal readonly PresentationAdmission.Image Image = image;
        internal readonly bool Valid = valid;
        internal bool Animation, AnimationComplete, Admitted, OrdinaryComplete;
        internal long AdmissionEpoch;
        internal int OrdinaryVersion;
    }

    private enum RenderImageReservationState { Created, Published, Consumed, Cancelled }
    private sealed class RenderImageReservation(PresentationAdmission.Image image, nint application,
        bool independentAdmitted, bool nativeEligibilityAtFlip, long admissionEpoch, bool ordinaryComplete)
    {
        internal readonly PresentationAdmission.Image Image = image;
        internal readonly nint Application = application;
        internal readonly bool IndependentAdmittedSnapshot = independentAdmitted;
        internal readonly bool NativeEligibilityAtFlip = nativeEligibilityAtFlip;
        internal readonly long AdmissionEpoch = admissionEpoch;
        internal readonly bool OrdinaryComplete = ordinaryComplete;
        internal RenderImageReservationState State;
        internal bool SlotRequested;
    }
    private sealed class RenderImageActionScope(PresentationAdmission.Image? image, nint application,
        bool independentAdmitted, bool nativeEligibilityAtFlip, long admissionEpoch, bool ordinaryComplete)
    {
        internal readonly PresentationAdmission.Image? Image = image;
        internal readonly nint Application = application;
        internal readonly bool IndependentAdmittedSnapshot = independentAdmitted;
        internal readonly bool NativeEligibilityAtFlip = nativeEligibilityAtFlip;
        internal readonly long AdmissionEpoch = admissionEpoch;
        internal readonly bool OrdinaryComplete = ordinaryComplete;
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint d_render_image_native(nint self);

    private bool init_render_images()
    {
        if (!_config.VrrEnabled) return true;
        if (IntPtr.Size != 4 || !NativeBuild.TryValidate(out _))
        {
            _logger.Error("[Vrr] Image submission binding refused: unsupported executable or pointer width.");
            return false;
        }
        bool valid = graphics_lifetime_bindings_valid() && render_image_prefix(RenderImageFrame, [0x55, 0x8b, 0xec, 0x6a, 0xff])
            && render_image_prefix(RenderImageFlip, [0x55, 0x8b, 0xec, 0x83, 0xec, 8, 0x56, 0x8b, 0xf1, 0xf6, 0x46, 4, 1])
            && render_image_prefix(RenderImageSlot, [0x81, 0xc1, 0x64, 1, 0, 0, 0xe8, 0xa5, 0xb6, 0xe1, 0xff, 0x33, 0xc0, 0xc3])
            && render_image_prefix(RenderImageAction, [0x56, 0x8b, 0xf1, 0x8b, 0x56, 0x74, 0x85, 0xd2, 0x75, 4,
                0x32, 0xc0, 0x5e, 0xc3, 0x8b, 0x0a, 0x33, 0xc0, 0x38, 0x86, 0x1c, 0x13, 0, 0, 0x6a, 0,
                0x0f, 0x95, 0xc0, 0x50, 0x52, 0xff, 0x51, 0x20, 0xb0, 1, 0x5e, 0xc3])
            && render_image_prefix(0x21516e, [0x6a, 1, 0x6a, 1, 0x8d, 0x8b, 0x64, 1, 0, 0, 0xe8, 3, 0xaf, 0xe1, 0xff])
            && render_image_prefix(0x214d86, [0x8b, 0x4d, 8, 0xe8, 0xd2, 0xe9, 0xf7, 0xff, 0x6a, 0, 0x6a, 1,
                0x8d, 0x8e, 0x64, 1, 0, 0, 0xe8, 0x73, 0xb4, 0xe1, 0xff, 0x5e, 0x5d, 0xc2, 0x0c, 0])
            && render_image_pointer_is((nint)FhUtil.ptr_at<byte>(0x70d998 + 0x10), (nint)FhUtil.ptr_at<byte>(0x2f520))
            && render_image_pointer_is((nint)FhUtil.ptr_at<byte>(0x70d998 + 0x1c), (nint)FhUtil.ptr_at<byte>(0x2f770))
            && render_image_pointer_is((nint)FhUtil.ptr_at<byte>(0x70d998 + 0x28), (nint)FhUtil.ptr_at<byte>(0x2f4f0))
            && render_image_pointer_is((nint)FhUtil.ptr_at<byte>(0x7363f8 + 8), (nint)FhUtil.ptr_at<byte>(0x1b6c50));
        if (!valid)
        {
            _logger.Error("[Vrr] Image submission binding refused: frame/flip/slot/action contract mismatch.");
            return false;
        }
        bool ok = hook_or_log("PApplication frame", RenderImageFrame,
            () => new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageFrame, 0)).hook(this, h_render_image_frame));
        ok &= hook_or_log("renderer flip identity", RenderImageFlip,
            () => new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageFlip, 0)).hook(this, h_render_image_flip));
        ok &= hook_or_log("render flip slot", RenderImageSlot,
            () => new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageSlot, 0)).hook(this, h_render_image_slot));
        ok &= hook_or_log("render flip action", RenderImageAction,
            () => new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageAction, 0)).hook(this, h_render_image_action));
        ok &= init_graphics_lifetime_hooks();
        _renderImagesInstalled = ok;
        return ok;
    }

    private static bool render_image_prefix(nint rva, ReadOnlySpan<byte> expected)
    {
        Span<byte> current = stackalloc byte[expected.Length];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(rva), current) && current.SequenceEqual(expected);
    }

    private static bool render_image_pointer_is(nint address, nint expected)
        => MemoryProbe.IsReadable(address, sizeof(nint)) && *(nint*)address == expected;

    private bool render_image_structural_ready()
        => _renderImagesInstalled && _presenter_active && Volatile.Read(ref _renderImageAssociationInvalid) == 0
            && _presentationAdmission.Failure is null
            && _renderImageFrame.Value is { Valid: true, Animation: true };

    private static bool render_image_native_eligible(nint application)
    {
        Span<byte> initialized = stackalloc byte[sizeof(uint)];
        Span<byte> loading = stackalloc byte[1];
        return MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(0x8cb45c), initialized)
            && MemoryMarshal.Read<uint>(initialized) == 1
            && MemoryProbe.TryCopy(application + 0x3ad, loading) && loading[0] == 0;
    }

    private bool render_image_admission_ready()
    {
        if (!render_image_structural_ready()) return false;
        RenderImageFrameScope frame = _renderImageFrame.Value!;
        long epoch;
        lock (_graphicsLifetimeGate) epoch = _graphicsEpoch;
        if (!render_image_native_eligible(frame.Application)) return false;
        lock (_graphicsLifetimeGate)
            return graphics_epoch_ready(epoch) && _graphicsReadyThread == Environment.CurrentManagedThreadId
                && (!frame.Admitted || frame.AdmissionEpoch == epoch) && render_image_structural_ready();
    }

    private bool render_image_admission_begin()
    {
        if (!render_image_structural_ready()) return false;
        RenderImageFrameScope frame = _renderImageFrame.Value!;
        long epoch;
        lock (_graphicsLifetimeGate) epoch = _graphicsEpoch;
        if (!render_image_native_eligible(frame.Application)) return false;
        lock (_graphicsLifetimeGate)
        {
            if (!graphics_epoch_ready(epoch) || _graphicsReadyThread != Environment.CurrentManagedThreadId
                || !render_image_structural_ready() || (frame.Admitted && frame.AdmissionEpoch != epoch)) return false;
            if (!frame.Admitted)
            {
                frame.AdmissionEpoch = epoch;
                frame.Admitted = true;
                Volatile.Write(ref _renderImageEverAdmitted, 1);
            }
            return true;
        }
    }

    private void render_image_animation_end(nint application, bool normal)
    {
        RenderImageFrameScope? frame = _renderImageFrame.Value;
        if (frame is { Valid: true, Animation: true } && frame.Application == application)
            frame.AnimationComplete = normal;
    }

    private void render_image_animation_begin(nint application)
    {
        RenderImageFrameScope? frame = _renderImageFrame.Value;
        if (frame is { Valid: true } && frame.Application == application)
        {
            frame.Animation = true;
            frame.AnimationComplete = frame.OrdinaryComplete = false;
            frame.OrdinaryVersion++;
            return;
        }
        if (frame is not null) frame.Animation = false;
        if (_presenter_active && RenderSchedulerReady)
            refuse_independent_presentation("render_image_animation_owner");
    }

    private void render_image_refuse_association(string reason, Exception? error = null)
    {
        // A shifted queue cannot establish the identity of later native images.
        if (Interlocked.Exchange(ref _renderImageAssociationInvalid, 1) == 0)
            _logger.Error($"[Vrr] Native image association invalid ({reason}); owned image output refused until restart.");
        refuse_independent_presentation(reason, error);
    }
    private bool render_image_rejected_for_present(uint flags)
    {
        if ((flags & 1) != 0) return false;
        if (Volatile.Read(ref _renderImageAssociationInvalid) != 0) return true;
        RenderImageActionScope? scope = _renderImageAction.Value;
        if (scope is not null)
        {
            if (scope.Image is null) return true;
            if (!scope.IndependentAdmittedSnapshot)
                return Volatile.Read(ref _renderImageAssociationInvalid) != 0 || scope.Image.IsRejected;
            long epoch;
            lock (_graphicsLifetimeGate) epoch = _graphicsEpoch;
            bool eligible = render_image_native_eligible(scope.Application);
            lock (_graphicsLifetimeGate)
                return !scope.NativeEligibilityAtFlip || !scope.OrdinaryComplete || !eligible
                    || !graphics_epoch_ready(epoch) || scope.AdmissionEpoch != epoch
                    || Volatile.Read(ref _renderImageAssociationInvalid) != 0 || scope.Image.IsRejected;
        }
        if (Volatile.Read(ref _renderImageEverAdmitted) == 0) return Volatile.Read(ref _renderImageAssociationInvalid) != 0;
        render_image_refuse_association("render_image_missing_action");
        return true;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_image_frame(nint application)
    {
        RenderImageFrameScope? previous = _renderImageFrame.Value;
        bool valid = render_image_pointer_is(application, (nint)FhUtil.ptr_at<byte>(0x70d998))
            && render_image_pointer_is(application + 0x124, (nint)FhUtil.ptr_at<byte>(0x7363f8));
        bool eligible = _presenter_active && RenderSchedulerReady;
        if (valid)
        {
            int thread = Environment.CurrentManagedThreadId;
            Interlocked.CompareExchange(ref _renderImageProducerThread, thread, 0);
            if (Volatile.Read(ref _renderImageProducerThread) != thread)
            {
                valid = false;
                if (eligible) refuse_independent_presentation("render_image_foreign_frame_thread");
            }
        }
        if (previous is not null)
        {
            previous.OrdinaryComplete = false;
            previous.OrdinaryVersion++;
            if (eligible) refuse_independent_presentation("render_image_nested_frame");
        }
        _renderImageFrame.Value = new(application, _presentationAdmission.BeginImage(), valid);
        try
        {
            return new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageFrame, 0))
                .chain_from(h_render_image_frame).fnptr!(application);
        }
        catch (Exception error)
        {
            refuse_independent_presentation("render_image_frame_exception", error);
            throw;
        }
        finally { _renderImageFrame.Value = previous; }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_image_flip(nint renderer)
    {
        RenderImageReservation? previous = _renderImageReservation.Value;
        if (previous is not null) render_image_refuse_association("render_image_nested_flip");
        RenderImageFrameScope? frame = _renderImageFrame.Value;
        if (frame is { Valid: true } && renderer != frame.Application + 0x124
            && _presenter_active && RenderSchedulerReady)
            refuse_independent_presentation("render_image_foreign_renderer");
        bool matchingFrame = frame is { Valid: true } && renderer == frame.Application + 0x124;
        PresentationAdmission.Image image = matchingFrame ? frame!.Image : _presentationAdmission.BeginImage();
        nint application = matchingFrame ? frame!.Application : 0;
        bool admitted = matchingFrame && frame!.Admitted;
        bool nativeEligible = matchingFrame && render_image_native_eligible(application);
        var reservation = new RenderImageReservation(image, application, admitted, nativeEligible, frame?.AdmissionEpoch ?? 0, matchingFrame && frame!.OrdinaryComplete);
        _renderImageReservation.Value = reservation;
        try
        {
            uint result = new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageFlip, 0))
                .chain_from(h_render_image_flip).fnptr!(renderer);
            bool invalid;
            lock (_renderImageQueueLock)
            {
                invalid = result == 0 ? reservation.State is not (RenderImageReservationState.Published or RenderImageReservationState.Consumed)
                    : reservation.State != RenderImageReservationState.Created;
                if (result != 0 && reservation.State != RenderImageReservationState.Consumed)
                    reservation.State = RenderImageReservationState.Cancelled;
            }
            if (invalid) render_image_refuse_association(result == 0 ? "render_image_missing_slot" : "render_image_flip_failed_after_slot");
            return result;
        }
        catch (Exception error)
        {
            lock (_renderImageQueueLock)
                if (reservation.State != RenderImageReservationState.Consumed) reservation.State = RenderImageReservationState.Cancelled;
            render_image_refuse_association("render_image_flip_exception", error);
            throw;
        }
        finally { _renderImageReservation.Value = previous; }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_image_slot(nint thread)
    {
        RenderImageReservation? reservation = _renderImageReservation.Value;
        bool publish;
        lock (_renderImageQueueLock)
        {
            publish = reservation is { State: RenderImageReservationState.Created, SlotRequested: false }
                && thread == (nint)FhUtil.ptr_at<byte>(0x8c96f8);
            if (reservation is not null) reservation.SlotRequested = true;
        }
        if (!publish) render_image_refuse_association("render_image_unmatched_slot");
        uint result = new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageSlot, 0))
            .chain_from(h_render_image_slot).fnptr!(thread);
        if (result != 0) render_image_refuse_association("render_image_slot_result");
        // The native single credit orders publication before this flip's command can be appended.
        if (publish)
        {
            lock (_renderImageQueueLock)
            {
                if (reservation!.State == RenderImageReservationState.Created)
                {
                    reservation.State = RenderImageReservationState.Published;
                    _renderImageQueue.Enqueue(reservation);
                }
                else publish = false;
            }
            if (!publish) render_image_refuse_association("render_image_duplicate_publication");
        }
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_image_action(nint context)
    {
        RenderImageActionScope? previous = _renderImageAction.Value;
        int thread = Environment.CurrentManagedThreadId;
        Interlocked.CompareExchange(ref _renderImageConsumerThread, thread, 0);
        bool owned = Volatile.Read(ref _renderImageConsumerThread) == thread && previous is null;
        if (!owned) render_image_refuse_association("render_image_action_owner");
        RenderImageReservation? consumed = null;
        lock (_renderImageQueueLock)
        {
            while (_renderImageQueue.TryPeek(out var cancelled) && cancelled.State == RenderImageReservationState.Cancelled)
                _renderImageQueue.Dequeue();
            if (_renderImageQueue.TryDequeue(out var reservation) && reservation.State == RenderImageReservationState.Published)
            {
                reservation.State = RenderImageReservationState.Consumed;
                if (owned) consumed = reservation;
            }
        }
        if (consumed is null) render_image_refuse_association("render_image_missing_reservation");
        _renderImageAction.Value = new(consumed?.Image, consumed?.Application ?? 0,
            consumed?.IndependentAdmittedSnapshot ?? false, consumed?.NativeEligibilityAtFlip ?? false,
            consumed?.AdmissionEpoch ?? 0, consumed?.OrdinaryComplete ?? false);
        try
        {
            // AL is the native boolean result; preserve the original EAX carrier unchanged.
            return new FhCallHandle<d_render_image_native>(new FhCallLocation(RenderImageAction, 0))
                .chain_from(h_render_image_action).fnptr!(context);
        }
        catch (Exception error)
        {
            render_image_refuse_association("render_image_action_exception", error);
            throw;
        }
        finally { _renderImageAction.Value = previous; }
    }
}
