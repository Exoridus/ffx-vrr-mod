namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private readonly StartupActivation _startupActivation = new();
    internal bool RuntimeEffectsArmed => _startupActivation.IsArmed;

    private T with_hook_continuation<T>(T callback) where T : Delegate
    {
        // The unresolved handle looks up this exact callback without binding another native target.
        if (_hookRegistration is null || !_hookRegistration.RecordContinuation(callback,
            () => new FhCallHandle<T>(default).chain_from(callback).fnptr is not null))
            throw new InvalidOperationException("The callback has no selected owning registration attempt.");
        return callback;
    }

    private HookRegistrationPlan? _hookRegistration;
    private long _hookRegistrationGeneration;

    private bool prepare_hook_registration(VrrConfig config, bool independent, bool diagnostics, bool binaryDiagnostics,
        IReadOnlyDictionary<HookSiteId, IReadOnlyList<nint>>? templateTargets = null, double? probeRate = null)
    {
        if (_hookRegistration is not null)
        {
            _logger.Error("[Vrr] Hook registration already has an owner plan.");
            return false;
        }
        bool probe = !independent && config.NativePhaseProbe && diagnostics && probeRate == 60;
        if (HookRegistrationPlan.TryCreateResolved(config, independent, diagnostics, binaryDiagnostics,
            templateTargets, out _hookRegistration, out string? error,
            inputProbe: probe && Environment.GetEnvironmentVariable("FFX_VRR_INPUT_PROBE") == "1",
            submissionProbe: probe && Environment.GetEnvironmentVariable("FFX_VRR_SUBMISSION_PROBE") == "1",
            actorProbe: probe && Environment.GetEnvironmentVariable("FFX_VRR_ACTOR_PROBE") == "1")) return true;
        _logger.Error($"[Vrr] Hook registration preflight refused: {error}");
        return false;
    }

    private bool hook_registration_selected(HookSiteId site)
        => _hookRegistration is { State: not (HookRegistrationState.Refused or HookRegistrationState.Disposed) }
            && _hookRegistration.IsSelected(site);

    private bool install_registered_hook(HookSiteId site, nint target, Func<bool> install)
        => HookSites.Install(site, _hookRegistration, target, _hookRegistrationGeneration, install);

    private bool register_hook(HookSiteId site, string name, nint rva, Func<bool> install)
    {
        bool ok = install_registered_hook(site, rva, install);
        diagnostic_event("timing_hook", new { name, rva = (long)rva, installed = ok });
        if (!ok) _logger.Error($"[Vrr] Could not register {name} at RVA 0x{rva:X}: {_hookRegistration?.RefusalReason ?? "owner plan unavailable"}.");
        return ok;
    }

    private bool register_diagnostic_hook(HookSiteId site, string name, nint rva, bool matches, Func<bool> install)
    {
        bool installed = false;
        string? failure = null;
        try { installed = matches && install_registered_hook(site, rva, install); }
        catch (Exception e) { failure = $"{e.GetType().Name}: {e.Message}"; }
        diagnostic_event("probe_hook", new { name, rva = (long)rva, entry_matches = matches, installed, failure });
        if (!installed) _logger.Warning($"[Vrr] Diagnostic hook {name} not installed; entry mismatch or hook refused. Capture coverage is incomplete.");
        return installed;
    }

    private bool register_survey_hook(HookSiteId site, string name, nint rva, Func<bool> install)
    {
        bool installed = install_registered_hook(site, rva, install);
        _logger.Info(installed
            ? $"[Vrr] Probe attached: {name} at RVA 0x{rva:X}."
            : $"[Vrr] Probe did NOT attach: {name} at RVA 0x{rva:X}; its count stays 0 for that reason.");
        return installed;
    }

    private void dispose_hook_registration()
    {
        _hookRegistration?.Dispose();
        System.Threading.Interlocked.Increment(ref _hookRegistrationGeneration);
    }
}
