<div align="center">

# FFX VRR

### An experimental workspace for variable-rate timing and interpolation.

[![Latest](https://img.shields.io/github/v/release/Exoridus/ffx-vrr-mod?include_prereleases&style=for-the-badge&logo=github&label=Latest)](https://github.com/Exoridus/ffx-vrr-mod/releases)
[![CI](https://img.shields.io/github/actions/workflow/status/Exoridus/ffx-vrr-mod/ci.yml?branch=main&style=for-the-badge&logo=githubactions&logoColor=fff&label=CI)](https://github.com/Exoridus/ffx-vrr-mod/actions/workflows/ci.yml)
[![Downloads](https://img.shields.io/github/downloads/Exoridus/ffx-vrr-mod/total?style=for-the-badge&logo=github)](https://github.com/Exoridus/ffx-vrr-mod/releases)

A Fahrenheit mod for **Final Fantasy X HD Remaster on Windows**.

**[Downloads](https://github.com/Exoridus/ffx-vrr-mod/releases)** · **[Development builds](https://github.com/Exoridus/ffx-vrr-mod/actions/workflows/ci.yml)** · **[Install](#installation)** · **[Settings](#settings)** · **[Report a bug](https://github.com/Exoridus/ffx-vrr-mod/issues)**

</div>

> [!IMPORTANT]
> **Experimental alpha12 development.** This branch uses matching local Fahrenheit and SDK packages from the commit in [fahrenheit.release.ref](fahrenheit.release.ref). These packages are development builds, not official-SDK releases. Build and packaging checks do not prove in-game compatibility with Steam build **25501027**. FFX-2 is unsupported.

## What it provides

| Feature | Behavior |
| --- | --- |
| Timing research | Configurable presentation and timing experiments. |
| Interpolation | Opt-in native camera, root and full skeleton-palette interpolation with automated history/restore tests. |
| Diagnostics | Controls for investigating individual timing systems. |

## Installation

Requires a compatible Fahrenheit installation. Follow the [upstream prerequisites](https://github.com/fahrenheit-crew/fahrenheit/wiki/Installation-guide): **.NET Runtime 10** and the latest **Visual C++ Redistributable**, each in x86 and x64 versions. The mod runs as x86; a 64-bit runtime alone is insufficient. See the [Fahrenheit compatibility guide](https://github.com/fahrenheit-crew/fahrenheit/wiki/Compatibility-guide).

| Download | Destination | Use |
| --- | --- | --- |
| `fhvrr-mod-<tag>.zip` | `GAME_DIR/fahrenheit/mods/` | Add the mod to an existing, matching Fahrenheit installation. |
| `fhvrr-full-<tag>.zip` | `GAME_DIR/`, beside `FFX.exe` | Install the mod together with its matching Fahrenheit host. |

### Mod only

1. Download the mod ZIP and its `.sha256` file. For development packages, use the CI artifact and the exact Fahrenheit source pin recorded in its provenance.
2. Close the game. Extract the ZIP into `fahrenheit/mods/`; the resulting DLL must be `fahrenheit/mods/fhvrr/fhvrr.dll`.
3. Add `fhvrr` on its own line to the extensionless `fahrenheit/mods/loadorder` file, if it is absent. Keep existing entries.
4. Launch through Fahrenheit and open **F7** for settings.

Keep the entire `fhvrr/` folder together, including its generated manifest, dependency metadata, PDB, language files and resources. GitHub's automatic source-code ZIP is not an installable mod.

### Full package

Extract the full ZIP next to `FFX.exe` and run `start-ffx-with-mods.cmd`. The package includes `fahrenheit/bin/` and a loadorder containing this mod. Back up an existing Fahrenheit installation first; use the mod-only package when keeping other mods and configuration. The runtime is a prerequisite and is not installed automatically.

Compare `Get-FileHash .\downloaded.zip -Algorithm SHA256` with the accompanying `.sha256` file. CI artifacts are download containers: unpack that download first, then install the mod or full ZIP inside it.

To update, close the game and replace the mod's entire folder with the new one. Preserve Fahrenheit's configuration and loadorder. To uninstall, remove the `fhvrr` loadorder entry and its folder.

## Settings

Use Fahrenheit's native **F7** settings menu. English and German labels are included. Settings are saved by Fahrenheit; do not edit the shipped localization or manifest files to configure the mod. When updating from the old alpha11 settings layout, keep a backup of your configuration and check your selections after the update.

The F7 panel has three controls:

| Control | Default | Behavior |
| --- | --- | --- |
| Simulation / engine cutscenes | `60` | Select 30, 60 or 120 Hz for the next startup. The active rate stays fixed through stalls and loading. The legacy JSON `TargetFramerateOverride` takes precedence. A 120 Hz simulation requires the independent presentation path. |
| Maximum render FPS | `0` | Zero means unlimited; nonzero values have a minimum of 30, including 144. Applies live when the independent presentation path is active. Movies retain a cap of at most 60. |
| FPS overlay | On | One noninteractive line at the top right, refreshed twice a second: `rendered / simulation FPS`. |

The simple FPS overlay is independent of `TimerOverlay` and diagnostics. Its render count comes from Fahrenheit's per-Present overlay callback, so it measures render submissions rather than monitor scanout or confirmed successful Presents. Simulation counts advancing `AdvanceVBlankCounters` calls; extra presentation-only passes do not count. Loading screens and FMVs can render while simulation stops. It reports `--` if the simulation-clock hook is disabled. These are invocation rates, not a claim that every animation subsystem advances at that rate.

Other controls live in `fhvrr.config.json`; omitted values use [VrrConfig](src/VrrConfig.cs). Most baseline corrections default on, including vblank/character timing, ATEL waits and movement, camera/fades, motion sequences, particle timeline/kernels, event clocks, menu water and optional video replacement. Their hooks still respect supported states and native validation. `VrrEnabled`, generic particle-integrator scaling, ATEL worker gravity and experimental asset overrides default off. New-format weapon/character texture animation uses a guarded per-call adapter: discrete sprite/blink steps follow the simulation clock, private UV scroll rates scale continuously, and all native material outputs remain. The engine pause byte is never overwritten.

Source defaults differ from a tested installation's overrides: diagnostics and the native phase probe default on in source, and the broad `EffectHold` and old `Buoyancy` switches also default on. Existing installation overrides must be retained when updating; enabling those switches is not required to use the FPS overlay.
## Known limitations

`VrrEnabled` is experimental and defaults off. Its scheduler supports selected fixed 30/60/120 Hz simulation independently of a render cap, with native camera/root/skeleton interpolation and coordinated DXGI flip/tearing. The general effect path remains incomplete: holding the entire mode-0 effect update also removes geometry preparation, while running it on every image advances effect scripts too quickly. The scheduler now requires complete producer qualification before consuming an extra-image plan. Unqualified scenes retain native clock ownership and a render cap of at most 60. The bounded producers cover empty scenes, a strict Game Over 670 pilot and native field programs composed entirely of the validated Shape, ShapeX, ShapeField and matrix kernels plus approved simulation-only operations. Field presentation rebuilds geometry without running simulation operations, preserves manager restart/derived work and requires live backend/child/history state. Any unsupported active program rejects the extra image. This whitelist covers only a subset of the intro field assets; general field/battle coverage and native appearance remain unaccepted. Keep the accepted 60 Hz installation overrides while native acceptance is pending.

Use windowed or borderless mode with MSAA disabled for presenter experiments. FMVs and recorded syncdata retain their native clock owner. Repeated device creation remains unsupported. Native gameplay across scenes and monitor VRR still need acceptance. Do not combine this mod with another FPS/VRR timing patch. Frame Timer is optional diagnostics; the FPS display and rate controls do not require its window.

Fahrenheit alpha11 targets the older Steam executable; alpha12 targets the October 2026 executable line. Matching the framework alone does not verify this mod's custom hooks against a new game build. Reports should include the Steam build ID, mod tag, Fahrenheit version/source pin, scene and enabled settings.

## Build from source

Requires **Git and the .NET 10 SDK** selected by [global.json](global.json). The commands below build and validate only the mod, without a game installation or automatic deployment.

```powershell
git clone https://github.com/Exoridus/ffx-vrr-mod.git
cd ffx-vrr-mod
.\build.cmd mod-build --sdk local --configuration Debug
.\build.cmd mod-verify --sdk local --configuration Release
.\build.cmd mod-pack --sdk local --configuration Release --tag v0.0.0-dev --out-dir .release
```

`local` checks out the committed Fahrenheit pin and builds missing core/SDK packages. The current unpublished startup repair is preserved as a hash-checked incremental Git bundle under `build/`; its prerequisite is the recorded upstream commit. Local package output includes the bundle and provenance beside the ZIP. GitHub bundle uploads remain disabled. The mod builds in an isolated workspace with a package cache separated by package content. SDK-generated metadata and every runtime asset are checked before packaging. The ZIP contains exactly one top-level folder, `fhvrr/`.

To also produce the full package, append `--full true` to `mod-pack`. This requires Visual Studio C++ Build Tools, an x86 VC++ toolset, Windows SDK Debugging Tools and integrated vcpkg. The matching Fahrenheit host is built from fresh deployment output. The shipped CMD launcher is Windows glue; maintained build and packaging logic is C#.

For public distribution, use `--sdk official --tag v<project-version>`. That mode restores exclusively from nuget.org into a separate cache and uses the matching official Fahrenheit release for the full package. It fails explicitly while the required official SDK or host is unavailable.

## CI and releases

CI verifies Debug and produces tested Release mod/full development packages. Both archives have SHA256 checksums and provenance recording the SDK origin, source commit and payload hashes. Release uses the same build and package checks.

Maintain the version in the mod `.csproj`, merge to `main`, then run a development rehearsal:

```powershell
gh workflow run release.yml -R Exoridus/ffx-vrr-mod -f version=v<project-version> -f sdk=local -f dry_run=true
```

Public releases require `sdk=official` and `dry_run=false`. Only after verification succeeds does the workflow create an experimental prerelease and its tag. Local-SDK packages cannot pass that publication gate. No release is created by a normal push.

## Play-session diagnostics

Optional AV1 movies go in `<mod>/video/`, for example `video/opl.webm`.
The native movie request chooses `opl_us.webm` or `opl_jp.webm` first, then
shared `opl.webm`. The same names with `.mkv` are supported; localized names
win over shared ones, then WebM wins over MKV for the same stem.
Missing or rejected files retain the original movie.
Supported files are WebM or MKV with AV1 up to 3840x2160 at 29.97/59.94 FPS and
original Vorbis audio. Shared files use the ISO language tags `eng` and `jpn`.
A JP request prefers `jpn`, then falls back to `eng`; other requests use `eng`.
Titles such as `opl_us`/`opl_jp` are informational. See `video/README.txt`.
Replacement videos are not bundled.
D3D11VA hardware decoding uses dav1d as software fallback; 4K software
playback can exceed the CPU budget. This is an 8-bit SDR output path.

An optional `FmvLabManifest` takes precedence over the video folder and starts
with Original (retail). Its Next FMV selector changes the next initialization
of the matching movie. Lab candidates require a checked sidecar and hash;
`FmvLabExperimentalFormats` admits AV1 and up to 120000/1001 FPS for experiments.
120 FPS playback remains unaccepted. The selector does not start a movie.

Optional `DiagnosticsBinary` records exact phase/camera words and bounded raw
actor snapshots before/after existing character-update hooks. The shared KB's
`ffx-kb play --profile vrr` enables it for one process and correlates output with
Stage0 logs; the updated mod must already be installed and enabled. No loadorder
or game files are changed by that launcher. `--actors 0..64` controls the raw
slot cap (default 8); EXE-qualified `--watches` adds bounded read-only pointer
walks. Compression and disk writes run on a bounded background queue, with
explicit loss/limit reporting. This instrumentation records the opt-in native interpolation path; it is not native acceptance
of the 120-FPS/VRR presenter. Use `ffx-kb trace --input capture.ffxt`
to inspect completed blocks or export raw uint32 words with `--output new.jsonl`.
The maintained binary layout and sampling contracts are in the knowledge base's
`docs/diagnostic-traces.md`.

`Diagnostics` and `NativePhaseProbe` default to `true`. Each run writes one
bounded JSONL file under `%LOCALAPPDATA%/Fahrenheit/VRR/diagnostics/`; Fahrenheit
also logs its exact path. `DiagnosticsMaxMiB` defaults to 512 (8..2048 allowed).
The Frame Timer panel exposes a Marker button for matching visible defects to
the capture. Set `Diagnostics: false` to disable capture and its overlay; the
existing `TimerOverlay` switch can still enable the timer separately.

The header pins the validated executable, mod DLL hash, managed modules and full
configuration. Records correlate monotonic timestamps, threads, outer frames,
inner main-loop invocations, exec state, camera-source bindings and matrix bits.
Frame gaps, syncdata/backlog, correction rate, FMV state/override selection and
bounded field/battle particle population snapshots are included. Matrix values
are raw uint32 IEEE-754 bits so non-finite values remain representable in JSON.
Optional probes refuse mismatched entry bytes and log incomplete coverage.

No recorder I/O runs in the native hooks. The bounded queue drops records rather
than waiting; `session_end` reports written/dropped records and file saturation.
A limit warning also appears in-game. Missing `session_end` means shutdown did
not finish; native crashes are not a guaranteed managed exception notification.
Unknown populations/bindings are null or explicitly unavailable, not zero.

For a useful first run, retain the JSONL and Fahrenheit log, mark visible defects,
and note the scene/save and other enabled mods. Include a camera cut, battle,
pause/focus loss and an FMV start/skip/return. The framework overlay is a callback
observation, not a native Present hook. Native Draw/Present, actor/skeleton history
and a complete input-latency trace are not captured. This is instrumentation;
native 60-FPS interpolation and VRR remain unvalidated.
## Animated texture presentation

`TextureFrameInterpolation` defaults off while native acceptance is pending. The current adapter supports the
observed `w001` eight-frame BC3_UNORM sequence: it blends adjacent authored
images into an owned RGBA8_UNORM texture, including all ten mip levels, and
substitutes PS slot 1 only around the matching draw. Native bindings and compute
state are restored afterward. Native pause, authored duration/random holds,
frame wrap, slot lifetime and ambiguous-owner rejection retain game ownership.
It works independently of `VrrEnabled`; unsupported textures retain native output.

Continuous character UV presentation uses a separate final-image publication
boundary after joined character jobs. Canonical animation work stays unchanged.
Clock reset epochs invalidate histories across gaps and native clock handoffs.
GPU WARP readback and managed tests cover these contracts; retail appearance,
performance and general VRR effect coverage still require acceptance.

`TextureBlendProbe` defaults off. When enabled with normal diagnostics off, it
uses an isolated bounded texture-only session instead of enabling frame capture.
Its w001 resource/binding census stops at 32 resources, 64 bindings or 4096 draws.

## Related projects

[Fahrenheit](https://github.com/fahrenheit-crew/fahrenheit) · [FFX Parry](https://github.com/Exoridus/ffx-parry-mod) · [FFX Quality of Life](https://github.com/Exoridus/ffx-qol-mod)

Final Fantasy X belongs to Square Enix. This is an independent community project. No game executable, extracted game assets or replacement videos are distributed.
