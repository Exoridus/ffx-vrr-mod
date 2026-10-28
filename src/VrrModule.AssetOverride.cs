namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Loose files that win over the VBF, without touching the 20.7 GB archive.
///
///     The engine already loads loose files. Its fios open asks <c>BigFileStream::openFile</c>
///     first, and only when that returns null does it call <c>CreateFileW</c> on the relative path
///     - keeping the two results side by side as <c>handle_vbf</c> and <c>handle_os</c>. The
///     existence probe has the same shape around <c>checkExists</c>. So an override needs no
///     archive surgery and no patched fall-through: it needs the archive to say no.
///
///     <para>The path keeps its authored case and its <c>../../../</c> prefix.</para> The tolower
///     and the separator rewrite in that function run on a second, narrow buffer; the wide buffer
///     <c>CreateFileW</c> receives is converted from the caller's string untouched, and the
///     engine's own trace confirms the shape as
///     <c>../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm</c>. The prefix never reaches the
///     filesystem - the archive registers as the bare <c>data\FFX_Data.vbf</c> and opens - so it is
///     stripped here rather than resolved.
///
///     That is all this does. Both hooks answer "not in the archive" for a path the override tree
///     carries, and the engine's own fallback opens the file. Nothing here fabricates a VFile,
///     which is why the override file has to sit at exactly the relative path the engine asks for
///     rather than in a folder of its own: <c>CreateFileW</c> receives that path unchanged and
///     resolves it against the process working directory, which is the game directory by the time
///     any asset is opened. It is not the game directory while this module initialises, which is
///     why the tree is indexed from the executable's own directory instead.
///
///     <para>The hooks are not installed when there is nothing to override.</para> They sit on the
///     path every single file open takes, so a run with an empty override tree pays nothing at all
///     rather than paying a detour per file. When there is something, the cost is one
///     case-insensitive dictionary lookup.
///
///     <para>Both targets are measured thiscall.</para> 412 and 204 bytes, each ending in
///     <c>ret 4</c> - one stack argument, <c>this</c> in ecx - and each opening with the security
///     cookie prologue. The catalog calls checkExists <c>__stdcall</c> with one parameter, which is
///     its blanket default and wrong in the convention.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     Relative path as the engine asks for it to the absolute file that answers it. Built once
    ///     at init and never mutated, so the hooks need no lock.
    ///
    ///     <para>Case-insensitive, because the engine asks in the case the asset was authored in.</para>
    ///     Its own trace prints <c>../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm</c>, so the
    ///     lowercasing in the fios open happens on a different buffer from the one the archive is
    ///     asked with. An ordinal comparer matched nothing at all against this.
    /// </summary>
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private long _override_probes;
    private long _override_hits;

    /// <summary>
    ///     Distinct paths seen by the hooks, bounded, so a run that matches nothing still says what
    ///     the engine actually asked for. Only written while below the cap, which is what keeps a
    ///     hook on every file open from turning into a per-open log line.
    /// </summary>
    private readonly HashSet<string> _probe_samples = new(StringComparer.OrdinalIgnoreCase);

    /* The override dictionary is read-only after init and needs no lock, but this set is written
     * from the hooks, and file opens are not all on the main thread. */
    private readonly System.Threading.Lock _probe_lock = new();

    private const int ProbeSampleCap = 24;

    /// <summary>
    ///     Files in the override tree, then probes seen and probes answered. A file count above
    ///     zero with no probes means the engine never asked for that path - almost always a
    ///     misspelled directory rather than a failed hook.
    /// </summary>
    private string asset_override_counts()
        => $"override={_overrides.Count}/{_override_probes}/{_override_hits}";

    /// <summary>
    ///     Normalises a path the way the two hooks receive it: separators unified, then the prefix
    ///     its callers use stripped. The shapes are the ones the FFX HD file loader established - a
    ///     leading <c>../../../</c>, a leading <c>../../..</c>, or a bare slash. Case is left alone,
    ///     because the caller's string still carries the authored case at this point.
    /// </summary>
    private static string normalise(string path)
    {
        if (path.Contains('\\')) path = path.Replace('\\', '/');

        if (path.StartsWith("../../../", StringComparison.Ordinal)) return path[9..];
        if (path.StartsWith("../../..",  StringComparison.Ordinal)) return path[8..];
        if (path.Length > 0 && path[0] == '/')                      return path[1..];
        return path;
    }

    /// <summary>
    ///     The game directory, taken from the running executable rather than from the working
    ///     directory. The launcher does <c>pushd fahrenheit\bin</c> before it starts the process, so
    ///     the working directory is still that bin folder while this module initialises and every
    ///     override root resolved against it is missing. It is the game directory by the time assets
    ///     are opened - the archive registers as the bare <c>data\FFX_Data.vbf</c> and opens, which
    ///     works from neither the bin folder nor <c>C:\Games</c> - but nothing in the engine does
    ///     that: its only <c>SetCurrentDirectoryA</c> call sits behind the <c>SCE_PHYRE</c>
    ///     environment variable and returns immediately when that is unset.
    /// </summary>
    private static string game_directory()
    {
        string? exe = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(exe) && Path.GetDirectoryName(exe) is { Length: > 0 } dir)
            return Path.GetFullPath(dir);

        return Path.GetFullPath(Directory.GetCurrentDirectory());
    }

    /// <summary>
    ///     Builds the override index and, only when <see cref="VrrConfig.AssetOverride"/> is
    ///     set, installs the two file-layer detours over it. The index itself is shared: the video
    ///     path rewrite reads the same dictionary without any detour on the file layer, so the tree
    ///     is enumerated whenever either consumer wants it.
    /// </summary>
    private bool init_asset_override()
    {
        if (!_config.AssetOverride && !_config.VideoOverride) return true;

        string root = game_directory();

        /* The working directory is logged because it is what the engine's fallback resolves
         * against, and it is not the same thing at init as it is in play: the launcher's pushd is
         * still in force here, while the archive itself is registered as the bare relative
         * "data\FFX_Data.vbf" and opens - which it could not do from the bin folder, and which is
         * the evidence that the game directory is current by then. */
        string cwd = Path.GetFullPath(Directory.GetCurrentDirectory());

        _logger.Info($"[Vrr] Asset override: root={root}, cwd at init={cwd}.");

        foreach (string relative in _config.AssetOverrideRoots)
        {
            string dir = Path.GetFullPath(Path.Combine(root, relative));
            if (!Directory.Exists(dir)) continue;

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                // The key is what the engine will ask for: relative to the game directory with
                // forward slashes, so FFX_Data/GameData/... - the only shape the engine's own
                // CreateFileW fallback can find again. Case is not normalised here because the
                // dictionary ignores it; the engine asks in the authored case. The roots are
                // subtrees of that directory rather than the directory itself, which is what keeps
                // FFX.exe, the fahrenheit tree and the archives out of the index.
                string key = Path.GetRelativePath(root, file).Replace('\\', '/');
                _overrides.TryAdd(key, file);
            }
        }

        if (_overrides.Count == 0)
        {
            _logger.Info($"[Vrr] Asset override: nothing found under {root}, nothing installed.");
            return true;
        }

        if (!_config.AssetOverride)
        {
            foreach ((string key, string file) in _overrides.OrderBy(e => e.Key))
                _logger.Info($"[Vrr] Override file: {key} -> {file} ({new FileInfo(file).Length:N0} bytes)");

            return true;
        }

        foreach ((string key, string file) in _overrides.OrderBy(e => e.Key))
            _logger.Info($"[Vrr] Asset override: {key} -> {file} ({new FileInfo(file).Length:N0} bytes)");

        bool ok = hook_or_log(HookSiteId.AssetOverrideBigFileStreamCheckExists, "BigFileStream::checkExists", EngineAddresses.BigFileCheckExists,
            () => new FhCallHandle<d_big_file_check_exists>(new FhCallLocation(EngineAddresses.BigFileCheckExists, 0))
                .hook(this, with_hook_continuation<d_big_file_check_exists>(h_big_file_check_exists)));

        ok &= hook_or_log(HookSiteId.AssetOverrideBigFileStreamOpenFile, "BigFileStream::openFile", EngineAddresses.BigFileOpenFile,
            () => new FhCallHandle<d_big_file_open_file>(new FhCallLocation(EngineAddresses.BigFileOpenFile, 0))
                .hook(this, with_hook_continuation<d_big_file_open_file>(h_big_file_open_file)));

        return ok;
    }

    /* Measured thiscall on both: each ends in ret 4, so the callee pops the one stack argument and
     * this arrives in ecx. Declared with an explicit this parameter because that is how the
     * framework expresses a thiscall here. */
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate byte d_big_file_check_exists(nint ptr_this, nint path);

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_big_file_open_file(nint ptr_this, nint path);

    /// <summary>
    ///     True when this path is one the override tree answers. Counted separately from the hit so
    ///     a log can tell "the hook never fired" from "the hook fired and matched nothing".
    /// </summary>
    private bool overridden(nint path)
    {
        _override_probes++;

        if (path == 0) return false;

        string? raw = Marshal.PtrToStringAnsi(path);
        if (string.IsNullOrEmpty(raw)) return false;

        string key = normalise(raw);

        if (_probe_samples.Count < ProbeSampleCap)
        {
            bool fresh;
            lock (_probe_lock) fresh = _probe_samples.Count < ProbeSampleCap && _probe_samples.Add(key);
            if (fresh) _logger.Info($"[Vrr] Asset override probe: {key}");
        }

        if (!_overrides.ContainsKey(key)) return false;

        _override_hits++;
        return true;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private byte h_big_file_check_exists(nint ptr_this, nint path)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_big_file_check_exists>(new FhCallLocation(EngineAddresses.BigFileCheckExists, 0)).chain_from(h_big_file_check_exists).fnptr)(ptr_this, path);

        // Zero means "the archive does not have it", which is what sends the caller to CreateFileW.
        if (overridden(path)) return 0;

        return new FhCallHandle<d_big_file_check_exists>(new FhCallLocation(EngineAddresses.BigFileCheckExists, 0))
            .chain_from(h_big_file_check_exists).fnptr!(ptr_this, path);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_big_file_open_file(nint ptr_this, nint path)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_big_file_open_file>(new FhCallLocation(EngineAddresses.BigFileOpenFile, 0)).chain_from(h_big_file_open_file).fnptr)(ptr_this, path);

        // Null is the miss the fios open tests for before it falls through to the filesystem. The
        // caller stores it as handle_vbf either way, so returning it costs nothing to unwind.
        if (overridden(path)) return 0;

        return new FhCallHandle<d_big_file_open_file>(new FhCallLocation(EngineAddresses.BigFileOpenFile, 0))
            .chain_from(h_big_file_open_file).fnptr!(ptr_this, path);
    }
}
