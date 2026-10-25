namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private DiagnosticSession? _diagnostics;
    private DiagnosticSession? _textureBlendDiagnostics;
    private bool _diagnosticFailureReported;
    private bool _diagnosticSaturationReported;
    private long _diagnosticLastFrame;
    private long _diagnosticFrameStart;
    private long _diagnosticParticleSample;
    private long _diagnosticFmvSample;

    private void diagnostic_fmv_native(long now)
    {
        if (!fmv_playing() || _fmv_selection_manager == 0
            || FhUtil.get_at<nint>(EngineAddresses.FmvPlayerManager) != _fmv_selection_manager
            || Stopwatch.GetElapsedTime(_diagnosticFmvSample, now).TotalMilliseconds < 100) return;
        _diagnosticFmvSample = now;
        Span<byte> player = stackalloc byte[0x2d4];
        if (!MemoryProbe.TryCopy(_fmv_selection_manager + 0x38, player))
        {
            diagnostic_event("fmv_native", new { available = false });
            return;
        }
        diagnostic_event("fmv_native", new { available = true, manager = (long)_fmv_selection_manager,
            state = BitConverter.ToInt32(player[0xe4..]), uploaded_frame = BitConverter.ToUInt32(player[0x100..]),
            frame_duration_ms_bits = BitConverter.ToInt64(player[0x108..]),
            width = BitConverter.ToUInt32(player[0x14c..]), height = BitConverter.ToUInt32(player[0x150..]),
            queue_capacity = BitConverter.ToUInt32(player[0x158..]), frame_buffer_bytes = BitConverter.ToUInt32(player[0x2d0..]) });
    }

    private void diagnostic_particles(long now)
    {
        if (Stopwatch.GetElapsedTime(_diagnosticParticleSample, now).TotalSeconds < 1) return;
        _diagnosticParticleSample = now;
        nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
        if (MemoryProbe.IsReadable(table, 6))
            diagnostic_managers("field", FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers), *(ushort*)(table + 4), FieldManagerCapacity);
        else diagnostic_event("particle_managers", new { family = "field", available = false });
        diagnostic_managers("battle", FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers),
            FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount), 256);
    }

    private void diagnostic_managers(string family, byte* managers, int nativeCount, int cap)
    {
        int count = Math.Clamp(nativeCount, 0, cap);
        if (nativeCount < 0 || !MemoryProbe.IsReadable((nint)managers, Math.Max(1, count * FieldManagerStride)))
        {
            diagnostic_event("particle_managers", new { family, available = false, native_count = nativeCount });
            return;
        }
        List<object> live = [];
        for (int i = 0; i < count; i++)
        {
            byte* manager = managers + i * FieldManagerStride;
            if (family == "field" && *(int*)manager == FieldManagerDead) continue;
            nint first = *(nint*)(manager + 0x30);
            int? age = MemoryProbe.IsReadable(first, 0x10) ? *(int*)(first + 0xc) : null;
            int emitters = *(int*)(manager + 0x18);
            nint array = *(nint*)(manager + 0x3c);
            int? population = emitters == 0 ? 0 : null;
            if (emitters > 0 && emitters <= 4096 && MemoryProbe.IsReadable(array, emitters * 0x10))
            {
                int total = 0;
                for (int e = 0; e < emitters; e++) total += *(ushort*)(array + e * 0x10 + 0xc);
                population = total;
            }
            live.Add(new { index = i, countdown = *(int*)manager, accumulator = *(int*)(manager + 8),
                duration = *(int*)(manager + 4), step = *(int*)(manager + 0x10), emitters, population,
                end_flag = manager[0x1c], empty_flag = manager[0x1d], first_pointer = (long)first, first_age = age });
        }
        diagnostic_event("particle_managers", new { family, available = true, native_count = nativeCount, sampled_count = count, truncated = nativeCount > count, live });
    }
    private const nint DiagnosticFinalize = 0x3BC110;
    private const nint DiagnosticViewCommit = 0x239860;

    private void init_diagnostics()
    {
        if (!_config.Diagnostics && !_config.TextureBlendProbe && Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_BINARY") != "1") return;
        try
        {
            string directory = Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_DIRECTORY")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fahrenheit", "VRR", "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            using var process = Process.GetCurrentProcess();
            var assembly = typeof(VrrModule).Assembly;
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)));
            var session = new DiagnosticSession(path, (long)Math.Clamp(_config.DiagnosticsMaxMiB, 8, 2048) * 1024 * 1024, 8192,
                new { executable_sha256 = NativeBuild.ImageSha256, mod_sha256 = hash,
                    mod_version = assembly.GetName().Version?.ToString(), framework_version = typeof(FhModule).Assembly.GetName().Version?.ToString(),
                    config = _config, process_id = Environment.ProcessId, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    image_base = process.MainModule?.BaseAddress.ToInt64(),
                    managed_modules = AppDomain.CurrentDomain.GetAssemblies().Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString() }).ToArray(),
                    coverage = "animate; main loop/viewport/view commit when armed; vblank/character when installed; framework overlay; actual Present results while presenter is active; movie composition when native phase probe and binary capture are active; no universal draw counter" });
            bool textureOnly = _config.TextureBlendProbe && !_config.Diagnostics
                && Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_BINARY") != "1";
            if (textureOnly) _textureBlendDiagnostics = session;
            else _diagnostics = session;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { _binaryDiagnostics?.Dispose(); session.Dispose(); };
            _logger.Info($"[Vrr] Diagnostics: {path}");
            if (_config.TextureBlendProbe) init_texture_blend_probe();
            if (textureOnly) return;
            try { init_raw_diagnostics(path); }
            catch (Exception e) { diagnostic_event("binary_capture_failed", new { error = e.Message }); }
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                diagnostic_event("unhandled_exception", new { type = e.ExceptionObject.GetType().FullName, detail = e.ExceptionObject.ToString(), terminating = e.IsTerminating });
                session.Dispose();
                _binaryDiagnostics?.Dispose();
            };
            if (_config.NativePhaseProbe)
            {
                init_diagnostic_bindings();
                init_farplane_diagnostics();
                init_menu_water_diagnostics();
                diagnostic_hook("viewport_finalize", DiagnosticFinalize, [0x55, 0x8b, 0xec, 0x83, 0xec, 0x20, 0xc7, 0x05],
                    () => new FhCallHandle<d_diag_finalize>(new FhCallLocation(DiagnosticFinalize, 0)).hook(this, h_diag_finalize));
                diagnostic_hook("view_commit", DiagnosticViewCommit, [0x55, 0x8b, 0xec, 0x81, 0xec, 0xc0, 0x00, 0x00, 0x00],
                    () => new FhCallHandle<d_diag_commit>(new FhCallLocation(DiagnosticViewCommit, 0)).hook(this, h_diag_commit));
            }
        }
        catch (Exception e) { _logger.Error($"[Vrr] Diagnostics unavailable: {e.GetType().Name}: {e.Message}"); }
    }

    private void diagnostic_hook(string name, nint rva, byte[] expected, Func<bool> install)
    {
        bool matches = MemoryProbe.IsReadable((nint)FhUtil.ptr_at<byte>(rva), expected.Length)
            && new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(rva), expected.Length).SequenceEqual(expected);
        bool installed = false;
        string? failure = null;
        try { installed = matches && install(); }
        catch (Exception e) { failure = $"{e.GetType().Name}: {e.Message}"; }
        diagnostic_event("probe_hook", new { name, rva = (long)rva, entry_matches = matches, installed, failure });
        if (!installed) _logger.Warning($"[Vrr] Diagnostic hook {name} not installed; entry mismatch or hook refused. Capture coverage is incomplete.");
    }

    private void diagnostic_event(string kind, object? data = null)
    {
        var session = kind.StartsWith("texture_", StringComparison.Ordinal)
            ? _textureBlendDiagnostics ?? _diagnostics : _diagnostics;
        try { if (session?.IsCapturing == true) session.TryWrite(kind, _frames, new { loop = _diagnosticLoopId, detail = data }); }
        catch (Exception) { /* Diagnostics must not propagate a managed failure through a native hook. */ }
    }

    private object diagnostic_state() => new
    {
        correction_hz = TargetFramerate, scale = Scale, limiter_interval = VSyncInterval,
        actor_interpolation = render_actors_counters(),
        render_scheduler = _render_scheduler_active, simulation_ticks = _simulation_ticks,
        additional_images = _additional_images, discarded_steps = _simulation_debt_dropped,
        presenter_active = _presenter_active, present_attempts = _present_attempts,
        present_successes = _present_successes, present_rejections = _present_rejections, last_present_timestamp = _last_present_timestamp,
        keep_fps = KeepFps, sync = EngineSyncPaced,
        sg_count = FhUtil.get_at<uint>(EngineAddresses.SgCount),
        v_count = FhUtil.get_at<uint>(EngineAddresses.SgVCount),
        backlog = FhUtil.get_at<int>(EngineAddresses.ElapsedFrameCountFrameSkip),
        fmv = fmv_playing(), particle_step = _timeline_step,
        particle_transition_deferred = _timeline_transition_refused,
        last_sampled_particle_objects = _pobj_last, fmv_source_hz = _active_fmv_rate,
        menu_water_expanded = ExpandedMenuWaterActive,
        menu_water_frame = FhUtil.get_at<byte>(EngineAddresses.MenuWaterFrame),
        menu_water_calls = _menu_water_calls, menu_water_advances = _menu_water_advances, menu_water_wraps = _menu_water_wraps
    };

    private void diagnostic_frame_begin()
    {
        if (_diagnostics is null) return;
        diagnostic_raw(1);
        long now = Stopwatch.GetTimestamp();
        double gapMs = _diagnosticLastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(_diagnosticLastFrame, now).TotalMilliseconds;
        if (_diagnostics.Saturated && !_diagnosticSaturationReported)
        {
            _diagnosticSaturationReported = true;
            _logger.Warning("[Vrr] Diagnostic file limit reached; capture stopped. Later events and markers are not recorded. Restart with a larger DiagnosticsMaxMiB for a longer capture.");
        }
        if (!_diagnosticFailureReported && _diagnostics.Failure is { } writerFailure)
        {
            _diagnosticFailureReported = true;
            _logger.Error($"[Vrr] Diagnostic writer stopped: {writerFailure}");
        }
        if (_binaryDiagnostics is { IsCapturing: false } && !_diagnosticBinaryStoppedReported)
        {
            _diagnosticBinaryStoppedReported = true;
            _logger.Warning($"[Vrr] Binary capture stopped: {_binaryDiagnostics.Failure ?? "capture limit reached"}; dropped={_binaryDiagnostics.Dropped}.");
        }
        if (!_diagnostics.IsCapturing) return;
        _diagnosticLastFrame = _diagnosticFrameStart = now;
        diagnostic_particles(now);
        diagnostic_fmv_native(now);
        diagnostic_event("animate_enter", new { gap_ms = gapMs, long_gap = gapMs >= 250, state = diagnostic_state() });

    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_diag_finalize(int viewport);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_diag_commit(float* matrix);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_diag_finalize(int viewport)
    {
        diagnostic_event("viewport_enter", new { viewport });
        int result = new FhCallHandle<d_diag_finalize>(new FhCallLocation(DiagnosticFinalize, 0)).chain_from(h_diag_finalize).fnptr!(viewport);
        if (_diagnostics?.IsCapturing == true) diagnostic_event("viewport_exit", new { viewport, result, view = diagnostic_view() });
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_diag_commit(float* matrix)
    {
        if (_binaryDiagnostics?.IsCapturing == true)
        {
            Span<byte> bytes = stackalloc byte[64];
            if (MemoryProbe.TryCopy((nint)matrix, bytes))
                _binaryDiagnostics.TryWrite(5, _frames, _diagnosticLoopId, MemoryMarshal.Cast<byte, uint>(bytes));
        }
        else if (_diagnostics?.IsCapturing == true)
        {
            uint[]? bits = null;
            if (MemoryProbe.IsReadable((nint)matrix, 64))
            {
                bits = new uint[16];
                new ReadOnlySpan<uint>(matrix, 16).CopyTo(bits);
            }
            diagnostic_event("camera_matrix_input", new { pointer = (long)(nint)matrix, matrix_bits = bits });
        }
        new FhCallHandle<d_diag_commit>(new FhCallLocation(DiagnosticViewCommit, 0)).chain_from(h_diag_commit).fnptr!(matrix);
        diagnostic_event("camera_commit_exit");
    }
}
