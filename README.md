<div align="center">

# FFX VRR

### High refresh rate and VRR for Final Fantasy X HD Remaster.

[![Latest](https://img.shields.io/github/v/release/Exoridus/ffx-vrr-mod?include_prereleases&style=for-the-badge&logo=github&label=Latest)](https://github.com/Exoridus/ffx-vrr-mod/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/Exoridus/ffx-vrr-mod/ci.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=fff&label=CI)](https://github.com/Exoridus/ffx-vrr-mod/actions/workflows/ci.yml)

A [Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) mod for the Windows Steam version.

**[Downloads](https://github.com/Exoridus/ffx-vrr-mod/releases)** · **[Install](#installation)** · **[Settings](#settings)** · **[Report a bug](https://github.com/Exoridus/ffx-vrr-mod/issues)**

</div>

> [!IMPORTANT]
> Experimental. Built against Fahrenheit alpha12 from the commit in [fahrenheit.release.ref](fahrenheit.release.ref)
> for Steam build **25501027**. FFX-2 is not supported.
> Independent presentation is currently vetoed: the required engine contracts and live capabilities are not yet qualified.
> A VRR request retains native timing and the original swapchain; new native gameplay/VRR acceptance has not been run.

## What it does

- Retains the existing fixed-rate corrections, with 60 Hz as the development target.
- Contains experimental independent rendering, interpolation and flip presentation behind the qualification veto.
- Reports requested and effective presentation separately; hook installation alone cannot enable the independent path.
- Optional AV1 replacement movies up to 4K, see [docs/video-replacement.md](docs/video-replacement.md).

## Installation

Requires Fahrenheit with its prerequisites: **.NET Runtime 10** and the **Visual C++ Redistributable**,
both in the **x86** version (see the [Fahrenheit installation guide](https://github.com/fahrenheit-crew/fahrenheit/wiki/Installation-guide)).

| Download | Extract to | Use |
| --- | --- | --- |
| `fhvrr-mod-<tag>.zip` | `GAME_DIR/fahrenheit/mods/` | Add to an existing Fahrenheit installation. Add `fhvrr` to `fahrenheit/mods/loadorder`. |
| `fhvrr-full-<tag>.zip` | `GAME_DIR/`, next to `FFX.exe` | Mod plus matching Fahrenheit. Start with `start-ffx-with-mods.cmd`. |

Keep the `fhvrr/` folder complete. To update, replace the folder; to uninstall, delete it and its
loadorder entry. Each download has a `.sha256` file to compare with `Get-FileHash`.

Use windowed or borderless mode with MSAA off. Do not combine with another frame-rate mod.

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
.\build.cmd mod-build  --sdk local --configuration Debug
.\build.cmd mod-verify --sdk local --configuration Release
.\build.cmd mod-pack   --sdk local --configuration Release --tag v0.0.0-dev --out-dir .release
```

The build checks the generated `src/IndependentPresentationAdmission.json` against the copied source inputs.
After changing reviewed inputs, regenerate it with `build.cmd build-vrr-core-contracts` in the sibling
knowledge-base repository. This input can explain a veto; it cannot authorize independent presentation.

`--sdk local` builds Fahrenheit from the pinned commit; a not yet upstreamed startup fix is applied
from the hash-checked bundle in `build/`. Add `--full true` to `mod-pack` for the full package
(needs the Visual Studio C++ build tools). Public releases use `--sdk official` and the `release.yml`
workflow; a normal push never creates a release.

## Related projects

[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) · [FFX Parry](https://github.com/Exoridus/ffx-parry-mod) · [FFX Quality of Life](https://github.com/Exoridus/ffx-qol-mod)

Final Fantasy X belongs to Square Enix. This is an independent community project. No game files or
replacement movies are distributed.
