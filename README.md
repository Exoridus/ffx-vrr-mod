<div align="center">

# FFX VRR

### High refresh rate and VRR for Final Fantasy X HD Remaster.

[![Latest](https://img.shields.io/github/v/release/Exoridus/ffx-vrr-mod?include_prereleases&style=for-the-badge&logo=github&label=Latest)](https://github.com/Exoridus/ffx-vrr-mod/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/Exoridus/ffx-vrr-mod/ci.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=fff&label=CI)](https://github.com/Exoridus/ffx-vrr-mod/actions/workflows/ci.yml)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the Windows Steam version.

**[Downloads](https://github.com/Exoridus/ffx-vrr-mod/releases)** · **[Install](#installation)** · **[Settings](#settings)** · **[Report a bug](https://github.com/Exoridus/ffx-vrr-mod/issues)**

</div>

> [!IMPORTANT]
> Experimental. Targets the official Fahrenheit alpha12 SDK and matching unmodified runtime.
> for Steam build **25501027**. FFX-2 is not supported.
> Independent presentation is currently vetoed: the required engine contracts and live capabilities are not yet qualified.
> A VRR request retains native timing and the original swapchain; new native gameplay/VRR acceptance has not been run.

## What it does

- Retains the existing fixed-rate corrections, with 60 Hz as the development target.
- Contains experimental independent rendering, interpolation and flip presentation behind the qualification veto.
- Reports requested and effective presentation separately; hook installation alone cannot enable the independent path.
- Optional AV1 replacement movies up to 4K, see [docs/video-replacement.md](docs/video-replacement.md).

## Installation

Requires the unmodified official **Fahrenheit alpha12** runtime with its prerequisites: **.NET Runtime 10** and the **Visual C++ Redistributable**,
both in the **x86** version (see the [Fahrenheit installation guide](https://github.com/fahrenheit-crew/fahrenheit/wiki/Installation-guide)).

| Download | Extract to | Use |
| --- | --- | --- |
| `fhvrr-mod-<tag>.zip` | `GAME_DIR/fahrenheit/mods/` | Add to an existing Fahrenheit installation. Add `fhvrr` to `fahrenheit/mods/loadorder`. |

Close the game before installing, updating or removing the mod. Keep the `fhvrr/` folder complete.
To update, replace only that folder. To uninstall, delete it and remove only its `fhvrr` loadorder
entry. Fahrenheit and other mods remain installed. Each download has a `.sha256` file to compare
with `Get-FileHash`.

Use windowed or borderless mode with MSAA off. Do not combine with another frame-rate mod.
Place `fhvrr` **last (bottom)** in `fahrenheit/mods/loadorder`, after Parry,
QoL and other mods. Keep this order when adding or updating mods. Compatibility
with another frame-rate mod or a mod replacing the same timing behavior is not
supported. High render FPS remain disabled until native validation is complete.

## Settings

Open Fahrenheit's settings with **F7**.

| Setting | Default | Effect |
| --- | --- | --- |
| Simulation rate | `60` | Game speed reference, applied on the next start. Keep 60. |
| Maximum render FPS | `0` | Applies only to qualified independent presentation; currently inactive. |
| FPS overlay | On | Shows `rendered / simulation FPS` in the top right corner. |

Diagnostics are written to `%LOCALAPPDATA%\Fahrenheit\VRR\diagnostics\` and to the Fahrenheit log.
Please attach both, plus the scene and your mod list, to bug reports.

## Known limitations

- Some battle effects, including the shatter transition at battle start, play too fast.
- The sphere grid cursor moves at the right speed but is not interpolated, so it steps visibly.
- Exclusive fullscreen, MSAA and a second device creation fall back to the native 60 FPS swapchain.

## Build from source

Requires Git and the .NET 10 SDK pinned in [global.json](global.json).

```powershell
.\build.cmd mod-build  --sdk official --configuration Release
.\build.cmd mod-verify --sdk official --configuration Release
.\build.cmd mod-pack   --sdk official --configuration Release --tag v0.1.0 --out-dir .release
```

The build checks the generated `src/IndependentPresentationAdmission.json` against the copied source inputs.
After changing runtime source, review its hook IDs, bindings and source pins in the sibling knowledge base's
`inputs/mappings/vrr_hook_installations.json`. Run `build.cmd build-vrr-hook-source-inventory` and then
`build.cmd build-vrr-core-contracts`; both reject unreviewed declaration drift. This input can explain a veto; it cannot authorize independent presentation.

Official SDK mode is the default and restores published packages from NuGet into an isolated cache.
The ZIP contains only the mod and its assets. Fahrenheit is installed separately; `--full true` is refused.
For development, `--sdk local` builds the unchanged official source commit in `fahrenheit.release.ref`.
The `release.yml` workflow publishes releases; a normal push never creates a release.

## Related projects

[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) · [FFX Parry](https://github.com/Exoridus/ffx-parry-mod) · [FFX Quality of Life](https://github.com/Exoridus/ffx-qol-mod)

Final Fantasy X belongs to Square Enix. This is an independent community project. No game files or
replacement movies are distributed.
