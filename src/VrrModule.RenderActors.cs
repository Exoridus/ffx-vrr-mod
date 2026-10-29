using System.Numerics;
using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint RenderActorDraw = 0x1b9a90, RenderActorHierarchy = 0xf6ef0;
    private const nint RenderActorConstruct = 0xf27e0, RenderActorDestruct = 0xf2c50;
    private readonly object _renderActorLock = new();
    private readonly Dictionary<long, RenderActorState> _renderActorStates = [];
    private readonly ThreadLocal<RenderActorDrawScope?> _renderActorDrawScope = new();
    private bool _renderActorsBound;
    private long _renderActorLifetime, _renderActorGeneration, _renderActorResetGeneration;
    private ActorSimulationReceipt? _renderActorTick;
    private ActorImageReceipt? _renderActorLatestReceipt;
    private uint? _renderActorAmbiguousStamp;
    private long _renderActorCaptures, _renderActorBlends, _renderActorSkipped, _renderActorBoneBlends;
    private long _renderActorUnchanged, _renderActorLayoutRejects;

    private sealed class ActorSimulationReceipt(long serial, long generation, long historyEpoch)
    {
        internal readonly long Serial = serial, Generation = generation, HistoryEpoch = historyEpoch;
        private int _complete, _invalid;
        internal bool Complete => Volatile.Read(ref _complete) != 0 && !Invalid;
        internal bool Invalid => Volatile.Read(ref _invalid) != 0;
        internal void Finish() => Volatile.Write(ref _complete, 1);
        internal void Invalidate() => Volatile.Write(ref _invalid, 1);
    }

    private sealed record ActorImageReceipt(PresentationAdmission.Image Image, uint NativeStamp, long Renderer,
        ActorSimulationReceipt Tick, long Epoch, long ResetGeneration, float Alpha, bool Simulation, bool Interpolate)
    {
        internal readonly Dictionary<long, RenderActorImagePose> Poses = [];
    }

    private sealed record RenderActorPose(ActorSimulationReceipt Tick, Matrix4x4[] Matrices, float Step, bool Paired);
    private sealed record RenderActorImagePose(long Lifetime, Matrix4x4[] Canonical, Matrix4x4[] Rendered);
    private sealed class RenderActorState(long mesh, long lifetime)
    {
        internal readonly long Mesh = mesh, Lifetime = lifetime;
        internal RenderActorPose? Previous, Current;
    }
    private sealed class RenderActorDrawScope(nint instance, RenderActorState state, ActorImageReceipt receipt)
    {
        internal readonly nint Instance = instance;
        internal readonly RenderActorState State = state;
        internal readonly ActorImageReceipt Receipt = receipt;
        internal bool Seen, Invalid, Blended;
    }

    private object render_actors_counters()
    {
        lock (_renderActorLock) return new
        {
            captures = _renderActorCaptures, blends = _renderActorBlends, skipped = _renderActorSkipped,
            restores = 0, bone_matrices_blended = _renderActorBoneBlends, unchanged = _renderActorUnchanged,
            layout_rejects = _renderActorLayoutRejects, tracked_instances = _renderActorStates.Count, replays = 0
        };
    }
    private uint[] render_actors_counter_words()
    {
        lock (_renderActorLock) return
            [unchecked((uint)_renderActorCaptures), unchecked((uint)_renderActorBlends), unchecked((uint)_renderActorSkipped),
            0, unchecked((uint)_renderActorBoneBlends), unchecked((uint)_renderActorUnchanged),
            unchecked((uint)_renderActorLayoutRejects), (uint)_renderActorStates.Count, 0];
    }

    private bool ActorObservationRequested => Environment.GetEnvironmentVariable("FFX_VRR_ACTOR_PROBE") == "1"
        && _config.NativePhaseProbe && _diagnostics?.IsCapturing == true
        && ChosenFramerate == 60d && !IndependentPresentationEnabled;

    private bool init_render_actors()
    {
        if (IntPtr.Size != 4 || !NativeBuild.TryValidate(out _)) return false;
        bool valid = render_image_prefix(RenderActorDraw, [0x55,0x8b,0xec,0x6a,0xff])
            && render_image_prefix(0x1b9b78, [0xc7,0x45,0xfc,0,0,0,0,0x89,0x45,0xe0,0xe8,0x69,0xd3,0xf3,0xff])
            && render_image_prefix(0x1b9ecc, [0x8b,0xe5,0x5d,0xc2,0x10,0])
            && render_image_prefix(RenderActorHierarchy, [0x55,0x8b,0xec,0x81,0xec,0x80,0,0,0,0x56,0x57,0x8b,0xf9,0xf6,0x47,0x6c,1])
            && render_image_prefix(0xf6f53, [0x8b,0x75,8,0x50,0x8d,0x4e,0xc0,0xe8,0xe1,0xc3,0xf5,0xff,0x56,0x8b,0xcf,0xe8,0x59,0,0,0,0x5f,0x5e,0x8b,0xe5,0x5d,0xc2,4,0])
            && render_image_prefix(RenderActorConstruct, [0x55,0x8b,0xec,0x6a,0xff])
            && render_image_prefix(0xf28df, [0x8b,0xc6,0x8b,0x4d,0xf4,0x64,0x89,0x0d,0,0,0,0,0x59,0x5e,0x8b,0xe5,0x5d,0xc2,4,0])
            && render_image_prefix(RenderActorDestruct, [0x55,0x8b,0xec,0x6a,0xff])
            && render_image_prefix(0xf2dcb, [0x8b,0x4d,0xf4,0x64,0x89,0x0d,0,0,0,0,0x59,0x5f,0x5e,0x5b,0x8b,0xe5,0x5d,0xc3]);
        if (!valid) { _logger.Error("[Vrr] Actor draw/hierarchy/lifetime binding refused."); return false; }
        bool ok = hook_or_log(HookSiteId.RenderActorsMeshInstanceDraw, "actor mesh draw", RenderActorDraw,
            () => new FhCallHandle<d_render_actor_draw>(new FhCallLocation(RenderActorDraw, 0)).hook(this, with_hook_continuation<d_render_actor_draw>(h_render_actor_draw)));
        ok &= hook_or_log(HookSiteId.RenderActorsMeshInstanceHierarchy, "actor draw hierarchy", RenderActorHierarchy,
            () => new FhCallHandle<d_render_actor_hierarchy>(new FhCallLocation(RenderActorHierarchy, 0)).hook(this, with_hook_continuation<d_render_actor_hierarchy>(h_render_actor_hierarchy)));
        ok &= hook_or_log(HookSiteId.RenderActorsMeshInstanceConstruct, "actor instance construction", RenderActorConstruct,
            () => new FhCallHandle<d_render_actor_construct>(new FhCallLocation(RenderActorConstruct, 0)).hook(this, with_hook_continuation<d_render_actor_construct>(h_render_actor_construct)));
        ok &= hook_or_log(HookSiteId.RenderActorsMeshInstanceDestruct, "actor instance destruction", RenderActorDestruct,
            () => new FhCallHandle<d_render_actor_destruct>(new FhCallLocation(RenderActorDestruct, 0)).hook(this, with_hook_continuation<d_render_actor_destruct>(h_render_actor_destruct)));
        _renderActorsBound = ok;
        return ok;
    }

    private void render_actors_begin_simulation() => render_actors_begin_image(true, _renderCameraAlpha);
    private void render_actors_begin_presentation(float alpha) => render_actors_begin_image(false, alpha);
    private void render_actors_complete_simulation()
    {
        lock (_renderActorLock)
        {
            if (_renderActorTick is not { } tick) return;
            if (tick.Serial == RenderPassContext.SimulationSerial && tick.HistoryEpoch == RenderPassContext.HistoryEpoch) tick.Finish();
            else tick.Invalidate();
        }
    }
    private void render_actors_restore()
    {
        // Hierarchy substitutions belong to native draw scratch; game-owned actor storage is never borrowed.
    }
    private void reset_render_actors()
    {
        lock (_renderActorLock)
        {
            _renderActorResetGeneration++;
            _renderActorTick?.Invalidate();
            _renderActorTick = null;
            _renderActorLatestReceipt = null;
            _renderActorAmbiguousStamp = null;
            foreach (var state in _renderActorStates.Values) state.Previous = state.Current = null;
        }
    }

    private void render_actors_begin_image(bool simulation, float alpha)
    {
        lock (_renderActorLock)
        {
            if (!_renderActorsBound || _renderImageFrame.Value is not { Valid: true } frame) return;
            Span<byte> word = stackalloc byte[4];
            if (!MemoryProbe.TryCopy(frame.Application + 0x260, word)) return;
            uint stamp = MemoryMarshal.Read<uint>(word);
            if ((_renderActorLatestReceipt is { } previous && previous.NativeStamp == stamp && previous.Image.Serial != frame.Image.Serial)
                || _renderActorAmbiguousStamp == stamp)
            {
                reset_render_actors();
                _renderActorAmbiguousStamp = stamp;
                frame.ActorReceipt = null;
                return;
            }
            _renderActorAmbiguousStamp = null;
            long epoch;
            lock (_graphicsLifetimeGate)
            {
                if (_graphicsActive != 0) return;
                epoch = _graphicsEpoch;
            }
            if (_renderActorLatestReceipt is { } old && (old.Epoch != epoch || old.Tick.HistoryEpoch != RenderPassContext.HistoryEpoch)) reset_render_actors();
            if (simulation) _renderActorTick = new(RenderPassContext.SimulationSerial, ++_renderActorGeneration, RenderPassContext.HistoryEpoch);
            if (_renderActorTick is null) return;
            var receipt = new ActorImageReceipt(frame.Image, stamp, (long)(frame.Application + 0x124), _renderActorTick, epoch, _renderActorResetGeneration, alpha, simulation, frame.Admitted);
            frame.ActorReceipt = _renderActorLatestReceipt = receipt;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate uint d_render_actor_draw(nint renderer, nint instance, nint materials, int segment, int stamp);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void d_render_actor_hierarchy(nint instance, nint output);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate nint d_render_actor_construct(nint instance, nint mesh);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate uint d_render_actor_destruct(nint instance);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_render_actor_construct(nint instance, nint mesh)
    {
        if (!RuntimeEffectsArmed)
        {
            return HookSites.Native(() => new FhCallHandle<d_render_actor_construct>(new FhCallLocation(RenderActorConstruct, 0)).chain_from(h_render_actor_construct).fnptr)(instance, mesh);
        }

        var original = new FhCallHandle<d_render_actor_construct>(new FhCallLocation(RenderActorConstruct, 0)).chain_from(h_render_actor_construct).fnptr!;
        long? before;
        lock (_renderActorLock)
        {
            before = _renderActorStates.TryGetValue((long)instance, out var state) ? state.Lifetime : null;
            _renderActorStates.Remove((long)instance);
        }
        bool normal = false;
        nint result = 0;
        try
        {
            result = original(instance, mesh);
            if (RuntimeEffectsArmed && _renderActorsBound && result == instance)
                lock (_renderActorLock) _renderActorStates[(long)instance] = new((long)mesh, ++_renderActorLifetime);
            normal = true;
            return result;
        }
        finally { diagnostic_actor_lifetime("ctor", instance, normal, normal ? unchecked((uint)(nuint)result) : null, before); }
    }
    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_actor_destruct(nint instance)
    {
        if (!RuntimeEffectsArmed)
        {
            return HookSites.Native(() => new FhCallHandle<d_render_actor_destruct>(new FhCallLocation(RenderActorDestruct, 0)).chain_from(h_render_actor_destruct).fnptr)(instance);
        }

        long? before;
        lock (_renderActorLock)
        {
            before = _renderActorStates.TryGetValue((long)instance, out var state) ? state.Lifetime : null;
            _renderActorStates.Remove((long)instance);
        }
        bool normal = false;
        uint result = 0;
        try
        {
            result = new FhCallHandle<d_render_actor_destruct>(new FhCallLocation(RenderActorDestruct, 0)).chain_from(h_render_actor_destruct).fnptr!(instance);
            normal = true;
            return result;
        }
        finally { diagnostic_actor_lifetime("dtor", instance, normal, normal ? result : null, before); }
    }

    private void diagnostic_actor_lifetime(string kind, nint instance, bool normal, uint? result, long? before)
    {
        try
        {
            if (!RuntimeEffectsArmed || !ActorObservationRequested) return;
            long? after;
            lock (_renderActorLock) after = _renderActorStates.TryGetValue((long)instance, out var state) ? state.Lifetime : null;
            diagnostic_event(kind == "ctor" ? "actor_constructor" : "actor_destructor", new
            {
                kind, instance = (uint)(nuint)instance, normal_return = normal, native_result = result,
                lifetime_before = before, lifetime_after = after
            });
        }
        catch (Exception) { }
    }
    private ActorImageReceipt? render_actor_receipt(nint renderer, int stamp)
    {
        ActorImageReceipt? receipt;
        if (_renderImageAction.Value is { } action)
        {
            receipt = action.ActorReceipt;
            if (receipt is null || !ReferenceEquals(action.Image, receipt.Image) || renderer != action.Application + 0x124) return null;
        }
        else if (_renderImageFrame.Value is { Valid: true } frame)
        {
            receipt = frame.ActorReceipt;
            if (receipt is null || !ReferenceEquals(frame.Image, receipt.Image) || renderer != frame.Application + 0x124) return null;
        }
        else return null;
        return receipt.ResetGeneration == _renderActorResetGeneration && receipt.Renderer == (long)renderer && receipt.NativeStamp == unchecked((uint)stamp) && !receipt.Tick.Invalid
            && receipt.Tick.HistoryEpoch == RenderPassContext.HistoryEpoch && !receipt.Image.IsRejected ? receipt : null;
    }
    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_render_actor_draw(nint renderer, nint instance, nint materials, int segment, int stamp)
    {
        if (!RuntimeEffectsArmed)
        {
            return HookSites.Native(() => new FhCallHandle<d_render_actor_draw>(new FhCallLocation(RenderActorDraw, 0)).chain_from(h_render_actor_draw).fnptr)(renderer, instance, materials, segment, stamp);
        }

        var original = new FhCallHandle<d_render_actor_draw>(new FhCallLocation(RenderActorDraw, 0)).chain_from(h_render_actor_draw).fnptr!;
        RenderActorDrawScope? previous = _renderActorDrawScope.Value, scope = null;
        lock (_renderActorLock)
        {
            if (RuntimeEffectsArmed && _renderActorsBound && previous is null && render_actor_receipt(renderer, stamp) is { } receipt
                && _renderActorStates.TryGetValue((long)instance, out var state)) scope = new(instance, state, receipt);
            else _renderActorSkipped++;
            if (previous is not null) previous.Invalid = true;
        }
        _renderActorDrawScope.Value = scope;
        bool normal = false;
        uint result = 0;
        try { result = original(renderer, instance, materials, segment, stamp); normal = true; return result; }
        finally
        {
            _renderActorDrawScope.Value = previous;
            if (RuntimeEffectsArmed && _config.NativePhaseProbe && _diagnostics?.IsCapturing == true)
                try
                {
                    long? lifetime;
                    lock (_renderActorLock) lifetime = _renderActorStates.TryGetValue((long)instance, out var known) ? known.Lifetime : null;
                    diagnostic_event("actor_draw_scope", new { stamp, renderer = (uint)(nuint)renderer, instance = (uint)(nuint)instance,
                        image = scope?.Receipt.Image.Serial, simulation_serial = scope?.Receipt.Tick.Serial,
                        simulation = scope?.Receipt.Simulation, completed = scope?.Receipt.Tick.Complete,
                        constructor_lifetime = lifetime, hierarchy_seen = scope?.Seen, blended = scope?.Blended,
                        normal_return = normal, native_result = normal ? (uint?)result : null });
                }
                catch (Exception) { }
        }
    }

    private void diagnostic_actor_hierarchy(nint instance, nint output, bool normal)
    {
        try
        {
            if (!RuntimeEffectsArmed || !ActorObservationRequested) return;
            Span<byte> instanceBytes = stackalloc byte[16], meshBytes = stackalloc byte[28], sample = stackalloc byte[128];
            int? matrices = null;
            uint[]? words = null;
            if (normal && (nuint)output >= 64 && MemoryProbe.TryCopy(instance, instanceBytes))
            {
                uint mesh = MemoryMarshal.Read<uint>(instanceBytes);
                if (MemoryProbe.TryCopy(unchecked((nint)(nuint)mesh), meshBytes))
                {
                    uint count = MemoryMarshal.Read<uint>(meshBytes[24..]) & 0x7fffffff;
                    if (count <= 2048)
                    {
                        matrices = (int)count + 1;
                        var bytes = sample[..(Math.Min(matrices.Value, 2) * 64)];
                        if (MemoryProbe.TryCopy(output - 64, bytes)) words = MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
                    }
                }
            }
            long? lifetime;
            lock (_renderActorLock) lifetime = _renderActorStates.TryGetValue((long)instance, out var state) ? state.Lifetime : null;
            diagnostic_event("actor_hierarchy_output", new { instance = (uint)(nuint)instance, output = (uint)(nuint)output,
                normal_return = normal, constructor_lifetime = lifetime, matrix_count = matrices,
                sampled_matrices = words is null ? 0 : words.Length / 16, raw_words = words,
                snapshot_atomic = false, native_actor_storage_written = false });
        }
        catch (Exception) { }
    }
    private static bool render_actor_ranges_overlap(nuint start, nuint length, nuint other, nuint otherLength)
        => length != 0 && otherLength != 0 && start <= nuint.MaxValue - length && other <= nuint.MaxValue - otherLength
            && start < other + otherLength && other < start + length;
    private static bool render_actor_output_layout(nint instance, nint output, long meshIdentity, out int matrices)
    {
        matrices = 0;
        Span<byte> header = stackalloc byte[16], meshHeader = stackalloc byte[48];
        if (!MemoryProbe.TryCopy(instance, header)) return false;
        var fields = MemoryMarshal.Cast<byte, uint>(header);
        nint mesh = unchecked((nint)(nuint)fields[0]);
        if ((long)mesh != meshIdentity || !MemoryProbe.TryCopy(mesh, meshHeader)) return false;
        var meshWords = MemoryMarshal.Cast<byte, uint>(meshHeader);
        uint bones = meshWords[6] & 0x7fffffff;
        if (bones > 2048 || (bones != 0 && (fields[3] == 0 || meshWords[11] == 0)) || (nuint)output < 64) return false;
        matrices = checked((int)bones + 1);
        nuint start = (nuint)output - 64, bytes = (nuint)matrices * 64;
        if (start > nuint.MaxValue - bytes || (nuint)instance > nuint.MaxValue - 0x84
            || (nuint)mesh > nuint.MaxValue - 48 || (fields[1] != 0 && fields[1] > nuint.MaxValue - 48)
            || (bones != 0 && fields[3] > nuint.MaxValue - bones * 64)) return false;
        return !render_actor_ranges_overlap(start, bytes, (nuint)instance, 0x84)
            && !render_actor_ranges_overlap(start, bytes, (nuint)mesh, 48)
            && !render_actor_ranges_overlap(start, bytes, fields[1], fields[1] == 0 ? 0u : 48u)
            && !render_actor_ranges_overlap(start, bytes, fields[3], bones * 64);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private void h_render_actor_hierarchy(nint instance, nint output)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_actor_hierarchy>(new FhCallLocation(RenderActorHierarchy, 0)).chain_from(h_render_actor_hierarchy).fnptr)(instance, output);
            return;
        }

        var original = new FhCallHandle<d_render_actor_hierarchy>(new FhCallLocation(RenderActorHierarchy, 0)).chain_from(h_render_actor_hierarchy).fnptr!;
        var scope = _renderActorDrawScope.Value;
        bool eligible = scope is { Invalid: false, Seen: false } && scope.Instance == instance;
        if (scope is not null) { if (scope.Seen || scope.Instance != instance) scope.Invalid = true; scope.Seen = true; }
        int matrices = 0;
        if (eligible)
            try { eligible = render_actor_output_layout(instance, output, scope!.State.Mesh, out matrices); }
            catch (Exception) { eligible = false; }
        bool normal = false;
        try { original(instance, output); normal = true; }
        finally { diagnostic_actor_hierarchy(instance, output, normal); }
        if (!eligible || scope is null) { if (scope is not null) lock (_renderActorLock) _renderActorLayoutRejects++; return; }
        try { render_actor_apply_hierarchy(scope, instance, output, matrices); }
        catch (Exception)
        {
            lock (_renderActorLock) { scope.Invalid = true; _renderActorLayoutRejects++; }
        }
    }

    private void render_actor_apply_hierarchy(RenderActorDrawScope scope, nint instance, nint output, int matrices)
    {
        Span<byte> raw = stackalloc byte[matrices * 64];
        if (!MemoryProbe.TryCopy(output - 64, raw)) return;
        lock (_renderActorLock)
        {
            if (scope.Invalid || scope.Receipt.ResetGeneration != _renderActorResetGeneration || scope.Receipt.Tick.Invalid || scope.Receipt.Tick.HistoryEpoch != RenderPassContext.HistoryEpoch
                || scope.Receipt.Image.IsRejected
                || !_renderActorStates.TryGetValue((long)instance, out var current) || !ReferenceEquals(current, scope.State)) return;
            lock (_graphicsLifetimeGate)
                if (_graphicsActive != 0 || _graphicsEpoch != scope.Receipt.Epoch) return;
            if (!render_actor_output_layout(instance, output, scope.State.Mesh, out int currentMatrices) || currentMatrices != matrices) return;
            var canonical = MemoryMarshal.Cast<byte, Matrix4x4>(raw);
            var receipt = scope.Receipt;
            if (receipt.Poses.TryGetValue((long)instance, out var cached))
            {
                if (cached.Lifetime == current.Lifetime && RenderActorHistory.Equal(canonical, cached.Canonical) && receipt.Interpolate)
                { cached.Rendered.CopyTo(new Span<Matrix4x4>((void*)(output - 64), matrices)); scope.Blended = !ReferenceEquals(cached.Rendered, cached.Canonical); }
                return;
            }
            if (receipt.Simulation)
            {
                if (current.Current is { } seen && seen.Tick.Serial > receipt.Tick.Serial) return;
                if (current.Current is null || !ReferenceEquals(current.Current.Tick, receipt.Tick))
                {
                    var preceding = current.Current;
                    float step = preceding is null ? 0 : Vector3.Distance(preceding.Matrices[0].Translation, canonical[0].Translation);
                    bool pair = preceding is not null && preceding.Tick.Complete && preceding.Tick.Serial + 1 == receipt.Tick.Serial
                        && preceding.Matrices.Length == matrices && step <= 500
                        && !(preceding.Paired && step > 30 && step > 3 * preceding.Step);
                    current.Previous = pair ? preceding : null;
                    current.Current = new(receipt.Tick, canonical.ToArray(), step, pair);
                    _renderActorCaptures++;
                }
            }
            if (current.Current is not { } pose || !ReferenceEquals(pose.Tick, receipt.Tick)
                || (!receipt.Simulation && !pose.Tick.Complete) || !RenderActorHistory.Equal(canonical, pose.Matrices)) return;
            Matrix4x4[] rendered = pose.Matrices;
            if (receipt.Interpolate && float.IsFinite(receipt.Alpha) && current.Previous is { } prior && prior.Tick.Complete)
            {
                var candidate = new Matrix4x4[matrices];
                bool valid = true;
                for (int i = 0; i < matrices; i++)
                    if (!RenderActorHistory.TryBlend(prior.Matrices[i], canonical[i], Math.Clamp(receipt.Alpha, 0, 1), out candidate[i])) { valid = false; break; }
                if (valid && !RenderActorHistory.Equal(canonical, candidate))
                {
                    rendered = candidate;
                    candidate.CopyTo(new Span<Matrix4x4>((void*)(output - 64), matrices));
                    scope.Blended = true; _renderActorBlends++;
                    for (int i = 1; i < matrices; i++)
                        if (!RenderCameraWrite.BitwiseEqual(canonical[i], candidate[i])) _renderActorBoneBlends++;
                }
                else _renderActorUnchanged++;
            }
            else _renderActorSkipped++;
            receipt.Poses[(long)instance] = new(current.Lifetime, pose.Matrices, rendered);
        }
    }
}
