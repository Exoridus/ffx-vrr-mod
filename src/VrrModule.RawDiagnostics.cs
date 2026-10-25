namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private BinaryTraceSession? _binaryDiagnostics;
    private DiagnosticWatchPlan? _diagnosticWatches;
    private int _diagnosticActorLimit;
    private bool _diagnosticBinaryStoppedReported;
    private long _diagnosticPoseLoop = -1;
    private readonly HashSet<nint> _diagnosticPoseInstances = [];
    private readonly HashSet<(nint Instance, bool Rendered)> _diagnosticPoseSamples = [];

    private void diagnostic_present(int result, uint interval, uint flags, nint swapchain, long timestamp)
    {
        if (_binaryDiagnostics?.IsCapturing != true) return;
        _binaryDiagnostics.TryWrite(15, _frames, _diagnosticLoopId,
            [unchecked((uint)result), interval, flags, unchecked((uint)(nuint)swapchain),
                unchecked((uint)timestamp), unchecked((uint)(timestamp >> 32))]);
    }

    private void diagnostic_actor_pose(nint instance, System.Numerics.Matrix4x4 root,
        ReadOnlySpan<System.Numerics.Matrix4x4> bones, bool rendered)
    {
        if (_binaryDiagnostics?.IsCapturing != true || _diagnosticActorLimit == 0) return;
        long loop = System.Threading.Interlocked.Read(ref _diagnosticLoopCounter);
        // Actor callbacks hold the shared renderer-state lock, including worker-job callbacks.
        if (_diagnosticPoseLoop != loop)
        {
            _diagnosticPoseLoop = loop;
            _diagnosticPoseInstances.Clear();
            _diagnosticPoseSamples.Clear();
        }
        if (!_diagnosticPoseInstances.Contains(instance))
        {
            if (_diagnosticPoseInstances.Count >= _diagnosticActorLimit) return;
            _diagnosticPoseInstances.Add(instance);
        }
        if (!_diagnosticPoseSamples.Add((instance, rendered))) return;
        Span<uint> packet = stackalloc uint[128];
        packet[0] = unchecked((uint)(nuint)instance);
        packet[1] = (uint)(bones.Length + 1);
        packet[4] = _render_simulation_pass ? 1u : 0u;
        packet[5] = 0;
        var rootWords = MemoryMarshal.Cast<System.Numerics.Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(ref root, 1));
        for (int first = 0; first <= bones.Length; first += 7)
        {
            int count = Math.Min(7, bones.Length + 1 - first);
            packet[2] = (uint)first;
            packet[3] = (uint)count;
            for (int index = 0; index < count; index++)
            {
                int matrix = first + index;
                var words = matrix == 0 ? rootWords
                    : MemoryMarshal.Cast<System.Numerics.Matrix4x4, uint>(bones.Slice(matrix - 1, 1));
                words.CopyTo(packet.Slice(6 + index * 16, 16));
            }
            _binaryDiagnostics.TryWrite((ushort)(rendered ? 17 : 16), _frames, loop, packet[..(6 + count * 16)]);
        }
    }

    private void init_raw_diagnostics(string jsonPath)
    {
        if (Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_BINARY") != "1" && !_config.DiagnosticsBinary) return;
        using var process = Process.GetCurrentProcess();
        if (Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_WATCHES") is { Length: > 0 } plan)
        {
            try { _diagnosticWatches = DiagnosticWatchPlan.Load(plan, NativeBuild.ImageSha256, process.MainModule!.ModuleMemorySize); }
            catch (Exception e) { diagnostic_event("watch_plan_rejected", new { error = e.Message }); }
        }
        _diagnosticActorLimit = int.TryParse(Environment.GetEnvironmentVariable("FFX_DIAGNOSTICS_ACTORS"), out int limit)
            ? Math.Clamp(limit, 0, 64) : 8;
        var metadata = new { schema_version = 1, utc = DateTimeOffset.UtcNow, stopwatch_frequency = Stopwatch.Frequency,
            stopwatch_timestamp = Stopwatch.GetTimestamp(), image_base = process.MainModule?.BaseAddress.ToInt64(),
            executable_sha256 = NativeBuild.ImageSha256, json_path = jsonPath, actor_limit = _diagnosticActorLimit,
            native_phase_requested = _config.NativePhaseProbe,
            actor_stride = 0x880, watch_plan = _diagnosticWatches,
            channels = new { animate_enter = 1, animate_exit = 2, loop_enter = 3, loop_exit = 4, camera_input = 5,
                character_enter = 6, character_exit = 7, actor_before = 8, actor_after = 9, watch_before = 10, watch_after = 11,
                actor_catalog = 12, sampling_cost = 13, actor_interpolation = 14, present = 15, canonical_pose = 16, rendered_pose = 17, farplane_composition = 18 },
            actor_interpolation_layout = "captures,blends,skipped,restores,bone_matrices_blended,bone_matrices_unchanged,layout_rejects,tracked_instances,replays; cumulative uint32 counters",
            pose_layout = "instance,total_matrices,first_matrix,matrix_count,simulation_pass,reserved; root matrix first, then full remapped palette, 16 raw uint32 per matrix; up to 7 matrices per packet",
            farplane_composition_layout = FarplaneDiagnosticPacket.Layout,
            farplane_composition_filter = "movie_id=104 or movie_play!=0; movie 104 remains eligible after failed background initialization; active movies provide native state comparison",
            present_layout = "hresult,interval,flags,swapchain,timestamp_low,timestamp_high",
            scalar_layout = "sg_count,v_count,backlog,correction_hz_bits,scale_bits,particle_step,limiter_interval,keep_fps,sync,delta_bits",
            memory_layout = "index,address,byte_offset,valid_byte_count,raw_uint32_words; count=0 means unreadable",
            coverage = "Animate/main-loop/camera/character phases, actual Present results, and canonical/rendered native root and complete remapped bone palettes. Ch slot snapshots remain raw. No input-latency measurement; native acceptance is unverified." };
        _binaryDiagnostics = new BinaryTraceSession(Path.ChangeExtension(jsonPath, ".ffxt"),
            (long)Math.Clamp(_config.DiagnosticsMaxMiB, 8, 2048) * 1024 * 1024, 4096,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        diagnostic_event("binary_capture", new { path = Path.ChangeExtension(jsonPath, ".ffxt"), actor_limit = _diagnosticActorLimit,
            watches = _diagnosticWatches?.Watches.Length ?? 0 });
    }

    private void diagnostic_raw(ushort channel, float delta = 0)
    {
        if (_binaryDiagnostics?.IsCapturing != true) return;
        Span<uint> words = stackalloc uint[10];
        words[0] = FhUtil.get_at<uint>(EngineAddresses.SgCount);
        words[1] = FhUtil.get_at<uint>(EngineAddresses.SgVCount);
        words[2] = unchecked((uint)FhUtil.get_at<int>(EngineAddresses.ElapsedFrameCountFrameSkip));
        words[3] = BitConverter.SingleToUInt32Bits(TargetFramerate);
        words[4] = BitConverter.SingleToUInt32Bits(Scale);
        words[5] = unchecked((uint)_timeline_step);
        words[6] = VSyncInterval;
        words[7] = unchecked((uint)KeepFps);
        words[8] = EngineSyncPaced ? 1u : 0u;
        words[9] = BitConverter.SingleToUInt32Bits(delta);
        _binaryDiagnostics.TryWrite(channel, _frames, _diagnosticLoopId, words);
        if (channel == 4 && _render_scheduler_active)
            _binaryDiagnostics.TryWrite(14, _frames, _diagnosticLoopId, render_actors_counter_words());
    }

    private void diagnostic_memory(bool after)
    {
        if (_binaryDiagnostics?.IsCapturing != true) return;
        long begin = Stopwatch.GetTimestamp();
        try
        {
            Span<byte> buffer = stackalloc byte[0x880];
            nint actors = FhUtil.get_at<nint>(EngineAddresses.ChrArray);
            int nativeCount = FhUtil.get_at<int>(EngineAddresses.NbMaxChr);
            int count = nativeCount is >= 0 and <= 4096 ? Math.Min(nativeCount, _diagnosticActorLimit) : 0;
            _binaryDiagnostics.TryWrite(12, _frames, _diagnosticLoopId,
                [(uint)(after ? 1 : 0), unchecked((uint)(nuint)actors), unchecked((uint)nativeCount), (uint)count]);
            for (int i = 0; i < count; i++)
                diagnostic_memory_record((ushort)(after ? 9 : 8), i, actors == 0 ? 0 : actors + i * 0x880, buffer);
            if (_diagnosticWatches is null) return;
            for (int i = 0; i < _diagnosticWatches.Watches.Length; i++)
            {
                var watch = _diagnosticWatches.Watches[i];
                nint address = (nint)FhUtil.ptr_at<byte>((nint)DiagnosticWatchPlan.RvaValue(watch.Rva));
                foreach (int offset in watch.Offsets ?? [])
                {
                    if (!MemoryProbe.TryCopy(address, buffer[..4])) { address = 0; break; }
                    long target = (long)BitConverter.ToUInt32(buffer[..4]) + offset;
                    if (target is < 65536 or > uint.MaxValue) { address = 0; break; }
                    address = (nint)(nuint)target;
                }
                diagnostic_memory_record((ushort)(after ? 11 : 10), i, address, buffer[..watch.Bytes]);
            }
        }
        catch (Exception e) { diagnostic_event("memory_sample_failed", new { error = e.Message }); }
        finally
        {
            long end = Stopwatch.GetTimestamp();
            _binaryDiagnostics.TryWrite(13, _frames, _diagnosticLoopId,
                [unchecked((uint)begin), unchecked((uint)(begin >> 32)), unchecked((uint)end), unchecked((uint)(end >> 32))]);
        }
    }

    private void diagnostic_memory_record(ushort channel, int index, nint address, Span<byte> buffer)
    {
        bool valid = MemoryProbe.TryCopy(address, buffer);
        Span<uint> words = stackalloc uint[128];
        words[0] = (uint)index; words[1] = unchecked((uint)(nuint)address);
        if (!valid)
        {
            words[2] = words[3] = 0;
            _binaryDiagnostics!.TryWrite(channel, _frames, _diagnosticLoopId, words[..4]);
            return;
        }
        for (int offset = 0; offset < buffer.Length; offset += 496)
        {
            int bytes = Math.Min(496, buffer.Length - offset);
            words[2] = (uint)offset; words[3] = (uint)bytes;
            MemoryMarshal.Cast<byte, uint>(buffer.Slice(offset, bytes)).CopyTo(words[4..]);
            _binaryDiagnostics!.TryWrite(channel, _frames, _diagnosticLoopId, words[..(4 + bytes / 4)]);
        }
    }
}
