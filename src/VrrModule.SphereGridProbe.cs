namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Sphere Grid draw-side clock, and a measurement of the grid engine.
///
///     The grid draw advances two dwords at +0x116B4 and +0x116B8 on every call (measured: every
///     presentation pass changed both), which animates the triangle pointer at the active menu entry.
///     Stock draws the grid at 30 Hz, so both are put back after every pass except a simulation tick
///     that advances at 30 Hz.
///
///     Measurement: which dwords of the Sphere Grid engine (LpAbilityMapEngine, pointer at 0x1F05834,
///     about 0x116B0 bytes) a presentation pass changes. The grid update is already held on those
///     passes, so anything that still moves is a clock inside the draw.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint SphereGridEngine = 0x1F05834;
    private const int SphereGridBytes = 0x11700;
    private byte[]? _sphereProbeBefore;
    private nint _sphereProbeBase;
    private readonly Dictionary<int, int> _sphereProbeChanges = new();
    private long _sphereProbePasses, _sphereProbeLastLog, _sphereUpdatePasses, _spherePointerHolds;
    private readonly Dictionary<int, int> _sphereUpdateChanges = new();
    private readonly Dictionary<int, (uint First, uint Last, float Min, float Max)> _sphereUpdateValues = new();
    private const int SpherePointerClockA = 0x116B4, SpherePointerClockB = 0x116B8;

    /* The grid update runs at 30 Hz (see h_sphere_grid_update), so the camera and ring move in 30 Hz
     * steps. Measured as the fields a grid update changes while the ring moves: three float pairs
     * (+0x112B8, cam_desired_pos +0x11308, cam_limited_pos +0x11318), a fourth pair at +0x11450 and
     * moving_progress +0x11620. Every pass that does not run the update draws them interpolated
     * between the last two updates; the update's own values are put back when the pass ends. */
    private static readonly int[] SphereSmoothOffsets = [0x112B8, 0x112BC, 0x11308, 0x1130C, 0x11318, 0x1131C, 0x11450, 0x11454, 0x11620];
    private readonly float[] _spherePrev = new float[9], _sphereCur = new float[9];
    private bool _sphereHistory, _sphereSmoothed;
    private nint _sphereSmoothBase;
    private int _sphereTicksSinceUpdate;
    private long _sphereSmoothPasses;

    // After the grid update ran in this pass: its values become the newest history sample.
    private void sphere_record_update()
    {
        nint grid = FhUtil.get_at<nint>(SphereGridEngine);
        if (grid == 0) { _sphereHistory = false; return; }
        bool sameGrid = _sphereHistory && grid == _sphereSmoothBase;
        for (int i = 0; i < SphereSmoothOffsets.Length; i++)
        {
            float v = *(float*)(grid + SphereSmoothOffsets[i]);
            _spherePrev[i] = sameGrid ? _sphereCur[i] : v;
            _sphereCur[i] = v;
        }
        _sphereSmoothBase = grid;
        _sphereHistory = true;
        _sphereTicksSinceUpdate = 0;
    }

    // A pass that skips the grid update: draw the fields between the last two updates.
    private void sphere_smooth(bool presentation)
    {
        if (!presentation) _sphereTicksSinceUpdate = Math.Min(_sphereTicksSinceUpdate + 1, 1);
        nint grid = FhUtil.get_at<nint>(SphereGridEngine);
        if (!_sphereHistory || grid != _sphereSmoothBase || _sphereSmoothed) return;
        float alpha = (float)Math.Clamp((_sphereTicksSinceUpdate + (presentation ? _render_plan.Alpha : 0)) / 2.0, 0, 1);
        for (int i = 0; i < SphereSmoothOffsets.Length; i++)
        {
            float* field = (float*)(grid + SphereSmoothOffsets[i]);
            // A field the update did not leave as written is not ours to blend.
            if (*field != _sphereCur[i]) continue;
            float a = _spherePrev[i], b = _sphereCur[i];
            if (!float.IsFinite(a) || !float.IsFinite(b) || Math.Abs(b - a) > 4096f) continue;
            // moving_progress restarts at 0 on the next link; blending back from 1 would run the ring backwards.
            if (SphereSmoothOffsets[i] == 0x11620 && b < a) continue;
            *field = a + (b - a) * alpha;
        }
        _sphereSmoothed = true;
        _sphereSmoothPasses++;
    }

    private void sphere_smooth_restore()
    {
        if (!_sphereSmoothed) return;
        _sphereSmoothed = false;
        if (FhUtil.get_at<nint>(SphereGridEngine) != _sphereSmoothBase) return;
        for (int i = 0; i < SphereSmoothOffsets.Length; i++) *(float*)(_sphereSmoothBase + SphereSmoothOffsets[i]) = _sphereCur[i];
    }

    private void sphere_probe_enter()
    {
        _sphereProbeBase = FhUtil.get_at<nint>(SphereGridEngine);
        if (_sphereProbeBase == 0 || !MemoryProbe.IsReadable(_sphereProbeBase, SphereGridBytes)) { _sphereProbeBase = 0; return; }
        _sphereProbeBefore ??= new byte[SphereGridBytes];
        new ReadOnlySpan<byte>((void*)_sphereProbeBase, SphereGridBytes).CopyTo(_sphereProbeBefore);
    }

    private void sphere_probe_exit(bool presentation)
    {
        sphere_smooth_restore();
        if (_sphereProbeBase == 0 || _sphereProbeBefore is null) return;
        if (FhUtil.get_at<nint>(SphereGridEngine) == _sphereProbeBase)
        {
            var changes = presentation ? _sphereProbeChanges : _sphereUpdateChanges;
            if (presentation) _sphereProbePasses++; else _sphereUpdatePasses++;
            uint* now = (uint*)_sphereProbeBase;
            fixed (byte* before = _sphereProbeBefore)
            {
                for (int i = 0; i < SphereGridBytes / 4; i++)
                {
                    if (now[i] == ((uint*)before)[i] || i * 4 == SpherePointerClockA || i * 4 == SpherePointerClockB) continue;
                    changes[i * 4] = changes.GetValueOrDefault(i * 4) + 1;
                    if (presentation) continue;
                    float f = *(float*)&now[i];
                    _sphereUpdateValues[i * 4] = _sphereUpdateValues.TryGetValue(i * 4, out var r)
                        ? (r.First, now[i], Math.Min(r.Min, f), Math.Max(r.Max, f))
                        : (((uint*)before)[i], now[i], f, f);
                }
                if (presentation || !advance_this_frame())
                {
                    now[SpherePointerClockA / 4] = ((uint*)before)[SpherePointerClockA / 4];
                    now[SpherePointerClockB / 4] = ((uint*)before)[SpherePointerClockB / 4];
                    _spherePointerHolds++;
                }
            }
        }
        _sphereProbeBase = 0;

        long t = Stopwatch.GetTimestamp();
        if (t - _sphereProbeLastLog < Stopwatch.Frequency * 5 || _sphereProbePasses + _sphereUpdatePasses == 0) return;
        _sphereProbeLastLog = t;
        static string Top(Dictionary<int, int> c) => string.Join(" ", c.OrderByDescending(kv => kv.Value).Take(24).Select(kv => $"+0x{kv.Key:X}:{kv.Value}"));
        if (_sphereUpdateValues.Count > 0)
            _logger.Info("[Vrr] sphere grid values: " + string.Join(" ", _sphereUpdateValues.OrderBy(kv => kv.Key).Take(48)
                .Select(kv => $"+0x{kv.Key:X}=[0x{kv.Value.First:X8}->0x{kv.Value.Last:X8} f{kv.Value.Min:G5}..{kv.Value.Max:G5}]")));
        _sphereUpdateValues.Clear();
        _logger.Info($"[Vrr] sphere grid probe: pointer holds {_spherePointerHolds}, smoothed passes {_sphereSmoothPasses}; {_sphereProbePasses} presentation passes changed {Top(_sphereProbeChanges)}; " +
                     $"{_sphereUpdatePasses} simulation passes changed {Top(_sphereUpdateChanges)}");
        _sphereProbeChanges.Clear();
        _sphereUpdateChanges.Clear();
        _sphereProbePasses = _sphereUpdatePasses = 0;
    }
}
