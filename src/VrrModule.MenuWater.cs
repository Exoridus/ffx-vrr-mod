namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private nint _expanded_menu_water_table;
    private readonly List<nint> _expanded_menu_water_paths = new();
    private double _expanded_menu_water_carry;
    private readonly PatchJournal _menu_water_patches = new();
    private bool _expanded_menu_water_active;
    private bool ExpandedMenuWaterActive => _expanded_menu_water_active;

    private string[]? _expanded_menu_water_prepared_paths;

    private bool preflight_expanded_menu_water()
    {
        _expanded_menu_water_prepared_paths = null;
        if (string.IsNullOrWhiteSpace(_config.ExpandedMenuWaterPack)) return true;
        try
        {
            if (!_config.MenuWater) throw new InvalidDataException("MenuWater must be enabled for the expanded pack.");
            string packRoot = Path.GetFullPath(_config.ExpandedMenuWaterPack);
            if (!FhApi.Mods.get_modules().Any(context => context.Module.GetType().FullName == "Fahrenheit.Runtime.FhFileLoaderModule"))
                throw new InvalidDataException("Fahrenheit's file-loader module must be loaded for the expanded menu pack.");
            string[] eflRoots = FhApi.Mods.get_mods().SelectMany(mod => new[] {
                Path.Combine(mod.Paths.EflDir.FullName, "x2"), Path.Combine(mod.Paths.EflDir.FullName, "x") }).ToArray();
            Fahrenheit.Mods.Shared.MenuWaterExpansion.ValidateEflRoot(packRoot, eflRoots);
            string[] paths = Fahrenheit.Mods.Shared.MenuWaterExpansion.ValidatePack(packRoot);
            uint imageBase = checked((uint)(nuint)FhUtil.ptr_at<byte>(0));
            Fahrenheit.Mods.Shared.MenuWaterExpansion.Plan(imageBase, 0,
                (rva, length) => new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(rva), length).ToArray());
            _expanded_menu_water_prepared_paths = paths;
            return true;
        }
        catch (Exception error)
        {
            // The pack is an optional add-on; without it the native water frames are used.
            _logger.Warning($"Expanded menu water pack not used: {error.Message}");
            return true;
        }
    }

    private bool init_expanded_menu_water()
    {
        if (_expanded_menu_water_prepared_paths is not { } paths) return true;
        try
        {
            nint table = Marshal.AllocHGlobal(140 * 4);
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    nint path = Marshal.StringToHGlobalAnsi(paths[i]);
                    _expanded_menu_water_paths.Add(path);
                    Marshal.WriteInt32(table, i * 4, unchecked((int)path));
                }
                uint imageBase = checked((uint)(nuint)FhUtil.ptr_at<byte>(0));
                var writes = Fahrenheit.Mods.Shared.MenuWaterExpansion.Plan(imageBase, checked((uint)(nuint)table),
                    (rva, length) => new ReadOnlySpan<byte>(FhUtil.ptr_at<byte>(rva), length).ToArray());
                _expanded_menu_water_table = table;
                foreach (var write in writes) _menu_water_patches.Write(write.Rva, write.Bytes);
                _expanded_menu_water_active = true;
                _menu_water_patches.Write(EngineAddresses.MenuWaterFrame, [0]);
                _logger.Info("Expanded menu water loaded: 140 slots at 60 Hz. Native playback and visual quality require validation.");
                return true;
            }
            catch { if (_expanded_menu_water_table == 0) Marshal.FreeHGlobal(table); else _menu_water_patches.RestoreAll(); throw; }
        }
        catch (Exception error)
        {
            if (_expanded_menu_water_table == 0) foreach (nint path in _expanded_menu_water_paths) Marshal.FreeHGlobal(path);
            _expanded_menu_water_paths.Clear();
            _logger.Error($"Expanded menu water disabled: {error.Message}");
            return false;
        }
    }

    private void restore_expanded_menu_water()
    {
        _menu_water_patches.RestoreAll();
        // The texture manager can retain path pointers. Keep the allocation alive until process teardown.
    }
    private byte expanded_menu_water_next(byte before)
    {
        _expanded_menu_water_carry += RenderPassContext.IsActive
            ? 60d * RenderPassContext.TakeWaterSeconds() : 60d / Math.Max(1f, TargetFramerate);
        int steps = (int)_expanded_menu_water_carry;
        _expanded_menu_water_carry -= steps;
        return (byte)((before + steps) % 140);
    }
}
