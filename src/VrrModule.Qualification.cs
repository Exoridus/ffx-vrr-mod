namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private bool IndependentPresentationEnabled { get; set; }
    private bool NativeRenderValidationEnabled { get; set; }
    private IndependentPresentationQualification _independentQualification = new("Independent presentation has not been qualified.");

    private bool init_vrr_presenter()
        => init_texture_device_observer() & (!IndependentPresentationEnabled || install_vrr_swapchain_hook());

    private bool prepare_presentation_startup()
    {
        _independentQualification = IndependentPresentationQualification.ReadEmbedded();
        NativeRenderValidationEnabled = _independentQualification.AllowsNativeValidation(
            Environment.GetEnvironmentVariable("FFX_VRR_RENDER_PROBE"), _config.VrrEnabled,
            _config.Diagnostics, _config.NativePhaseProbe, ChosenFramerate ?? 60d);
        IndependentPresentationEnabled = _config.VrrEnabled && (_independentQualification.Enabled || NativeRenderValidationEnabled);
        if (NativeRenderValidationEnabled)
            _logger.Warning("[Vrr] Experimental native render validation requested. Whole-game qualification remains pending; live per-image guards remain required.");
        else if (_config.VrrEnabled)
            _logger.Warning($"[Vrr] Requested independent presentation: {_independentQualification.Reason} Native timing retained.");
        _vsync_interval_target = IndependentPresentationEnabled ? _config.PresentInterval : ChosenFramerate == 30d ? 2u : 1u;
        if (!IndependentPresentationEnabled && ChosenFramerate == 120d)
        {
            _logger.Error("[Vrr] 120 Hz simulation requires qualified independent presentation. No timing hooks installed.");
            return false;
        }
        return true;
    }
}
