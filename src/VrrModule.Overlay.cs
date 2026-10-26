using Hexa.NET.ImGui;
using System.Numerics;

namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     A diagnostic ImGui overlay for play-testing: it puts the same clock the log stamps every
///     line with on screen, so a visual defect that leaves no log line of its own can still be
///     matched to the log by eye, and a compact counter view that carries evidence in a screenshot
///     alone.
///
///     Draw-only. It reads counters the other subsystems already maintain for the telemetry line
///     and reuses their formatting rather than duplicating it; it writes nothing back into them,
///     never calls <see cref="advance_this_frame"/>, and holds no state any correction reads.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private long _overlay_marker_count;
    private uint _reset_input_state = uint.MaxValue;

    public override void render_imgui()
    {
        if (!_initialized) return;
        presenter_overlay_begin();
        render_fps_overlay();
        diagnostic_reset_input();
        diagnostic_event("framework_overlay" );
        if (!_config.TimerOverlay && !_config.Diagnostics) return;

        if (ImGui.Begin("Frame Timer"))
        {
            ImGui.Text($"Reset input: gamepad={(_reset_input_state & 1) != 0} " +
                $"L1={(_reset_input_state & 2) != 0} R1={(_reset_input_state & 4) != 0} " +
                $"Start={(_reset_input_state & 8) != 0}");
            // Deliberately drawn at the default font size. Scaling it needs a PushFont overload
            // that nothing in either repository exercises, and this draws from h_present on every
            // presented frame - the one call site where an unproven ImGui call costs a crash rather
            // than a wrong pixel.
            ImGui.Text($"t={ElapsedSeconds:F1}s" );
            if (_fmv_lab is not null)
            {
                ImGui.Text($"Last movie: {_fmv_last_name} - {_fmv_last_outcome}");
                ImGui.BeginDisabled(fmv_playing());
                if (ImGui.BeginCombo("Next FMV", _fmv_lab.Label(_fmv_lab_index)))
                {
                    for (int i = 0; i <= _fmv_lab.Count; i++)
                        if (ImGui.Selectable(_fmv_lab.Label(i), i == _fmv_lab_index))
                        {
                            _fmv_lab_index = i;
                            diagnostic_event("fmv_lab_case", new { selected = _fmv_lab.Label(i) });
                        }
                    ImGui.EndCombo();
                }
                ImGui.EndDisabled();
            }
            if (_diagnostics?.Saturated == true) ImGui.Text("Diagnostic file limit reached - capture stopped" );

            // Same "u" format and the same UTC source the logger stamps every line with, so a
            // symptom read off this line can be matched to a log line by eye.
            ImGui.Text($"{TimeProvider.System.GetUtcNow():u}");

            ImGui.Separator();

            if (ImGui.Button("Marker"))
            {
                _overlay_marker_count++;
                diagnostic_event("marker", new { marker = _overlay_marker_count, state = diagnostic_state() });

                // The whole counter set, not just the time. A marker exists to capture the state at
                // the instant a defect was seen; the periodic sample is up to five seconds away from
                // that instant in either direction, which is long enough for the counters that move
                // per frame to say nothing about it.
                _logger.Info($"[Vrr] MARKER {_overlay_marker_count} at t={ElapsedSeconds:F1}, " +
                             $"scale={Scale:F3}, {counter_snapshot()}");
            }

            ImGui.Separator();
            ImGui.Text($"correction={TargetFramerate:F2}Hz scale={Scale:F3}");
            if (_config.VrrEnabled && !IndependentPresentationEnabled)
                ImGui.Text($"Independent presentation requested; native timing: {_independentQualification.Reason}");
            if (IndependentPresentationEnabled)
            {
                ImGui.Text($"Render cap={PresentationTargetHz:F0} FPS (0=unlimited); simulation={TargetFramerate:F0} Hz");
            }
            if (_render_scheduler_active)
                ImGui.Text($"Sim ticks={_simulation_ticks} extra images={_additional_images} " +
                    $"render gate={(_render_frame_enabled ? "open" : "closed: " + _render_gate_reason())}");
            ImGui.Text($"Water: {(ExpandedMenuWaterActive ? "140 frames / 60 Hz" : "retail 70 frames")} " +
                $"index={FhUtil.get_at<byte>(EngineAddresses.MenuWaterFrame)} wraps={_menu_water_wraps}");
            ImGui.Text(atel_worker_motion_counts());
            ImGui.Text(idle_sway_counts());
            ImGui.Text(neck_counts());
            ImGui.Text(motion_sequence_counts());
            ImGui.Text(cross_fade_counts());
            ImGui.Text(particle_counts());
            ImGui.Text(buoyancy_counts());
        }

        ImGui.End();
    }

    private readonly FrameRateCounter _fps_counter = new();
    private long _fps_rendered, _fps_simulated;

    private void render_fps_overlay()
    {
        // Fahrenheit calls this once for each native Present, including loading screens and FMVs.
        _fps_counter.Sample(ElapsedSeconds, ++_fps_rendered, System.Threading.Interlocked.Read(ref _fps_simulated));
        if (!_fps_overlay.get()) return;
        string rendered = _fps_counter.HasSample ? $"{_fps_counter.Rendered:F1}" : "--";
        string simulation = _fps_counter.HasSample && _config.VBlankRate ? $"{_fps_counter.Simulation:F1}" : "--";
        string text = $"{rendered} / {simulation} FPS";
        var viewport = ImGui.GetMainViewport();
        Vector2 position = viewport.Pos + new Vector2(Math.Max(12, viewport.Size.X - ImGui.CalcTextSize(text).X - 12), 12);
        var draw = ImGui.GetForegroundDrawList();
        draw.AddText(position + Vector2.One, 0xff000000, text);
        draw.AddText(position, 0xffffffff, text);
    }

    private void diagnostic_reset_input()
    {
        var io = ImGui.GetIO();
        bool gamepad = (io.BackendFlags & ImGuiBackendFlags.HasGamepad) != 0;
        bool l1 = ImGui.IsKeyDown(ImGuiKey.GamepadL1);
        bool r1 = ImGui.IsKeyDown(ImGuiKey.GamepadR1);
        bool start = ImGui.IsKeyDown(ImGuiKey.GamepadStart);
        bool r = ImGui.IsKeyDown(ImGuiKey.R);
        bool s = ImGui.IsKeyDown(ImGuiKey.S);
        bool t = ImGui.IsKeyDown(ImGuiKey.T);
        uint state = (gamepad ? 1u : 0u) | (l1 ? 2u : 0u) | (r1 ? 4u : 0u)
            | (start ? 8u : 0u) | (r ? 16u : 0u) | (s ? 32u : 0u) | (t ? 64u : 0u);
        if (state == _reset_input_state) return;
        _reset_input_state = state;
        diagnostic_event("reset_input", new { gamepad, l1, r1, start, r, s, t,
            navigation_enabled = (io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) != 0 });
    }
}
