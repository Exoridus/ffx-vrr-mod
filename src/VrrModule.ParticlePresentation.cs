using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint PilotWorker = 0x40cc00, PilotDd = 0x40b800;
    private const nint PilotRoots = 0xea40c0, PilotOpcode = 0xef4118;
    private const nint PilotCurrentManager = 0x1f0ffe4, PilotProgramIndex = 0x1f0fd20;
    private const int PilotOwnerLimit = 64, PilotObjectLimit = 1024, PilotCallLimit = 4096;
    private readonly Dictionary<nint, ParticlePilotOwner> _particle_pilot_owners = [];
    private readonly Dictionary<ParticlePresentationKernel, d_pilot_kernel> _particle_pilot_kernels = [];
    private bool _particle_pilot_installed, _particle_pilot_worker, _particle_pilot_replaying;
    private byte _particle_pilot_mode;
    private int _particle_pilot_thread, _particle_pilot_invalid;
    private nint _particle_pilot_dll;
    private long _particle_pilot_epoch;
    private ParticlePilotOwner? _particle_pilot_dd, _particle_pilot_capture;
    private static readonly ushort[] PilotProgram = [0x00dd, 0, 0x0209];

    private sealed class ParticlePilotOwner(nint root, nint address, nint manager, nint resource, long epoch)
    {
        internal readonly nint Root = root, Address = address, Manager = manager, Resource = resource;
        internal readonly long Epoch = epoch;
        internal readonly ParticlePresentationCapture Capture = new(PilotCallLimit);
        internal bool DrawObserved;
    }

    private bool init_particle_presentation()
    {
        if (!_config.VrrEnabled) return true;
        if (!preflight_particle_sprite_clock()) return false;
        if (!NativeBuild.TryValidate(out string error))
        {
            _logger.Error($"[Vrr] Particle presentation refused: {error}");
            return false;
        }
        nint[] targets = [PilotWorker, PilotDd, 0x312250, 0x316770, 0x3168f0, 0x312d20, 0x3123c0, 0x311f20,
            0x32d390, 0x334b70, 0x334ef0, 0x335160, 0x3394c0, 0x339d30, 0x32e790];
        foreach (nint target in targets)
        {
            byte* entry = FhUtil.ptr_at<byte>(target);
            ReadOnlySpan<byte> prefix = target is 0x3394c0 or 0x339d30 or 0x32e790
                ? [0x53, 0x8b, 0xdc] : [0x55, 0x8b, 0xec];
            if (!MemoryProbe.IsReadable((nint)entry, prefix.Length)
                || !new ReadOnlySpan<byte>(entry, prefix.Length).SequenceEqual(prefix))
            {
                _logger.Error($"[Vrr] Particle presentation refused: entry {target:X} changed.");
                return false;
            }
        }
        if (!preflight_field_particle_presentation()) return false;
        bool ok = hook_or_log("particle presentation worker", PilotWorker,
            () => new FhCallHandle<d_pilot_worker>(new FhCallLocation(PilotWorker, 0)).hook(this, h_particle_pilot_worker));
        ok &= hook_or_log("particle presentation DD", PilotDd,
            () => new FhCallHandle<d_pilot_dd>(new FhCallLocation(PilotDd, 0)).hook(this, h_particle_pilot_dd));
        ok &= init_particle_lifecycle();
        foreach (ParticlePresentationKernel kernel in Enum.GetValues<ParticlePresentationKernel>())
        {
            nint rva = (nint)(int)kernel;
            d_pilot_kernel? self = null;
            self = (obj, data, descriptor) =>
            {
                if (!_particle_pilot_replaying && _particle_pilot_capture is { } owner
                    && Environment.CurrentManagedThreadId == _particle_pilot_thread)
                {
                    try { owner.Capture.Record(obj, data, descriptor,
                        FhUtil.get_at<int>(PilotProgramIndex), kernel); }
                    catch (Exception) { owner.Capture.Invalidate(); _particle_pilot_capture = null; }
                }
                new FhCallHandle<d_pilot_kernel>(new FhCallLocation(rva, 0))
                    .chain_from(self!).fnptr!(obj, data, descriptor);
            };
            _particle_pilot_kernels.Add(kernel, self);
            ok &= hook_or_log($"particle presentation {kernel}", rva,
                () => new FhCallHandle<d_pilot_kernel>(new FhCallLocation(rva, 0)).hook(this, self));
        }
        if (ok) ok = init_field_particle_presentation();
        _particle_pilot_installed = ok;
        return ok;
    }

    private void particle_presentation_enter_loop()
    {
        if (!_particle_pilot_installed) return;
        int thread = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _particle_pilot_thread, thread, 0) == 0)
            reset_particle_presentation();
    }
    private void reset_particle_presentation()
    {
        Interlocked.Exchange(ref _particle_pilot_invalid, 1);
        Interlocked.Increment(ref _particle_pilot_epoch);
        if (Environment.CurrentManagedThreadId != _particle_pilot_thread) return;
        _particle_pilot_owners.Clear();
        _particle_pilot_dll = 0;
        _particle_pilot_capture = null;
        Interlocked.Exchange(ref _particle_pilot_invalid, 0);
    }

    private static nint pilot_pointer(nint address) => unchecked((nint)(nuint)(*(uint*)address));
    private static nint pilot_decode(uint encoded) => unchecked((nint)(nuint)encoded);

    private bool pilot_dll_matches(nint root)
    {
        try { return pilot_dll_matches_core(root); }
        catch (Exception) { return false; }
    }

    private bool pilot_dll_matches_core(nint root)
    {
        if (!MemoryProbe.IsReadable(root, 0x60)) return false;
        nint table = pilot_pointer(root + 0x50);
        nint module = table - 0x4a590;
        if (!MemoryProbe.IsReadable(table, 0x60) || !MemoryProbe.IsReadable(module + 0x4ccbc, 4)
            || pilot_pointer(module + 0x4ccbc) != (nint)FhUtil.ptr_at<byte>(0x864ce8)
            || pilot_pointer(table + 0x48) != module + 0x13b0
            || pilot_pointer(table + 0x54) != module + 0x1560) return false;
        if (_particle_pilot_dll != module)
        {
            bool found = false;
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule loaded in process.Modules)
                if (loaded.BaseAddress == module && string.Equals(Path.GetFileName(loaded.FileName),
                    "magic_0670.dll", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = File.OpenRead(loaded.FileName);
                    found = Convert.ToHexStringLower(SHA256.HashData(stream)) ==
                        "fcfc02a0a1b2cec86bd8f8e3d00ca88ced7de923047a3873c4a10104bc9f2cc6";
                    break;
                }
            if (!found) return false;
            _particle_pilot_owners.Clear();
            Interlocked.Increment(ref _particle_pilot_epoch);
            _particle_pilot_dll = module;
        }
        for (int i = 0; i < 18; i++)
        {
            nint descriptor = module + 0x4a610 + i * 0x28;
            if (!MemoryProbe.IsReadable(descriptor, 0x28) || *(uint*)(descriptor + 4) != 0
                || *(uint*)(descriptor + 0x14) != 0 || *(uint*)(descriptor + 0x18) != 0) return false;
        }
        return true;
    }

    private bool pilot_owner_matches(ParticlePilotOwner owner)
        => owner.Epoch == Interlocked.Read(ref _particle_pilot_epoch) && MemoryProbe.IsReadable(owner.Address, 0x100)
        && *(byte*)(owner.Address + 0xbb) == 3
        && pilot_pointer(owner.Address + 0xbc) == owner.Manager
        && pilot_pointer(owner.Address + 0xd0) == owner.Resource
        && MemoryProbe.IsReadable(owner.Manager, 0x80) && MemoryProbe.IsReadable(owner.Root, 0x60)
        && pilot_pointer(owner.Root + 0x50) == _particle_pilot_dll + 0x4a590;

    private bool pilot_read_list(nint root, int mode, List<nint> owners)
    {
        if (!MemoryProbe.IsReadable(root, 0x60)) return false;
        nint list = pilot_pointer(root + 0x10 + mode * 4), storage = pilot_pointer(root + 0x20);
        if (mode == 2 && list == 0) return true;
        for (int i = 0; i <= PilotOwnerLimit; i++)
        {
            if (!MemoryProbe.IsReadable(list + i * 2, 2)) return false;
            short index = *(short*)(list + i * 2);
            if (index == -1) return true;
            if (index < 0 || index >= PilotOwnerLimit || owners.Count >= PilotOwnerLimit) return false;
            nint owner = storage + index * 0x100;
            if (!MemoryProbe.IsReadable(owner, 0x100)) return false;
            if (!owners.Contains(owner)) owners.Add(owner);
        }
        return false;
    }

    private string _qualificationRefusal = "";

    private bool particle_presentation_is_qualified()
    {
        bool Refuse(string reason) { _qualificationRefusal = reason; return false; }
        if (!_particle_pilot_installed) return Refuse("pilot_not_installed");
        if (Environment.CurrentManagedThreadId != _particle_pilot_thread) return Refuse("wrong_thread");
        if (Volatile.Read(ref _particle_pilot_invalid) != 0) return Refuse("pilot_invalid");
        return particle_pilot_scene_qualified(Refuse);
    }

    /// <summary>The Game Over pilot's scene: no live battle state, and every overlay owner observed and replayable.</summary>
    private bool particle_pilot_scene_qualified(Func<string, bool> Refuse)
    {
        if (!battle_effect_presentation_is_qualified()) return Refuse("battle_effect_state");
        // With the native field pause installed only battle and overlay particles still need a qualified owner.
        if (timeline_has_live_objects() && !(_fieldPauseInstalled ? battle_particles_idle() : field_particle_presentation_is_qualified()))
            return Refuse("live_particles_not_idle");
        try
        {
            nint roots = (nint)FhUtil.ptr_at<byte>(PilotRoots);
            nint nativeRoot = pilot_pointer(roots), overlayRoot = pilot_pointer(roots + 4);
            var nativeOwners = new List<nint>();
            if (nativeRoot != 0 && (!pilot_read_list(nativeRoot, 0, nativeOwners)
                || !pilot_read_list(nativeRoot, 1, nativeOwners)
                || !pilot_read_list(nativeRoot, 2, nativeOwners))) return Refuse("native_owner_list");
            var owners = new List<nint>();
            var drawOwners = new List<nint>();
            if (overlayRoot != 0 && (!pilot_read_list(overlayRoot, 0, owners)
                || !pilot_read_list(overlayRoot, 1, drawOwners)
                || !pilot_read_list(overlayRoot, 2, owners))) return Refuse("overlay_owner_list");
            foreach (nint address in drawOwners) if (!owners.Contains(address)) owners.Add(address);
            // Battle effects are not covered by the native field pause; only the Game Over overlay pilot is.
            if (owners.Count == 0 && FhUtil.get_at<byte>(EngineAddresses.BattleState) != 0) return Refuse("battle_active");
            // Native owners are the field particle programs, which the native field pause covers.
            if (owners.Count == 0 && (nativeOwners.Count == 0 || _fieldPauseInstalled))
                return FhUtil.get_at<byte>(EngineAddresses.GlobalEffectOverlayState) is not (4 or 5) || Refuse("effect_overlay_state_4_5");
            if (nativeOwners.Count != 0) return Refuse("native_owners_present");
            if (owners.Count > PilotOwnerLimit || FhUtil.get_at<int>(GameOverMagicId) != 670 || !pilot_dll_matches(overlayRoot))
                return Refuse("overlay_not_gameover_pilot");
            foreach (nint address in owners)
                if (!drawOwners.Contains(address) || !_particle_pilot_owners.TryGetValue(address, out var owner)
                    || !owner.DrawObserved || !pilot_owner_matches(owner)
                    || !owner.Capture.CanPresent(owner.Manager, owner.Epoch, _particle_pilot_thread)
                    || !pilot_calls_match(owner)) return Refuse("pilot_owner_mismatch");
            _qualificationRefusal = "";
            return true;
        }
        catch (Exception) { return Refuse("exception"); }
    }
    private bool pilot_calls_match(ParticlePilotOwner owner)
    {
        var live = new HashSet<nint>();
        nint obj = pilot_decode(*(uint*)(owner.Manager + 0x30));
        while (obj != 0)
        {
            if (live.Count >= PilotObjectLimit || !live.Add(obj) || !MemoryProbe.IsReadable(obj, 0xa0)) return false;
            obj = pilot_decode(*(uint*)obj);
        }
        var objects = new HashSet<nint>();
        int snapshotBytes = 0;
        foreach (var call in owner.Capture.Calls)
        {
            if (!live.Contains(call.Object) || !MemoryProbe.IsReadable(call.Descriptor, 16)
                || call.Data < _particle_pilot_dll || call.Data >= _particle_pilot_dll + 0x50000
                || !MemoryProbe.IsReadable(call.Data, 4)) return false;
            nint entry = pilot_decode(*(uint*)(call.Object + 8));
            if (!MemoryProbe.IsReadable(entry, 0x10)) return false;
            nint group = pilot_decode(*(uint*)entry);
            if (!MemoryProbe.IsReadable(group, 0x28)) return false;
            int count = *(short*)(group + 0x26);
            int bytes = *(int*)(group + 0x20) + count * 4;
            if (count is < 1 or > 64 || call.ProgramIndex >= count || bytes is < 0xa0 or > 0x10000
                || !MemoryProbe.IsReadable(call.Object, bytes)
                || call.Descriptor != group + 0x28 + call.ProgramIndex * 16) return false;
            if (objects.Add(call.Object))
            {
                if (snapshotBytes > 0x400000 - bytes) return false;
                snapshotBytes += bytes;
            }
            nint kernel = pilot_decode(*(uint*)call.Descriptor);
            (int slot, int wrapper, int api) = call.Kernel switch
            {
                ParticlePresentationKernel.MatrixXyz => (10, 0x1c70, 0x10c),
                ParticlePresentationKernel.DrawMatrix => (11, 0x1c90, 0x120),
                ParticlePresentationKernel.StandardMatrix => (12, 0x1cb0, 0x128),
                ParticlePresentationKernel.DepthOffset => (13, 0x1df0, 0x1b8),
                ParticlePresentationKernel.SimpleGeometry => (14, 0x1e10, 0x1c4),
                ParticlePresentationKernel.BlendedGeometry => (15, 0x1e30, 0x30c),
                ParticlePresentationKernel.GeometryBlur => (16, 0x1cd0, 0x160),
                _ => (-1, 0, 0),
            };
            if (slot < 0 || kernel != _particle_pilot_dll + 0x4a610 + slot * 0x28
                || !MemoryProbe.IsReadable(kernel, 0x28)
                || pilot_pointer(kernel + (slot == 10 ? 8 : 12)) != _particle_pilot_dll + wrapper) return false;
            nint wrapperAddress = _particle_pilot_dll + wrapper;
            if (!MemoryProbe.IsReadable(wrapperAddress, 17)
                || *(uint*)(wrapperAddress + 4) != (uint)(nuint)(_particle_pilot_dll + 0x4ccbc)
                || *(uint*)(wrapperAddress + 10) != api) return false;
        }
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_pilot_worker(ushort family, byte mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint d_pilot_dd(nint scratch, nint pc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint d_pilot_create(nint manager, nint entry);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_pilot_object(nint obj);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_pilot_kernel(nint obj, nint data, nint descriptor);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_pilot_worker(ushort family, byte mode)
    {
        var original = new FhCallHandle<d_pilot_worker>(new FhCallLocation(PilotWorker, 0)).chain_from(h_particle_pilot_worker).fnptr!;
        int thread = Environment.CurrentManagedThreadId;
        if (!RenderPassContext.IsPresentation && family <= 1 && mode <= 1
            && Interlocked.CompareExchange(ref _particle_pilot_thread, thread, 0) == 0)
            reset_particle_presentation();
        if (thread != Volatile.Read(ref _particle_pilot_thread))
        {
            reset_particle_presentation();
            if (!RenderPassContext.IsPresentation) original(family, mode);
            return;
        }
        if (RenderPassContext.IsPresentation && (family > 1 || mode > 1 || !particle_presentation_is_qualified()))
        {
            _logger.Error("[Vrr] Particle presentation rejected an unexpected worker scope.");
            reset_particle_presentation();
            return;
        }
        if (RenderPassContext.IsPresentation && _battleEffectPauseInstalled && !_particle_pilot_worker && !gameover_pilot_applies(family, mode))
        {
            opu_held_walk(original, family, mode);
            return;
        }
        bool previousWorker = _particle_pilot_worker;
        byte previousMode = _particle_pilot_mode;
        if (previousWorker) { reset_particle_presentation(); if (!RenderPassContext.IsPresentation) original(family, mode); return; }
        _particle_pilot_worker = family == 1 && FhUtil.get_at<int>(GameOverMagicId) == 670;
        _particle_pilot_mode = mode;
        try
        {
            if (!RenderPassContext.IsPresentation) { original(family, mode); return; }
            nint root = pilot_pointer((nint)FhUtil.ptr_at<byte>(PilotRoots + family * 4));
            var addresses = new List<nint>();
            if (!pilot_read_list(root, mode, addresses)) { reset_particle_presentation(); return; }
            if (addresses.Count == 0)
            {
                if (_battleEffectPauseInstalled) opu_held_walk(original, family, mode);
                else original(family, mode);
                return;
            }
            if (family != 1 || mode != 1) { reset_particle_presentation(); return; }
            int opcode = FhUtil.get_at<int>(PilotOpcode);
            fixed (ushort* program = PilotProgram)
            {
                uint encoded = unchecked((uint)(nuint)program);
                void InvokeOwner(int index)
                {
                    if (index == addresses.Count) { original(family, mode); return; }
                    var owner = _particle_pilot_owners[addresses[index]];
                    bool restored = ParticlePresentationOwnerScope.Run(new Span<byte>((void*)owner.Address, 0x100),
                        encoded, () => pilot_owner_matches(owner), () => InvokeOwner(index + 1));
                    if (!restored) Environment.FailFast("Particle presentation owner lifetime changed inside a producer-only worker.");
                }
                try { InvokeOwner(0); }
                finally { FhUtil.set_at(PilotOpcode, opcode); }
            }
        }
        finally { _particle_pilot_worker = previousWorker; _particle_pilot_mode = previousMode; }
    }

    // The Game Over overlay keeps its replay pilot while that pilot qualifies; every other walk is held instead.
    private bool gameover_pilot_applies(ushort family, byte mode)
        => family == 1 && mode == 1 && FhUtil.get_at<int>(GameOverMagicId) == 670 && particle_pilot_scene_qualified(_ => false);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private nint h_particle_pilot_dd(nint scratch, nint pc)
    {
        var original = new FhCallHandle<d_pilot_dd>(new FhCallLocation(PilotDd, 0)).chain_from(h_particle_pilot_dd).fnptr!;
        ParticlePilotOwner? previous = _particle_pilot_dd;
        try
        {
            if (_particle_pilot_worker && Environment.CurrentManagedThreadId == _particle_pilot_thread
                && MemoryProbe.IsReadable(scratch, 0x400))
            {
                nint address = pilot_pointer(scratch + 0x220);
                nint root = pilot_pointer((nint)FhUtil.ptr_at<byte>(PilotRoots + 4));
                if (MemoryProbe.IsReadable(address, 0x100) && *(byte*)(address + 0xbb) == 3 && pilot_dll_matches(root))
                {
                    nint manager = pilot_pointer(address + 0xbc), resource = pilot_pointer(address + 0xd0);
                    if (!_particle_pilot_owners.TryGetValue(address, out var owner) || !pilot_owner_matches(owner))
                    {
                        if (_particle_pilot_owners.Count >= PilotOwnerLimit) { reset_particle_presentation(); return original(scratch, pc); }
                        owner = new(root, address, manager, resource, Interlocked.Read(ref _particle_pilot_epoch));
                        _particle_pilot_owners[address] = owner;
                    }
                    if (_particle_pilot_mode == 1) owner.DrawObserved = true;
                    _particle_pilot_dd = owner;
                }
            }
            if (RenderPassContext.IsPresentation && _particle_pilot_dd is { } visualOwner)
            {
                Span<byte> visualState = stackalloc byte[0x40];
                if (!MemoryProbe.TryCopy(visualOwner.Manager + 0x40, visualState))
                {
                    visualOwner.Capture.Invalidate();
                    Interlocked.Exchange(ref _particle_pilot_invalid, 1);
                    return pc + 4;
                }
                try { return original(scratch, pc); }
                finally
                {
                    if (!pilot_owner_matches(visualOwner))
                        Environment.FailFast("Particle manager lifetime changed during a producer-only DD callback.");
                    visualState.CopyTo(new Span<byte>((void*)(visualOwner.Manager + 0x40), 0x40));
                }
            }
            return original(scratch, pc);
        }
        finally { _particle_pilot_dd = previous; }
    }

    private uint particle_pilot_run(nint manager, byte mode, d_ppp_run_part original)
    {
        if (_particle_pilot_dd is not { } owner || owner.Manager != manager
            || Environment.CurrentManagedThreadId != _particle_pilot_thread)
        {
            if (RenderPassContext.IsPresentation) { reset_particle_presentation(); return 0; }
            return original(manager, mode);
        }
        if (RenderPassContext.IsPresentation)
        {
            if (!pilot_owner_matches(owner) || !owner.Capture.CanPresent(manager, owner.Epoch, _particle_pilot_thread)
                || !pilot_calls_match(owner)) { reset_particle_presentation(); return 0; }
            try { return pilot_replay_calls(owner); }
            catch (Exception error)
            {
                owner.Capture.Invalidate();
                Interlocked.Exchange(ref _particle_pilot_invalid, 1);
                _logger.Error($"[Vrr] Particle presentation rejected renderer failure: {error.Message}");
                return 0;
            }
        }
        if (_particle_pilot_capture is not null) { reset_particle_presentation(); return original(manager, mode); }
        _particle_pilot_capture = owner;
        owner.Capture.BeginRealRun(manager, owner.Epoch, _particle_pilot_thread);
        bool completed = false;
        try
        {
            uint result = original(manager, mode);
            if (_particle_pilot_capture == owner) owner.Capture.CompleteRealRun();
            if (result != 0) owner.Capture.Invalidate();
            else Interlocked.Exchange(ref _particle_pilot_invalid, 0);
            completed = true;
            return result;
        }
        finally
        {
            if (!completed) owner.Capture.Invalidate();
            _particle_pilot_capture = null;
        }
    }

    private uint pilot_replay_calls(ParticlePilotOwner owner)
    {
        var savedObjects = new List<(nint Address, long Generation, byte[] Buffer, int Length)>();
        int totalBytes = 0;
        var savedAddresses = new HashSet<nint>();
        int paused = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
        int current = FhUtil.get_at<int>(PilotCurrentManager), index = FhUtil.get_at<int>(PilotProgramIndex);
        bool replaying = _particle_pilot_replaying;
        try
        {
            foreach (var call in owner.Capture.Calls)
            {
                if (!savedAddresses.Add(call.Object)) continue;
                nint entry = pilot_decode(*(uint*)(call.Object + 8));
                nint group = pilot_decode(*(uint*)entry);
                int length = *(int*)(group + 0x20) + *(short*)(group + 0x26) * 4;
                if (length is < 0xa0 or > 0x10000 || totalBytes > 0x400000 - length)
                    throw new InvalidOperationException("Particle visual snapshot exceeds its qualified bound.");
                byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
                savedObjects.Add((call.Object, call.Generation, buffer, length));
                if (!MemoryProbe.TryCopy(call.Object, buffer.AsSpan(0, length)))
                    throw new InvalidOperationException("Particle visual snapshot is no longer readable.");
                totalBytes += length;
            }
            _particle_pilot_replaying = true;
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
            FhUtil.set_at(PilotCurrentManager, (uint)(nuint)owner.Manager);
            foreach (var call in owner.Capture.Calls)
            {
                FhUtil.set_at(PilotProgramIndex, call.ProgramIndex);
                new FhCallHandle<d_pilot_kernel>(new FhCallLocation((nint)(int)call.Kernel, 0))
                    .chain_from(_particle_pilot_kernels[call.Kernel]).fnptr!(call.Object, call.Data, call.Descriptor);
            }
            return 0;
        }
        finally
        {
            if (_particle_pilot_replaying)
            {
                // Renderer-derived matrices and private draw work must not become the next simulation's input.
                if (!pilot_owner_matches(owner) || savedObjects.Any(saved =>
                    !owner.Capture.StillOwns(saved.Address, saved.Generation)
                    || !MemoryProbe.IsReadable(saved.Address, saved.Length)))
                    Environment.FailFast("Particle visual object lifetime changed during a producer-only pass.");
                foreach (var saved in savedObjects)
                    saved.Buffer.AsSpan(0, saved.Length).CopyTo(new Span<byte>((void*)saved.Address, saved.Length));
            }
            foreach (var saved in savedObjects) ArrayPool<byte>.Shared.Return(saved.Buffer);
            FhUtil.set_at(PilotCurrentManager, current);
            FhUtil.set_at(PilotProgramIndex, index);
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, paused);
            _particle_pilot_replaying = replaying;
        }
    }
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private nint h_particle_pilot_create(nint manager, nint entry)
    {
        var original = _particle_create_original ??= new FhCallHandle<d_pilot_create>(new FhCallLocation(0x316770, 0)).chain_from(h_particle_pilot_create).fnptr!;
        nint obj = original(manager, entry);
        field_particle_created(manager, obj);
        if (_particle_sprite_installed && !_particle_sprite_clock.Created(manager, obj)) refuse_particle_sprite_clock();
        if (_particle_pilot_capture is { } owner && owner.Manager == manager
            && Environment.CurrentManagedThreadId == _particle_pilot_thread) owner.Capture.Created(obj);
        return obj;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_pilot_delete(nint obj)
    {
        _fieldDeferral.Forget(obj);
        field_particle_object_changed(obj);
        if (_particle_sprite_installed) _particle_sprite_clock.Deleted(obj);
        if (Environment.CurrentManagedThreadId == _particle_pilot_thread)
            foreach (var owner in _particle_pilot_owners.Values) owner.Capture.Deleted(obj);
        else reset_particle_presentation();
        var original = _particle_delete_original ??= new FhCallHandle<d_pilot_object>(new FhCallLocation(0x3168f0, 0)).chain_from(h_particle_pilot_delete).fnptr!;
        original(obj);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_pilot_reset(nint obj)
    {
        // The dispatcher already set the loop age; ops and init callbacks reset at the next logic step.
        if (_fieldDeferral.TryDeferRestart(obj)) return;
        field_particle_object_changed(obj);
        if (_particle_sprite_installed) _particle_sprite_clock.LoopReset(obj);
        if (Environment.CurrentManagedThreadId == _particle_pilot_thread)
            foreach (var owner in _particle_pilot_owners.Values) owner.Capture.LoopReset(obj);
        else reset_particle_presentation();
        var original = _particle_reset_original ??= new FhCallHandle<d_pilot_object>(new FhCallLocation(0x312d20, 0)).chain_from(h_particle_pilot_reset).fnptr!;
        original(obj);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_particle_pilot_free(nint manager)
    {
        _fieldDeferral.Drop(manager);
        field_particle_manager_changed(manager);
        if (_particle_sprite_installed) _particle_sprite_clock.ResetManager(manager);
        reset_particle_presentation();
        var original = _particle_free_original ??= new FhCallHandle<d_pilot_object>(new FhCallLocation(0x311f20, 0)).chain_from(h_particle_pilot_free).fnptr!;
        original(manager);
    }
}
