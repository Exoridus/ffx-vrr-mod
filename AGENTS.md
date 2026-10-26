# AI Repository Rules — ffx-vrr-mod

## Purpose

Runtime consumer, not a research repo. This is an experimental Fahrenheit module for
FFX timing and interpolation. Several live adapters remain placeholders. Reverse-engineering evidence, engine addresses
and catalogs live in the sibling `ffx-knowledge-base`; findings discovered here
are promoted there, not documented only here.

## ffx-kb first

Maintained in ffx-knowledge-base/docs/ffx-kb-first.md and propagated by
`build.cmd sync-kb-rule`. Do not edit the block below; edit it there.

<!-- ffx-kb-first:portable:begin -->
Dialogue, data structures, asset mappings, decompiled code, the state of the
modding community including its repos and tools, earlier forensic attempts
including the failed ones, and general knowledge about the game and its
internals - all of it goes to the `ffx-kb` MCP tools first. Not web search,
not model knowledge, not fresh reverse engineering. FFX-2 included.

Hold an identifier (engine address, ATEL function id, model raw id, a struct
offset like `Chr+0x433`, an event id): use `lookup`. Hold a concept: use
`search` with the words a write-up would use, never a CamelCase symbol - a
symbol query returns zero hits from a store that holds the answer. Name only,
and both came back empty: `grep canonical/` in ffx-knowledge-base.

Research or reverse engineering starts only when the answer is genuinely
missing or marked uncertain, judged on the fields the tools return:

- Nothing in the `findings`, `canonical` and `engine` blocks, and `lookup`
  resolved on no spine.
- `status` is `hypothesis`, `disputed` or `obsolete`; or `confidence` is
  `low`; or `findings(neverVerified: true)` returns the claim. `verified` and
  `strong_evidence` are answers; `probable` is usable but re-check it.
- The claim rests on evidence whose `resolution_status` is neither `resolved`
  nor `symbol`.
- Every hit carries `platform` `switch` or `ffx2`. Those corroborate a PC
  answer and are never one.

**`falsified` is an answer, not a gap.** It means the approach was tested and
disproved, and the record exists precisely so nobody retries it. Never treat it
as a missing answer and never re-derive it: read the claim, pick a different
approach, and if the new approach also fails, write that down too.

**`catalog_sources_not_walked` means "never full-text indexed", not
"unknown".** The same holds for `unreachable_sources`. Those sources are
recorded by path, size and mtime on purpose - the NAS shares, the Ghidra
exports - so the index cannot answer from their content and was never meant to.
Open the file or the NAS path. Reverse engineering something that sits in an
unwalked source is the most expensive way to be told what it already says.

Whatever the research produces goes back in immediately, in the same session,
before the next task - not at session end. In the ffx-knowledge-base checkout:
write `canonical/ffx/findings/curated/<slug>.json`, then `build.cmd
build-findings`, then `build.cmd kb-index --incremental`. Both generators are
required: the findings layer is read from `index.json`, not from `curated/`,
so skipping `build-findings` leaves the finding invisible with no error. A
negative result earns the same write-back as a positive one.

When mod or tool work changes a recorded behavior, setting, integration or
limitation, update that project's facts and exact evidence hashes in
`inputs/mappings/project_knowledge/<project-id>.json` in the same task. Run
`build.cmd build-project-knowledge --name <project-id>`, then the incremental
index pass. `kb-index` also checks all profiles automatically. Changed or
missing evidence suppresses the affected claim until reviewed; unavailable
sources retain historical observations with `source_state: unavailable`.
`source_observed` describes code, not a successful in-game test. Keep source
code in the project and write reusable engine results as findings.
<!-- ffx-kb-first:portable:end -->

## Hard rules

- FFX only (`FhGameId.FFX`). FFX-2 is out of scope.
- No 30 FPS fallback, ever. Alternate-frame updates and frameskipping are
  workarounds, not acceptable end states. A subsystem that is not correct at
  60 Hz is logged or disabled through config, never hidden.
- Original game files stay byte-identical. Code patches are in-process only and
  go through `PatchJournal` so shutdown restores the image.
- Fahrenheit is the only hook and file-loader owner.
- No video or game asset is ever committed.
- Every retiming correction is individually toggleable in `VrrConfig`.

## Conventions

- C# / .NET 10, `win-x86`, file-scoped namespaces, nullable enabled, `sealed`.
- Hook targets are addressed by RVA (`FhCallLocation`), because the pinned
  Fahrenheit's STEP generation exposes most functions only as `FUN_<addr>`
  entries. RVA = Ghidra VA - 0x400000.
- `FhCallLocation` is a ref struct; construct it at the use site.
- One partial per subsystem (`VrrModule.Present.cs`, `.Atel.cs`, ...).

## Build and distribution

Use `build.cmd mod-build`, `mod-verify` and `mod-pack`. Build logic is C# under build/.
The SDK generates the manifest. Only official SDK builds may be publicly released.
Mod ZIPs extract into fahrenheit/mods/. Fahrenheit is installed separately and remains unchanged.
Do not distribute a replacement host, launcher or shared loadorder with this mod.
Keep runtime assets, PDBs, dependency metadata and localization together. Do not deploy during verification.
