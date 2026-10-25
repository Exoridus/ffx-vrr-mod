using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private readonly object _fieldPresentationLock = new();
    private readonly Dictionary<nint, FieldParticlePresentationPlan> _fieldPresentationPlans = [];
    private readonly Dictionary<nint, long> _fieldPresentationGenerations = [];
    private readonly Dictionary<nint, nint> _fieldPresentationObjects = [];
    private readonly Dictionary<nint, FieldParticleKernel> _fieldPresentationKernels = [];
    private readonly Dictionary<nint, Lazy<d_pilot_kernel>> _fieldPresentationProducers = [];
    private d_field_dispatch? _fieldDispatchOriginal;
    private Lazy<d_field_geometry>? _fieldGeometry;
    private Lazy<d_field_backend>? _fieldBackend;
    private Lazy<d_field_level>? _fieldLevel;
    private bool _fieldPresentationInstalled, _fieldPauseInstalled;
    private readonly FieldParticleDeferral _fieldDeferral = new();
    private nint _fieldPresentationTable;
    private int _fieldPresentationFailures;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_field_dispatch(nint manager);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_field_geometry(nint obj, nint data, nint descriptor, nint work);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint d_field_backend(int index, nint obj);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int d_field_level();

    private bool preflight_field_particle_presentation()
    {
        foreach (nint target in new nint[] { 0x317010, 0x341b90, 0x342cc0, 0x334170, 0x334bd0, 0x340a70, 0x340f60, 0x2453d0 })
        {
            ReadOnlySpan<byte> prefix = target is 0x341b90 or 0x340a70 or 0x340f60 ? [0x53, 0x8b, 0xdc] : [0x55, 0x8b, 0xec];
            byte* entry = FhUtil.ptr_at<byte>(target);
            if (!MemoryProbe.IsReadable((nint)entry, prefix.Length) || !new ReadOnlySpan<byte>(entry, prefix.Length).SequenceEqual(prefix)) return false;
        }
        return true;
    }

    private bool init_field_particle_presentation()
    {
        if (!preflight_field_particle_presentation()) return false;
        nint Address(nint rva) => (nint)FhUtil.ptr_at<byte>(rva);
        void Add(int opcode, nint update, nint draw, nint emitter, FieldParticleProducer kind, nint target, int bytes)
        {
            _fieldPresentationKernels[Address(0x83a508) + opcode * 40] = new(
                update == 0 ? 0 : Address(update), draw == 0 ? 0 : Address(draw), emitter == 0 ? 0 : Address(emitter), kind, target, bytes);
            if (kind is not (FieldParticleProducer.None or FieldParticleProducer.ShapeField))
                _fieldPresentationProducers.TryAdd(target, new(() => new FhCallHandle<d_pilot_kernel>(new FhCallLocation(target, 0)).fnptr!));
        }
        Add(16, 0x32d390, 0, 0, FieldParticleProducer.Matrix, 0x32d390, 4);
        Add(17, 0x334170, 0, 0, FieldParticleProducer.Matrix, 0x334170, 4);
        Add(20, 0, 0x334bd0, 0, FieldParticleProducer.Matrix, 0x334bd0, 4);
        Add(24, 0, 0, 0x3409e0, FieldParticleProducer.Shape, 0x340a70, 12);
        Add(81, 0, 0, 0x340ed0, FieldParticleProducer.ShapeX, 0x340f60, 16);
        Add(49, 0, 0x342cc0, 0, FieldParticleProducer.ShapeField, 0x341b90, 0x100);
        foreach (var (opcode, update) in new (int, nint)[] {
            (3,0x35ba40),(7,0x35c390),(8,0x35c450),(11,0x35d1f0),(10,0x35cfe0),
            (4,0x35bde0),(5,0x35bef0),(9,0x35ce30),(29,0x330ce0),(13,0x331100),
            (14,0x3325f0),(105,0x35c500),(106,0x35cec0),(107,0x35d090) })
            Add(opcode, update, 0, 0, FieldParticleProducer.None, 0, 4);
        _fieldGeometry = new(() => new FhCallHandle<d_field_geometry>(new FhCallLocation(0x341b90, 0)).fnptr!);
        _fieldBackend = new(() => new FhCallHandle<d_field_backend>(new FhCallLocation(0x2453d0, 0)).fnptr!);
        _fieldLevel = new(() => new FhCallHandle<d_field_level>(new FhCallLocation(0x241da0, 0)).fnptr!);
        _fieldPresentationInstalled = hook_or_log("field particle producer dispatcher", 0x317010,
            () => new FhCallHandle<d_field_dispatch>(new FhCallLocation(0x317010, 0)).hook(this, h_field_particle_dispatch));
        return _fieldPresentationInstalled;
    }

    private bool is_field_particle_manager(nint manager)
    {
        nint first = (nint)FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        long offset = manager - first;
        return offset >= 0 && offset % ParticleManagerStride == 0 && offset / ParticleManagerStride < field_manager_count();
    }

    private long field_particle_generation(nint manager) => _fieldPresentationGenerations.GetValueOrDefault(manager);

    private void field_particle_created(nint manager, nint obj)
    {
        if (!_fieldPresentationInstalled || !is_field_particle_manager(manager)) return;
        lock (_fieldPresentationLock)
        {
            _fieldPresentationGenerations[manager] = field_particle_generation(manager) + 1;
            _fieldPresentationPlans.Remove(manager);
            if (obj != 0) _fieldPresentationObjects[obj] = manager;
        }
    }

    private void field_particle_object_changed(nint obj)
    {
        lock (_fieldPresentationLock)
            if (_fieldPresentationObjects.Remove(obj, out nint manager)) field_particle_created(manager, 0);
    }

    private void field_particle_manager_changed(nint manager)
    {
        field_particle_created(manager, 0);
        lock (_fieldPresentationLock)
            foreach (nint obj in _fieldPresentationObjects.Where(x => x.Value == manager).Select(x => x.Key).ToArray())
                _fieldPresentationObjects.Remove(obj);
    }

    private bool field_particle_presentation_is_qualified()
    {
        if (!_fieldPresentationInstalled) return false;
        lock (_fieldPresentationLock)
        {
            nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
            if (table != _fieldPresentationTable)
            {
                _fieldPresentationPlans.Clear(); _fieldPresentationObjects.Clear();
                _fieldPresentationTable = table;
                return false;
            }
            int battle = FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount);
            if (battle is < 0 or > 1024) return false;
            byte* battleManagers = FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers);
            for (int i = 0; i < battle; i++)
                if (live_objects(battleManagers + i * ParticleManagerStride) > 0) return false;
            bool any = false;
            byte* managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
            for (int i = 0; i < field_manager_count(); i++)
            {
                nint manager = (nint)(managers + i * ParticleManagerStride);
                if (live_objects((byte*)manager) == 0) continue;
                any = true;
                if (*(int*)manager >= 0 || *(int*)manager == -0x1000) continue;
                if (!_fieldPresentationPlans.TryGetValue(manager, out var plan)
                    || plan.Thread != Environment.CurrentManagedThreadId
                    || !plan.Matches(field_particle_generation(manager), _fieldPresentationKernels)
                    || !field_particle_backends_ready(plan)) return false;
            }
            return any;
        }
    }

    private bool battle_particles_idle()
    {
        int battle = FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount);
        if (battle is < 0 or > 1024) return false;
        byte* managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers);
        for (int i = 0; i < battle; i++)
            if (live_objects(managers + i * ParticleManagerStride) > 0) return false;
        return true;
    }

    private void field_particle_flush_deferred(nint manager)
    {
        if (_fieldDeferral.PendingRestarts == 0 && _fieldDeferral.PendingFrees == 0) return;
        // Restart init callbacks run inside the dispatcher natively, where ppvMng is this manager.
        uint current = FhUtil.get_at<uint>(PilotCurrentManager);
        FhUtil.set_at(PilotCurrentManager, (uint)(nuint)manager);
        try { _fieldDeferral.Flush(manager, h_particle_pilot_reset, h_particle_pilot_delete); }
        finally { FhUtil.set_at(PilotCurrentManager, current); }
    }

    private bool field_particle_presentation_ready()
        => _fieldPauseInstalled && particle_presentation_is_qualified();

    private bool field_particle_backends_ready(FieldParticlePresentationPlan plan)
    {
        foreach (var call in plan.Calls)
            if (call.Kernel.Producer == FieldParticleProducer.ShapeField && *(uint*)(call.Data + 4) != 0xffff)
            {
                int level = _fieldLevel!.Value();
                if (level is 0x76f or 0x18e7 or 0x5c8 or 0x1f7 or 0x4c4 or 0x1b8a or 0x324 or 0x1e8a or 0x1139
                    || _fieldBackend!.Value(call.Index, call.Object) == 0) return false;
            }
        return true;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_field_particle_dispatch(nint manager)
    {
        var original = _fieldDispatchOriginal ??= new FhCallHandle<d_field_dispatch>(new FhCallLocation(0x317010, 0))
            .chain_from(h_field_particle_dispatch).fnptr!;
        if (!_fieldPresentationInstalled || !is_field_particle_manager(manager)) { original(manager); return; }
        if (_fieldPauseInstalled)
        {
            if (RenderPassContext.IsPresentation)
            {
                int paused = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
                FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
                try { original(manager); }
                finally { FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused); }
                return;
            }
            // Only a paced step can be followed by a presentation pass that would show its tail.
            _fieldDeferral.Scope = RenderPassContext.IsActive ? manager : 0;
            try { original(manager); }
            finally { _fieldDeferral.Scope = 0; }
            return;
        }
        if (!RenderPassContext.IsPresentation)
        {
            original(manager);
            lock (_fieldPresentationLock)
            {
                nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
                if (table != _fieldPresentationTable) { _fieldPresentationPlans.Clear(); _fieldPresentationObjects.Clear(); }
                _fieldPresentationTable = table;
                if (FieldParticlePresentationPlan.TryBuild(manager, field_particle_generation(manager), _fieldPresentationKernels, out var plan))
                {
                    _fieldPresentationPlans[manager] = plan!;
                    foreach (var call in plan!.Calls) _fieldPresentationObjects[call.Object] = manager;
                }
                else _fieldPresentationPlans.Remove(manager);
            }
            return;
        }
        lock (_fieldPresentationLock)
        {
            if (!_fieldPresentationPlans.TryGetValue(manager, out var captured)
                || !captured.Matches(field_particle_generation(manager), _fieldPresentationKernels)
                || !field_particle_backends_ready(captured)) { _fieldPresentationPlans.Remove(manager); return; }
            int index = FhUtil.get_at<int>(PilotProgramIndex);
            int paused = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
            try
            {
                captured.Replay(call =>
                {
                    FhUtil.set_at(PilotProgramIndex, call.Index);
                    if (call.Kernel.Producer == FieldParticleProducer.ShapeField)
                    {
                        if (FhUtil.get_at<int>(0x8fb7d0) == 0) _fieldGeometry!.Value(call.Object, call.Data, call.Descriptor, call.Private);
                    }
                    else _fieldPresentationProducers[call.Kernel.Target].Value(call.Object, call.Data, call.Descriptor);
                }, () => captured.Generation == field_particle_generation(manager));
            }
            catch (Exception error)
            {
                _fieldPresentationPlans.Remove(manager);
                if (Interlocked.Increment(ref _fieldPresentationFailures) <= 4)
                    _logger.Error($"[Vrr] Field particle presentation refused: {error.Message}");
            }
            finally { FhUtil.set_at(PilotProgramIndex, index); FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused); }
        }
    }

    private List<(nint Manager, byte[] Work, long Generation)>? field_particle_save_manager_work()
    {
        if (!RenderPassContext.IsPresentation) return null;
        var saved = new List<(nint, byte[], long)>();
        byte* first = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        for (int i = 0; i < field_manager_count(); i++)
        {
            nint manager = (nint)(first + i * ParticleManagerStride);
            saved.Add((manager, new ReadOnlySpan<byte>((void*)(manager + 0x40), 0x40).ToArray(), field_particle_generation(manager)));
        }
        return saved;
    }

    private void field_particle_restore_manager_work(List<(nint Manager, byte[] Work, long Generation)>? saved)
    {
        if (saved is null) return;
        foreach (var item in saved)
            if (field_particle_generation(item.Manager) == item.Generation)
                item.Work.CopyTo(new Span<byte>((void*)(item.Manager + 0x40), 0x40));
    }
}
