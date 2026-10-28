using Fahrenheit.Mods.Shared;
using TerraFX.Interop.DirectX;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint WaterClusterGet = 0x29b4b0;
    private const nint WaterClusterLoad = 0x29b940;
    private const nint WaterTextureIterator = 0x63bf0;
    private const nint WaterShaderTexture = 0x26e4f0;
    private const nint WaterAssetLoad = 0x406c0;
    private readonly object _waterDiagnosticLock = new();
    private readonly Dictionary<nint, HashSet<int>> _waterClusters = [];
    private readonly Dictionary<nint, HashSet<int>> _waterTextures = [];
    private readonly Dictionary<nint, HashSet<int>> _waterResources = [];
    private readonly MenuWaterDiagnosticWindow _waterDiagnosticWindow = new(6000);
    private int _waterDiagnosticStopReported;
    private bool WaterDiagnosticCapturing => _diagnostics?.IsCapturing == true
        && _waterDiagnosticWindow.IsCapturing;

    private void init_menu_water_diagnostics()
    {
        diagnostic_hook(HookSiteId.MenuWaterDiagnosticsWaterAssetLoad, "water_asset_load", WaterAssetLoad, [0x55, 0x8b, 0xec, 0x6a, 0xff],
            () => new FhCallHandle<d_water_asset>(new FhCallLocation(WaterAssetLoad, 0)).hook(this, with_hook_continuation<d_water_asset>(h_water_asset)));
        foreach (var context in FhApi.Mods.get_modules())
        {
            var module = context.Module;
            if (module.GetType().FullName != "Fahrenheit.Runtime.FhFileLoaderModule") continue;
            var field = module.GetType().GetField("_index", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(module) is not IDictionary<string, string> index)
            {
                diagnostic_event("water_efl_index", new { available = false });
                continue;
            }
            for (int slot = 0; slot < 140; slot++)
            {
                string key = $"/FFX_DATA/GAMEDATA/PS3DATA/MENU/TITL/D3D11/WATER_{slot:00}.DDS.PHYRE";
                bool found = index.TryGetValue(key, out string? selected);
                diagnostic_event("water_efl_index", new { available = true, slot, found, selected });
            }
        }
        diagnostic_hook(HookSiteId.MenuWaterDiagnosticsWaterClusterGet, "water_cluster_get", WaterClusterGet, [0x55, 0x8b, 0xec, 0x53, 0x56, 0x57, 0xff, 0x75, 0x08],
            () => new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterGet, 0)).hook(this, with_hook_continuation<d_water_cluster>(h_water_cluster_get)));
        diagnostic_hook(HookSiteId.MenuWaterDiagnosticsWaterClusterLoad, "water_cluster_load", WaterClusterLoad, [0x55, 0x8b, 0xec, 0x81, 0xec, 0x0c, 0x01, 0x00, 0x00],
            () => new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterLoad, 0)).hook(this, with_hook_continuation<d_water_cluster>(h_water_cluster_load)));
        diagnostic_hook(HookSiteId.MenuWaterDiagnosticsWaterTextureIterator, "water_texture_iterator", WaterTextureIterator, [0x55, 0x8b, 0xec, 0x8b, 0x45, 0x08, 0x83, 0xc0, 0x1c],
            () => new FhCallHandle<d_water_iterator>(new FhCallLocation(WaterTextureIterator, 0)).hook(this, with_hook_continuation<d_water_iterator>(h_water_iterator)));
        diagnostic_hook(HookSiteId.MenuWaterDiagnosticsWaterShaderTexture, "water_shader_texture", WaterShaderTexture, [0x55, 0x8b, 0xec, 0x83, 0xec, 0x10, 0x53, 0x57, 0x8b, 0x7d, 0x08, 0x8b, 0xd9],
            () => new FhCallHandle<d_water_shader>(new FhCallLocation(WaterShaderTexture, 0)).hook(this, with_hook_continuation<d_water_shader>(h_water_shader)));
    }

    private void water_event(string kind, object detail)
    {
        if (_waterDiagnosticWindow.TryRecord()) diagnostic_event(kind, detail);
        diagnostic_water_stop();
    }

    private void diagnostic_water_stop()
    {
        if (_waterDiagnosticWindow.StopReason is { } reason
            && System.Threading.Interlocked.Exchange(ref _waterDiagnosticStopReported, 1) == 0)
            diagnostic_event("water_capture_stopped", new { reason, event_limit = 6000,
                coverage = "first title load only; ordinary/indexed draws, PS slots 0..7; later pointer reuse is not tracked" });
    }

    private void diagnostic_water_load_begin(nint path)
    {
        if (!WaterDiagnosticCapturing) return;
        try
        {
            Span<byte> bytes = stackalloc byte[256];
            if (!MemoryProbe.TryCopy(path, bytes)) return;
            int slot = MenuWaterDiagnostic.Slot(bytes);
            // Startup can retry the preload before drawing; discard identities from its previous attempt.
            if (slot == 0)
            {
                lock (_waterDiagnosticLock)
                {
                    _waterDiagnosticWindow.BeginLoad(slot);
                    _waterClusters.Clear();
                    _waterTextures.Clear();
                    _waterResources.Clear();
                }
            }
            diagnostic_water_stop();
        }
        catch (Exception) { /* Observation must not prevent the requested load. */ }
    }

    private static void water_associate(Dictionary<nint, HashSet<int>> map, nint key, IEnumerable<int> slots)
    {
        if (key == 0) return;
        if (!map.TryGetValue(key, out var known)) map[key] = known = [];
        known.UnionWith(slots);
    }

    private void diagnostic_water_cluster(string operation, nint path, nint cluster)
    {
        if (!WaterDiagnosticCapturing) return;
        try
        {
            Span<byte> bytes = stackalloc byte[256];
            if (!MemoryProbe.TryCopy(path, bytes)) return;
            int slot = MenuWaterDiagnostic.Slot(bytes);
            if (slot < 0) return;
            lock (_waterDiagnosticLock) water_associate(_waterClusters, cluster, [slot]);
            water_event("water_cluster", new { operation, slot, cluster = (long)cluster });
        }
        catch (Exception) { /* Diagnostic failure must not change the native result. */ }
    }

    private void diagnostic_water_texture(nint iterator, nint cluster)
    {
        if (!WaterDiagnosticCapturing) return;
        try
        {
            int[] slots;
            lock (_waterDiagnosticLock)
            {
                if (!_waterClusters.TryGetValue(cluster, out var known)) return;
                slots = known.Order().ToArray();
            }
            Span<byte> list = stackalloc byte[0x20];
            Span<byte> texture = stackalloc byte[0x2c];
            if (!MemoryProbe.TryCopy(iterator, list)) return;
            uint pointer = BitConverter.ToUInt32(list);
            int count = BitConverter.ToInt32(list[0xc..]);
            bool readable = count > 0 && MemoryProbe.TryCopy((nint)pointer, texture);
            uint resource = 0, width = 0, height = 0;
            bool valid = readable && MenuWaterDiagnostic.Texture(texture, out resource, out width, out height);
            if (valid)
            {
                lock (_waterDiagnosticLock)
                {
                    water_associate(_waterTextures, (nint)pointer, slots);
                    water_associate(_waterResources, (nint)resource, slots);
                }
            }
            water_event("water_texture", new { slots, cluster = (long)cluster, texture = pointer,
                count, readable, valid, resource, width, height });
        }
        catch (Exception) { /* Diagnostic failure must not change native texture selection. */ }
    }

    private void diagnostic_water_shader(nint parameter, nint value, int result)
    {
        if (!WaterDiagnosticCapturing) return;
        try
        {
            Span<byte> words = stackalloc byte[16];
            if (!MemoryProbe.TryCopy(value, words)) return;
            uint texture = BitConverter.ToUInt32(words[12..]);
            int[] slots;
            lock (_waterDiagnosticLock)
            {
                if (!_waterTextures.TryGetValue((nint)texture, out var known)) return;
                slots = known.Order().ToArray();
            }
            water_event("water_shader_texture", new { slots, texture, parameter = (long)parameter,
                type = BitConverter.ToUInt32(words), result });
        }
        catch (Exception) { /* Diagnostic failure must not escape the native hook. */ }
    }

    private void diagnostic_water_draw(ID3D11DeviceContext* context, string kind, uint count, uint start, int baseVertex)
    {
        if (!WaterDiagnosticCapturing) return;
        ID3D11ShaderResourceView** views = stackalloc ID3D11ShaderResourceView*[8];
        new Span<nint>(views, 8).Clear();
        try
        {
            lock (_waterDiagnosticLock) if (_waterResources.Count == 0) return;
            // PSGetShaderResources/GetResource add references; release every returned interface.
            context->PSGetShaderResources(0, 8, views);
            for (int sampler = 0; sampler < 8; sampler++)
            {
                if (views[sampler] == null) continue;
                ID3D11Resource* resource = null;
                views[sampler]->GetResource(&resource);
                if (resource == null) continue;
                try
                {
                    int[] slots;
                    lock (_waterDiagnosticLock)
                    {
                        if (!_waterResources.TryGetValue((nint)resource, out var known)) continue;
                        _waterDiagnosticWindow.BeginDraw();
                        slots = known.Order().ToArray();
                    }
                    water_event("water_gpu_draw", new { kind, count, start, base_vertex = baseVertex, sampler,
                        slots, resource = (long)(nint)resource, view = (long)(nint)views[sampler],
                        water_call = _menu_water_calls, presents = _present_successes });
                }
                finally { resource->Release(); }
            }
        }
        catch (Exception) { /* Observation cannot suppress the native draw. */ }
        finally { for (int i = 0; i < 8; i++) if (views[i] != null) views[i]->Release(); }
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint d_water_cluster(nint manager, nint path);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate void d_water_iterator(nint iterator, nint cluster);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int d_water_shader(nint instance, nint parameter, nint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_water_asset(nint output, nint path, uint identity);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_water_asset(nint output, nint path, uint identity)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_water_asset>(new FhCallLocation(WaterAssetLoad, 0)).chain_from(h_water_asset).fnptr)(output, path, identity);

        int result = new FhCallHandle<d_water_asset>(new FhCallLocation(WaterAssetLoad, 0))
            .chain_from(h_water_asset).fnptr!(output, path, identity);
        if (WaterDiagnosticCapturing)
        {
            try
            {
                Span<byte> bytes = stackalloc byte[256];
                if (MemoryProbe.TryCopy(path, bytes))
                {
                    int slot = MenuWaterDiagnostic.Slot(bytes);
                    if (slot >= 0) water_event("water_asset_result", new { slot, result, identity });
                }
            }
            catch (Exception) { /* Retain the loader's result even when observation fails. */ }
        }
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_water_cluster_get(nint manager, nint path)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterGet, 0)).chain_from(h_water_cluster_get).fnptr)(manager, path);

        nint result = new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterGet, 0))
            .chain_from(h_water_cluster_get).fnptr!(manager, path);
        diagnostic_water_cluster("lookup", path, result);
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private nint h_water_cluster_load(nint manager, nint path)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterLoad, 0)).chain_from(h_water_cluster_load).fnptr)(manager, path);

        diagnostic_water_load_begin(path);
        nint result = new FhCallHandle<d_water_cluster>(new FhCallLocation(WaterClusterLoad, 0))
            .chain_from(h_water_cluster_load).fnptr!(manager, path);
        diagnostic_water_cluster("load", path, result);
        return result;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private void h_water_iterator(nint iterator, nint cluster)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_water_iterator>(new FhCallLocation(WaterTextureIterator, 0)).chain_from(h_water_iterator).fnptr)(iterator, cluster);
            return;
        }

        new FhCallHandle<d_water_iterator>(new FhCallLocation(WaterTextureIterator, 0))
            .chain_from(h_water_iterator).fnptr!(iterator, cluster);
        diagnostic_water_texture(iterator, cluster);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private int h_water_shader(nint instance, nint parameter, nint value)
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_water_shader>(new FhCallLocation(WaterShaderTexture, 0)).chain_from(h_water_shader).fnptr)(instance, parameter, value);

        int result = new FhCallHandle<d_water_shader>(new FhCallLocation(WaterShaderTexture, 0))
            .chain_from(h_water_shader).fnptr!(instance, parameter, value);
        diagnostic_water_shader(parameter, value, result);
        return result;
    }
}
