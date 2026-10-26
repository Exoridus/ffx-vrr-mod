namespace Fahrenheit.Mods.Vrr;

/// <summary>
/// Selects optional movie dimensions before allocation and rewrites the manager's
/// bounded path before playback. Absolute paths are preferred; long installed
/// paths use a checked relative path tied to the current working directory.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private readonly Dictionary<string, FmvReplacementMetadata?> _fmv_metadata = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _fmv_refused = new(StringComparer.OrdinalIgnoreCase);
    private FmvPlaybackLab? _fmv_lab;
    private string? _fmv_video_directory;
    private int _fmv_lab_index = 0;
    private string _fmv_last_name = "";
    private string _fmv_last_outcome = "not started";

    private long _fmv_starts;
    private long _fmv_rewrites;

    private string video_override_counts()
        => $"fmvpath={_fmv_starts}/{_fmv_rewrites}";

    /// <summary>
    ///     The manager owns 128 bytes here; its update-mode flag starts at +0x570. The 256-byte
    ///     fiosUnifyFilename destination is a separate stack buffer and does not enlarge this field.
    /// </summary>
    private const int FmvPathCapacity = 128;

    private bool init_video_override()
    {
        if (!_config.VideoOverride) return true;
        _fmv_video_directory = Path.Combine(Path.GetDirectoryName(VrrConfig.ResolvePath())!, "video");
        if (_config.FmvLabManifest is { Length: > 0 } manifest)
        {
            try
            {
                _fmv_lab = FmvPlaybackLab.Load(Path.GetFullPath(manifest, Path.GetDirectoryName(VrrConfig.ResolvePath())!));
                diagnostic_event("fmv_lab_ready", new { cases = _fmv_lab.Count, selected = "Original (retail)", experimental = _config.FmvLabExperimentalFormats });
            }
            catch (Exception e) { _logger.Error($"[Vrr] FMV lab unavailable: {e.Message}"); }
        }
        if (_overrides.Count == 0 && _fmv_lab is null && !Directory.Exists(_fmv_video_directory)) return true;

        bool play = hook_or_log(HookSiteId.VideoOverridePhyFMVPlayerManagerPlayStart, "PhyFMVPlayerManager play start", EngineAddresses.FmvPlayStart,
            () => new FhCallHandle<d_fmv_play_start>(new FhCallLocation(EngineAddresses.FmvPlayStart, 0))
                .hook(this, h_fmv_play_start));
        if (!play) return false;
        return init_av1_worker() && init_fmv_audio_hooks() && hook_or_log(HookSiteId.VideoOverridePhyFMVPlayerManagerInitialize, "PhyFMVPlayerManager initialize", EngineAddresses.FmvInitialize,
            () => new FhCallHandle<d_fmv_initialize>(new FhCallLocation(EngineAddresses.FmvInitialize, 0))
                .hook(this, h_fmv_initialize));
    }

    private sealed record FmvSelection(string Original, string Absolute, byte[] Bytes, FmvReplacementMetadata? Metadata)
    {
        public string NativePath { get; init; } = Absolute;
        public bool FromLibrary { get; init; }
        public int? AudioTrack { get; init; }
    }
    private FmvSelection? _fmv_selection;
    private nint _fmv_selection_manager;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_fmv_initialize(nint manager, int movieId, byte highResolution, byte arg3);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte d_fmv_get_name(nint manager, int movieId, byte* buffer);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_fmv_play_start(nint manager, int arg);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_fmv_initialize(nint manager, int movieId, byte highResolution, byte arg3)
    {
        _fmv_selection = null;
        _fmv_selection_manager = manager;
        _fmv_last_outcome = "retail";
        diagnostic_event("fmv_initialize", new { manager = (long)manager, movie_id = movieId, high_resolution = highResolution });
        select_fmv_surface(null);
        try
        {
            byte* buffer = stackalloc byte[128];
            new Span<byte>(buffer, 128).Clear();
            byte found = new FhCallHandle<d_fmv_get_name>(new FhCallLocation(EngineAddresses.FmvGetVideoName, 0))
                .fnptr!(manager, movieId, buffer);
            if (found != 0)
            {
                ReadOnlySpan<byte> name = new(buffer, 128);
                int end = name.IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("Movie name exceeds the native name-buffer capacity.");
                string raw = System.Text.Encoding.ASCII.GetString(name[..end]);
                _fmv_last_name = Path.GetFileName(raw.Replace('\\', '/'));
                diagnostic_event("fmv_asset", new { movie_id = movieId, path = raw, name = _fmv_last_name,
                    lab_case = _fmv_lab?.Label(_fmv_lab_index) });
                if (try_select_fmv(raw, out FmvSelection? selected))
                {
                    // The native initializer allocates luma/chroma surfaces before play begins.
                    select_fmv_surface(selected!.Metadata);
                    _fmv_selection = selected;
                    _fmv_last_outcome = "replacement selected; decode unverified";
                    diagnostic_event("fmv_selected", new { asset = selected.Original, path = selected.Absolute,
                        metadata = selected.Metadata, lab_case = _fmv_lab?.Label(_fmv_lab_index) });
                }
            }
        }
        catch (Exception e)
        {
            _fmv_selection = null;
            _fmv_last_outcome = "override rejected; retail";
            select_fmv_surface(null);
            _logger.Error($"[Vrr] Movie initialization override refused: {e.Message}");
        }

        int result = new FhCallHandle<d_fmv_initialize>(new FhCallLocation(EngineAddresses.FmvInitialize, 0))
            .chain_from(h_fmv_initialize).fnptr!(manager, movieId, highResolution, arg3);
        diagnostic_event("fmv_initialize_result", new { movie_id = movieId, result, replacement = _fmv_selection is not null,
            outcome = _fmv_last_outcome });
        return result;
    }

    private bool try_select_fmv(string raw, out FmvSelection? selection)
    {
        selection = null;
        string? replacement;
        bool library = false;
        if (_fmv_lab is not null)
        {
            replacement = _fmv_lab.Select(_fmv_lab_index, normalise(raw));
            if (replacement is null)
            {
                if (_fmv_lab_index != 0) _fmv_last_outcome = "case targets another movie; retail";
                return false;
            }
        }
        else
        {
            replacement = _fmv_video_directory is null ? null : FmvVideoLibrary.Find(_fmv_video_directory, raw);
            library = replacement is not null;
            if (replacement is null && !_overrides.TryGetValue(normalise(raw), out replacement)) return false;
        }
        if (!library && _fmv_refused.Contains(replacement))
        {
            _fmv_last_outcome = "override rejected; retail";
            return false;
        }
        try
        {
            string absolute = replacement.Replace('\\', '/');
            string nativePath = library ? FmvVideoLibrary.ForNativePlayer(absolute, Environment.CurrentDirectory) : absolute;
            if (nativePath.Any(c => c > 0x7f) || nativePath.Length + 1 > FmvPathCapacity)
                throw new InvalidDataException("Movie override needs an ASCII path shorter than the player buffer.");

            FmvReplacementMetadata? metadata;
            if (library)
            {
                metadata = FmvVideoLibrary.ReadMetadata(replacement);
                ensure_av1_decoder();
            }
            else if (!_fmv_metadata.TryGetValue(replacement, out metadata))
            {
                if (!FmvReplacementMetadata.IsVp8(replacement) && (_fmv_lab is null || !_config.FmvLabExperimentalFormats))
                    throw new InvalidDataException("VP8 video track could not be verified in the bounded WebM header.");
                if (_fmv_lab is not null && !File.Exists(replacement + ".manifest.json"))
                    throw new InvalidDataException("FMV lab candidates require a checked metadata sidecar.");
                metadata = File.Exists(replacement + ".manifest.json")
                    ? FmvReplacementMetadata.LoadValidatedForLab(replacement, _fmv_lab is not null && _config.FmvLabExperimentalFormats) : null;
                if (metadata?.VideoCodec == "av1") ensure_av1_decoder();
                _fmv_metadata[replacement] = metadata;
            }
            int? audio = metadata is null ? null : FmvVideoLibrary.SelectAudio(metadata, raw);
            if (audio is not null && !_fmvAudioHooksReady)
                throw new InvalidOperationException("Optional movie audio filtering is unavailable.");
            selection = new(normalise(raw), absolute, System.Text.Encoding.ASCII.GetBytes(nativePath), metadata)
            {
                NativePath = nativePath, FromLibrary = library, AudioTrack = audio
            };
            return true;
        }
        catch (Exception e)
        {
            _fmv_last_outcome = "override rejected; retail";
            if (!library) _fmv_refused.Add(replacement);
            diagnostic_event("fmv_rejected", new { asset = raw, path = replacement, reason = e.Message });
            _logger.Error($"[Vrr] Movie override refused for {raw}: {e.Message}");
            return false;
        }
    }

    private void ensure_av1_decoder()
    {
        if (!_config.Fmv) throw new InvalidOperationException("AV1 playback requires the FMV update hook.");
        _av1Native ??= new Av1Native(Path.Combine(Path.GetDirectoryName(VrrConfig.ResolvePath())!, "fhav1.dll"));
    }


    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_fmv_play_start(nint manager, int arg)
    {
        try
        {
            rewrite_fmv_path(manager);
            configure_fmv_decode_queue(manager);
            prepare_av1_worker(manager);
        }
        catch (Exception e) { _logger.Error($"[Vrr] FMV path rewrite skipped: {e.Message}"); }

        if (_fmv_selection is { AudioTrack: { } audio } selected && manager == _fmv_selection_manager
            && string.Equals(Marshal.PtrToStringAnsi(manager + EngineAddresses.FmvPathOffset), selected.NativePath, StringComparison.OrdinalIgnoreCase))
            begin_fmv_audio_selection(manager + 0x38, audio);
        else end_fmv_audio_selection();
        try
        {
            return new FhCallHandle<d_fmv_play_start>(new FhCallLocation(EngineAddresses.FmvPlayStart, 0))
                .chain_from(h_fmv_play_start).fnptr!(manager, arg);
        }
        finally { end_fmv_audio_selection(); }
    }

    private void configure_fmv_decode_queue(nint manager)
    {
        FmvSelection? selected = _fmv_selection;
        if (selected?.Metadata is not { } metadata || manager != _fmv_selection_manager) return;
        nint player = manager + 0x38;
        if (!MemoryProbe.IsReadable(player, 0x15c) || *(byte*)(player + 0xc4) == 0
            || *(int*)(player + 0xe4) != 0
            || !string.Equals(Marshal.PtrToStringAnsi(manager + EngineAddresses.FmvPathOffset),
                selected.NativePath, StringComparison.OrdinalIgnoreCase)) return;

        // The native ring allocates (frames + 1) YUV420 images, headers and allocator overhead.
        // Keep its largest block within the already supported 1080p, 30-frame allocation.
        const long budget = (1920L * 1080 * 3 / 2 + 4) * 30 + 1920L * 1080 * 3 / 2 + 31;
        long imageBytes = (long)metadata.Width * metadata.Height * 3 / 2;
        int frames = *(int*)(player + 0x158);
        int bounded = (int)Math.Min(frames, (budget - imageBytes - 31) / (imageBytes + 4));
        if (bounded < 1 || bounded >= frames) return;
        *(int*)(player + 0x158) = bounded;
        _logger.Info($"[Vrr] FMV decoded queue: {frames} -> {bounded} frames for {metadata.Width}x{metadata.Height}.");
    }

    private void rewrite_fmv_path(nint manager)
    {
        _fmv_starts++;
        FmvSelection? selected = _fmv_selection;
        if (selected is null || manager != _fmv_selection_manager
            || !MemoryProbe.IsReadable(manager, EngineAddresses.FmvPathOffset + FmvPathCapacity)) return;

        nint buffer = manager + EngineAddresses.FmvPathOffset;
        string? raw = Marshal.PtrToStringAnsi(buffer);
        if (string.IsNullOrEmpty(raw)) return;
        if (string.Equals(raw.Replace('\\', '/'), selected.NativePath, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(normalise(raw), selected.Original, StringComparison.OrdinalIgnoreCase))
        {
            _fmv_last_outcome = "path mismatch; replacement not applied";
            _logger.Error("[Vrr] Movie path differs from the asset selected before surface allocation; override refused.");
            return;
        }

        if (selected.FromLibrary && (!File.Exists(selected.Absolute)
            || !string.Equals(Path.GetFullPath(selected.NativePath), Path.GetFullPath(selected.Absolute), StringComparison.OrdinalIgnoreCase)))
        {
            _fmv_selection = null;
            _fmv_last_outcome = "optional movie disappeared; retail";
            select_fmv_surface(null);
            return;
        }
        Marshal.Copy(selected.Bytes, 0, buffer, selected.Bytes.Length);
        *(byte*)(buffer + selected.Bytes.Length) = 0;
        _fmv_rewrites++;
        _fmv_last_outcome = "replacement path applied; decode unverified";
        _logger.Info($"[Vrr] FMV path: {raw} -> {selected.Absolute}");
    }

}
