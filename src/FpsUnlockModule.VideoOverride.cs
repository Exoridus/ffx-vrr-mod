namespace Fahrenheit.Mods.FpsUnlock;

/// <summary>
///     Replacing an FMV without touching the 20.7 GB archive, and without a detour on the file
///     layer.
///
///     <para>The path is data, not a literal.</para> PhyFMVPlayerManager keeps the full path of the
///     clip it is about to play in its own buffer at instance+0x4F0 - prefix and authored case
///     intact, <c>../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm</c> - and the playback entry
///     reads it from there. So one hook on that entry, which runs once per video, does what two
///     hooks on <c>BigFileStream::checkExists</c> and <c>openFile</c> did while answering for every
///     file the engine opens. Those two are what <see cref="FpsUnlockConfig.AssetOverride"/> still
///     installs and what crashed the process twice.
///
///     <para>The replacement is written as an absolute path, and that is the whole trick.</para>
///     It settles both unknowns at once. The archive hashes the path from
///     <c>stream_prefix_strlen</c> bytes in, so an absolute path is hashed from its ninth character
///     and cannot match any digest in the index - the archive misses by construction, with nothing
///     patched and nothing hooked. The engine's fallback then calls <c>CreateFileW</c> on that same
///     string, and an absolute path resolves the same whatever the working directory happens to be
///     at that moment, which a relative one does not: the launcher leaves the process in
///     <c>fahrenheit\bin</c> and nothing in the engine sets it back.
/// </summary>
public unsafe sealed partial class FpsUnlockModule
{
    private long _fmv_starts;
    private long _fmv_rewrites;

    private string video_override_counts()
        => $"fmvpath={_fmv_starts}/{_fmv_rewrites}";

    /// <summary>
    ///     The engine reads this buffer through fiosUnifyFilename with a 256 byte bound, so that is
    ///     the limit a replacement has to respect; the fields around it start 0x1E0 bytes further on.
    /// </summary>
    private const int FmvPathCapacity = 256;

    private bool init_video_override()
    {
        if (!_config.VideoOverride || _overrides.Count == 0) return true;

        foreach ((string key, string file) in _overrides)
        {
            if (key.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                warn_unless_vp8(key, file);
        }

        return hook_or_log("PhyFMVPlayerManager play start", EngineAddresses.FmvPlayStart,
            () => new FhMethodHandle<d_fmv_play_start>(new FhMethodLocation(EngineAddresses.FmvPlayStart, 0))
                .hook(this, h_fmv_play_start));
    }

    /// <summary>
    ///     Warns about a replacement the player will silently refuse. Every one of the 95 shipped
    ///     FMVs is VP8, and a VP8 replacement plays while a VP9 one produces no picture, no error
    ///     and no failed open - the file is read and nothing happens, which costs a run to diagnose
    ///     and looks exactly like a broken override.
    ///
    ///     The test is the Matroska CodecID, which is a plain ASCII string in the track header near
    ///     the front of the file. Read rather than parsed: a full EBML walk buys nothing here, and
    ///     anything unreadable is passed over rather than reported, because this is advice and not
    ///     a gate.
    /// </summary>
    private void warn_unless_vp8(string key, string file)
    {
        try
        {
            byte[] head = new byte[4096];
            int read;

            using (FileStream stream = File.OpenRead(file))
                read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

            string text = System.Text.Encoding.ASCII.GetString(head, 0, read);

            if (text.Contains("V_VP8", StringComparison.Ordinal)) return;

            string codec = text.Contains("V_VP9", StringComparison.Ordinal) ? "VP9" : "not VP8";

            _logger.Info($"[FpsUnlock] Override {key} is {codec}. The player decodes VP8 only and will show nothing, without an error.");
        }
        catch
        {
            // Advice only. A file that cannot be read here is still worth handing to the engine.
        }
    }

    /* Measured thiscall: the manager arrives in ecx and the one argument on the stack. */
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_fmv_play_start(nint manager, int arg);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_fmv_play_start(nint manager, int arg)
    {
        try
        {
            rewrite_fmv_path(manager);
        }
        catch (Exception e)
        {
            // A failed rewrite must never cost the video. The original path is still in the buffer.
            _logger.Info($"[FpsUnlock] FMV path rewrite skipped: {e.Message}");
        }

        return new FhMethodHandle<d_fmv_play_start>(new FhMethodLocation(EngineAddresses.FmvPlayStart, 0))
            .chain_from(h_fmv_play_start).fnptr!(manager, arg);
    }

    private void rewrite_fmv_path(nint manager)
    {
        if (!MemoryProbe.IsReadable(manager, EngineAddresses.FmvPathOffset + FmvPathCapacity)) return;

        nint buffer = manager + EngineAddresses.FmvPathOffset;

        string? raw = Marshal.PtrToStringAnsi(buffer);
        if (string.IsNullOrEmpty(raw)) return;

        _fmv_starts++;

        if (!_overrides.TryGetValue(normalise(raw), out string? replacement))
        {
            _logger.Info($"[FpsUnlock] FMV path: {raw} (no override)");
            return;
        }

        /* Forward slashes throughout: CreateFileW takes them, and it keeps the string in the shape
         * the rest of the path handling expects to see. */
        string absolute = replacement.Replace('\\', '/');

        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(absolute);

        if (bytes.Length + 1 > FmvPathCapacity)
        {
            _logger.Info($"[FpsUnlock] FMV path: {absolute} is {bytes.Length} bytes and does not fit, left alone.");
            return;
        }

        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        *(byte*)(buffer + bytes.Length) = 0;

        _fmv_rewrites++;
        _logger.Info($"[FpsUnlock] FMV path: {raw} -> {absolute}");
    }
}
