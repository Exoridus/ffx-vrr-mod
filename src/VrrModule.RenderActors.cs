using System.Numerics;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint RenderActorWorldGroup = 0x23c3e0;
    private const nint RenderActorWorldModel = 0x23c430;
    private const nint RenderActorWorldInstance = 0x26d810;
    private const nint RenderActorSkeletonCommit = 0x23be60;
    [ThreadStatic] private static nint _renderActorSubmittingGroup;
    private readonly RenderActorHistory _renderActorHistory = new();
    private readonly Dictionary<nint, RenderActorNativeState> _renderActorStates = [];
    private readonly object _renderActorLock = new();
    private bool _renderActorsBound, _renderActorsActive, _renderActorsSampling;
    private long _renderActorCaptures, _renderActorBlends, _renderActorSkipped, _renderActorRestores;
    private long _renderActorBoneBlends, _renderActorUnchanged, _renderActorLayoutRejects, _renderActorReplays;
    private int _renderActorCallbacks;
    // shortcut: Refused loans stay retained until a qualified quiescent recovery boundary is implemented.
    private bool _renderActorCompletionRefused;
    private long? _renderActorGraphicsEpoch;

    private object render_actors_counters()
    {
        lock (_renderActorLock) return new
        {
            captures = _renderActorCaptures, blends = _renderActorBlends, skipped = _renderActorSkipped,
            restores = _renderActorRestores, bone_matrices_blended = _renderActorBoneBlends,
            unchanged = _renderActorUnchanged, layout_rejects = _renderActorLayoutRejects,
            tracked_instances = _renderActorStates.Count, replays = _renderActorReplays
        };
    }

    private uint[] render_actors_counter_words()
    {
        lock (_renderActorLock) return
            [unchecked((uint)_renderActorCaptures), unchecked((uint)_renderActorBlends), unchecked((uint)_renderActorSkipped),
            unchecked((uint)_renderActorRestores), unchecked((uint)_renderActorBoneBlends), unchecked((uint)_renderActorUnchanged),
            unchecked((uint)_renderActorLayoutRejects), (uint)_renderActorStates.Count, unchecked((uint)_renderActorReplays)];
    }

    private sealed class RenderActorNativeState(RenderActorIdentity identity, int boneCount)
    {
        internal readonly RenderActorIdentity Identity = identity;
        internal readonly Matrix4x4[] RenderedBones = new Matrix4x4[boneCount];
        internal readonly RenderActorWrite Write = new(boneCount);
        internal readonly byte[] CanonicalWorld = new byte[48];
        internal bool HasCanonicalWorld;
        internal long Generation;
    }

    private long _renderActorGeneration;

    private bool init_render_actors()
    {
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Actor renderer binding refused: {error}");
            return false;
        }
        byte* group = FhUtil.ptr_at<byte>(RenderActorWorldGroup);
        byte* model = FhUtil.ptr_at<byte>(RenderActorWorldModel);
        byte* instance = FhUtil.ptr_at<byte>(RenderActorWorldInstance);
        byte* skeleton = FhUtil.ptr_at<byte>(RenderActorSkeletonCommit);
        bool valid = MemoryProbe.IsReadable((nint)group, 67) && MemoryProbe.IsReadable((nint)model, 190)
            && MemoryProbe.IsReadable((nint)instance, 252) && MemoryProbe.IsReadable((nint)skeleton, 348)
            && new ReadOnlySpan<byte>(group, 9).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x57, 0x8b, 0x7d, 8, 0x85, 0xff])
            && new ReadOnlySpan<byte>(model, 10).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x83, 0xec, 0x0c, 0x53, 0x8b, 0x5d, 8])
            && new ReadOnlySpan<byte>(instance, 15).SequenceEqual<byte>([0x55, 0x8b, 0xec, 0x83, 0xec, 0x50,
                0x8b, 0x45, 0x0c, 0x56, 0xd9, 0, 0x8b, 0x75, 8])
            && new ReadOnlySpan<byte>(instance + 0xf9, 3).SequenceEqual<byte>([0xc2, 8, 0])
            && new ReadOnlySpan<byte>(skeleton + 0x25, 17).SequenceEqual<byte>([0x8b, 0x41, 4, 0x8b, 4, 0x98,
                0x8b, 0x50, 0x0c, 0x8b, 0, 0x8b, 0x78, 0x18, 0x8b, 0x41, 0x10])
            && group[0x2d] == 0xe8 && group + 0x32 + *(int*)(group + 0x2e) == instance
            && model[0x70] == 0xe8 && model + 0x75 + *(int*)(model + 0x71) == instance;
        if (!valid)
        {
            _logger.Error("[Vrr] Actor renderer binding refused: world submission/palette layout/stack cleanup contract mismatch.");
            return false;
        }
        bool ok = hook_or_log(HookSiteId.RenderActorsActorWorldGroup, "actor world group", RenderActorWorldGroup,
            () => new FhCallHandle<d_render_actor_group>(new FhCallLocation(RenderActorWorldGroup, 0)).hook(this, with_hook_continuation<d_render_actor_group>(h_render_actor_group)));
        ok &= hook_or_log(HookSiteId.RenderActorsActorModelWorldGroup, "actor model world group", RenderActorWorldModel,
            () => new FhCallHandle<d_render_actor_model>(new FhCallLocation(RenderActorWorldModel, 0)).hook(this, with_hook_continuation<d_render_actor_model>(h_render_actor_model)));
        ok &= hook_or_log(HookSiteId.RenderActorsActorRendererWorldInstance, "actor renderer world instance", RenderActorWorldInstance,
            () => new FhCallHandle<d_render_actor_instance>(new FhCallLocation(RenderActorWorldInstance, 0)).hook(this, with_hook_continuation<d_render_actor_instance>(h_render_actor_instance)));
        _renderActorsBound = ok;
        return ok;
    }

    private void render_actors_begin_simulation()
    {
        lock (_renderActorLock)
        {
            if (!render_actor_lifetime_valid(bind: true)) return;
            render_actors_restore();
            if (!render_actor_lifetime_valid()) return;
            _renderActorGeneration++;
            _renderActorHistory.BeginSimulation();
            _renderActorsActive = _renderActorsSampling = true;
        }
    }

    private bool render_actor_lifetime_valid(bool bind = false)
    {
        if (_renderActorCompletionRefused) return false;
        lock (_graphicsLifetimeGate)
        {
            if (_renderActorGraphicsEpoch is null && !bind) return true;
            if (_graphicsActive != 0 || (_renderActorGraphicsEpoch is { } epoch && epoch != _graphicsEpoch))
            {
                refuse_render_actor_completion("actor_graphics_lifetime_changed");
                return false;
            }
            _renderActorGraphicsEpoch ??= _graphicsEpoch;
            return true;
        }
    }

    private static bool render_actor_jobs_idle()
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        nint slot = (nint)FhUtil.ptr_at<byte>(0x8dede4);
        if (!MemoryProbe.TryCopy(slot, word)) return false;
        uint scheduler = MemoryMarshal.Read<uint>(word);
        if (scheduler == 0 || !MemoryProbe.TryCopy((nint)(nuint)scheduler + 0x1f8, word)
            || MemoryMarshal.Read<int>(word) != 8) return false;
        return MemoryProbe.TryCopy(slot, word) && MemoryMarshal.Read<uint>(word) == scheduler;
    }
    private void refuse_render_actor_completion(string reason)
    {
        _renderActorCompletionRefused = true;
        _renderActorsActive = _renderActorsSampling = false;
        _renderActorHistory.Reset();
        refuse_independent_presentation(reason);
    }
    private void render_actors_complete_simulation()
    {
        lock (_renderActorLock)
        {
            if (!render_actor_lifetime_valid()) return;
            if (System.Threading.Volatile.Read(ref _renderActorCallbacks) != 0)
            {
                refuse_render_actor_completion("actor_callbacks_outstanding");
                return;
            }
            // Native queue credit includes jobs whose callbacks have not entered; this check does not join them.
            if (!render_actor_jobs_idle())
            {
                refuse_render_actor_completion("actor_jobs_unconfirmed");
                return;
            }
            if (!render_actor_lifetime_valid()) return;
            _renderActorHistory.CompleteSimulation();
            _renderActorsSampling = false;
            foreach (var key in _renderActorStates.Where(pair => pair.Value.Generation != _renderActorGeneration).Select(pair => pair.Key).ToArray())
            {
                restore_render_actor(key, _renderActorStates[key]);
                if (!render_actor_lifetime_valid()) return;
                _renderActorStates.Remove(key);
            }
        }
    }

    private void render_actors_begin_presentation(float alpha)
    {
        lock (_renderActorLock)
        {
            if (!render_actor_lifetime_valid(bind: true)) return;
            render_actors_restore();
            if (!render_actor_lifetime_valid()) return;
            render_camera_set_presentation_alpha(alpha);
            _renderActorsActive = true;
            _renderActorsSampling = false;
            replay_render_actors();
        }
    }

    private void render_actors_restore()
    {
        lock (_renderActorLock)
        {
            if (!render_actor_lifetime_valid()) return;
            foreach (var pair in _renderActorStates) restore_render_actor(pair.Key, pair.Value);
        }
    }

    private void reset_render_actors()
    {
        lock (_renderActorLock)
        {
            if (!render_actor_lifetime_valid()) return;
            render_actors_restore();
            if (!render_actor_lifetime_valid()) return;
            _renderActorHistory.Reset();
            _renderActorStates.Clear();
            _renderActorGraphicsEpoch = null;
            _renderActorsActive = _renderActorsSampling = false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_render_actor_group(nint group, Matrix4x4* matrix);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_render_actor_model(nint group, Matrix4x4* matrix, uint model);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void d_render_actor_instance(nint instance, Matrix4x4* matrix);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_render_actor_group(nint group, Matrix4x4* matrix)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_actor_group>(new FhCallLocation(RenderActorWorldGroup, 0)).chain_from(h_render_actor_group).fnptr)(group, matrix);
            return;
        }

        nint previous = _renderActorSubmittingGroup;
        _renderActorSubmittingGroup = group;
        try
        {
            new FhCallHandle<d_render_actor_group>(new FhCallLocation(RenderActorWorldGroup, 0))
                .chain_from(h_render_actor_group).fnptr!(group, matrix);
        }
        finally { _renderActorSubmittingGroup = previous; }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_render_actor_model(nint group, Matrix4x4* matrix, uint model)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_actor_model>(new FhCallLocation(RenderActorWorldModel, 0)).chain_from(h_render_actor_model).fnptr)(group, matrix, model);
            return;
        }

        nint previous = _renderActorSubmittingGroup;
        _renderActorSubmittingGroup = group;
        try
        {
            new FhCallHandle<d_render_actor_model>(new FhCallLocation(RenderActorWorldModel, 0))
                .chain_from(h_render_actor_model).fnptr!(group, matrix, model);
        }
        finally { _renderActorSubmittingGroup = previous; }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private void h_render_actor_instance(nint instance, Matrix4x4* matrix)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_actor_instance>(new FhCallLocation(RenderActorWorldInstance, 0)).chain_from(h_render_actor_instance).fnptr)(instance, matrix);
            return;
        }

        System.Threading.Interlocked.Increment(ref _renderActorCallbacks);
        try { lock (_renderActorLock) submit_render_actor_instance(instance, matrix); }
        finally { System.Threading.Interlocked.Decrement(ref _renderActorCallbacks); }
    }

    private void submit_render_actor_instance(nint instance, Matrix4x4* matrix)
    {
        var original = new FhCallHandle<d_render_actor_instance>(new FhCallLocation(RenderActorWorldInstance, 0))
            .chain_from(h_render_actor_instance).fnptr!;
        if (!_renderActorsBound || !_renderActorsActive || _renderActorSubmittingGroup == 0 || !render_actor_lifetime_valid())
        {
            original(instance, matrix);
            return;
        }
        if (!MemoryProbe.IsReadable((nint)matrix, 64)
            || !try_render_actor_layout(instance, _renderActorSubmittingGroup, out var identity, out int boneCount))
        {
            _renderActorLayoutRejects++;
            original(instance, matrix);
            return;
        }
        if (!_renderActorStates.TryGetValue(instance, out var state) || state.Identity != identity
            || state.RenderedBones.Length != boneCount)
            _renderActorStates[instance] = state = new(identity, boneCount);
        restore_render_actor(instance, state);
        if (!render_actor_lifetime_valid()) { original(instance, matrix); return; }
        var palette = new Span<Matrix4x4>((void*)(nint)identity.Palette, boneCount);
        Matrix4x4 canonical = *matrix;
        diagnostic_actor_pose(instance, canonical, palette, rendered: false);
        if (_renderActorsSampling)
        {
            _renderActorHistory.Capture((long)instance, identity, canonical, palette);
            _renderActorCaptures++;
            state.Generation = _renderActorGeneration;
        }
        original(instance, matrix);
        if (!render_actor_lifetime_valid()) return;
        if (_renderActorsSampling)
        {
            new ReadOnlySpan<byte>((void*)(nint)identity.World, 48).CopyTo(state.CanonicalWorld);
            state.HasCanonicalWorld = true;
        }
        if (!_renderActorHistory.TryInterpolate((long)instance, identity, canonical, palette,
                _renderCameraAlpha, out var renderedRoot, state.RenderedBones))
        {
            _renderActorSkipped++;
            diagnostic_actor_pose(instance, canonical, palette, rendered: true);
            return;
        }
        if (RenderCameraWrite.BitwiseEqual(canonical, renderedRoot) && RenderActorHistory.Equal(palette, state.RenderedBones))
        {
            _renderActorUnchanged++;
            diagnostic_actor_pose(instance, canonical, palette, rendered: true);
            return;
        }

        Span<byte> canonicalWorld = stackalloc byte[48];
        new ReadOnlySpan<byte>((void*)(nint)identity.World, 48).CopyTo(canonicalWorld);
        // The native setter copies this input synchronously; no stack pointer reaches the draw.
        original(instance, &renderedRoot);
        if (!render_actor_lifetime_valid()) return;
        state.Write.Record(canonicalWorld, new ReadOnlySpan<byte>((void*)(nint)identity.World, 48), palette, state.RenderedBones);
        if (!render_actor_lifetime_valid()) return;
        _renderActorBlends++;
        for (int i = 0; i < palette.Length; i++)
            if (!RenderCameraWrite.BitwiseEqual(palette[i], state.RenderedBones[i])) _renderActorBoneBlends++;
        state.RenderedBones.CopyTo(palette);
        diagnostic_actor_pose(instance, renderedRoot, palette, rendered: true);
    }

    private void replay_render_actors()
    {
        if (!render_actor_lifetime_valid() || !_renderActorsBound) return;
        var original = new FhCallHandle<d_render_actor_instance>(new FhCallLocation(RenderActorWorldInstance, 0))
            .chain_from(h_render_actor_instance).fnptr!;
        foreach (var pair in _renderActorStates)
        {
            if (!render_actor_lifetime_valid()) return;
            var state = pair.Value;
            if (!state.HasCanonicalWorld
                || !try_render_actor_layout(pair.Key, (nint)state.Identity.Group, out var identity, out int count)
                || state.Identity != identity || count != state.RenderedBones.Length) continue;
            var world = new Span<byte>((void*)(nint)identity.World, 48);
            var palette = new Span<Matrix4x4>((void*)(nint)identity.Palette, count);
            if (!world.SequenceEqual(state.CanonicalWorld)
                || !_renderActorHistory.TryInterpolateCurrent((long)pair.Key, identity, palette,
                    _renderCameraAlpha, out var root, state.RenderedBones)) continue;
            // Render-only paths may reuse an instance without resubmitting its world matrix.
            original(pair.Key, &root);
            if (!render_actor_lifetime_valid()) return;
            state.Write.Record(state.CanonicalWorld, world, palette, state.RenderedBones);
            if (!render_actor_lifetime_valid()) return;
            state.RenderedBones.CopyTo(palette);
            diagnostic_actor_pose(pair.Key, root, palette, rendered: true);
            _renderActorReplays++;
        }
    }

    private static bool try_render_actor_layout(nint instance, nint group, out RenderActorIdentity identity, out int bones)
    {
        identity = default;
        bones = 0;
        if (!MemoryProbe.IsReadable(instance, 0x10)) return false;
        nint mesh = *(nint*)instance;
        nint world = *(nint*)(instance + 4);
        nint palette = *(nint*)(instance + 0x0c);
        if (!MemoryProbe.IsReadable(mesh, 0x1c) || !MemoryProbe.IsReadable(world, 48)) return false;
        uint count = *(uint*)(mesh + 0x18) & 0x7fffffff;
        if (count > 2048 || (count != 0 && !MemoryProbe.IsReadable(palette, checked((int)count * 64)))) return false;
        bones = (int)count;
        identity = new((long)group, (long)mesh, (long)palette, (long)world);
        return true;
    }

    private void restore_render_actor(nint instance, RenderActorNativeState state)
    {
        if (!render_actor_lifetime_valid()) return;
        if (!try_render_actor_layout(instance, (nint)state.Identity.Group, out var identity, out int count)
            || identity != state.Identity || count != state.RenderedBones.Length) return;
        if (!render_actor_lifetime_valid()) return;
        if (state.Write.Restore(new Span<byte>((void*)(nint)identity.World, 48),
                new Span<Matrix4x4>((void*)(nint)identity.Palette, count)) > 0) _renderActorRestores++;
    }
}
