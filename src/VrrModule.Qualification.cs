namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private bool IndependentPresentationEnabled { get; set; }
    private IndependentPresentationQualification _independentQualification = new("Independent presentation has not been qualified.");

    private bool init_vrr_presenter()
        => init_texture_device_observer() & (!IndependentPresentationEnabled || install_vrr_swapchain_hook());

    private bool prepare_presentation_startup()
    {
        _independentQualification = IndependentPresentationQualification.ReadEmbedded();
        IndependentPresentationEnabled = _config.VrrEnabled && _independentQualification.Enabled;
        if (_config.VrrEnabled) _logger.Warning($"[Vrr] Requested independent presentation: {_independentQualification.Reason} Native timing retained.");
        _vsync_interval_target = IndependentPresentationEnabled ? _config.PresentInterval : ChosenFramerate == 30d ? 2u : 1u;
        if (!IndependentPresentationEnabled && ChosenFramerate == 120d)
        {
            _logger.Error("[Vrr] 120 Hz simulation requires qualified independent presentation. No timing hooks installed.");
            return false;
        }
        return true;
    }
}
