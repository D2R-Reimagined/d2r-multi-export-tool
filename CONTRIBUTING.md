# Contributing to `d2r-multi-export-tool`

Thanks for your interest in contributing. This document is the entry
point for **human** contributors. Read it first; it links out to the
deeper docs only when you actually need them.

> Looking for the rules that apply to AI coding assistants (Claude,
> Junie, Copilot, Codex, …)? Those live in
> [`AGENTS.md`](./AGENTS.md). The conventions there apply to humans
> too. Root `CLAUDE.md` imports `AGENTS.md` using Claude Code's supported
> `@AGENTS.md` syntax; do not maintain a second standards document.

---

## Project scope (read this first)

`d2r-multi-export-tool` is a **purpose-built** data exporter. It reads a
D2R Reimagined mod source tree (`data\global\excel\*.txt` plus
`data\local\lng\strings\*.json`) and produces the key-based, language-
agnostic JSON bundle consumed by the companion
[D2R Reimagined website](https://github.com/D2R-Reimagined).

Concretely, the tool exists to produce:

- `<out>/keyed/*.json` — one file per content family
  (uniques, sets, runewords, magic affixes, cube recipes, base items,
  etc.), with every player-facing string represented as a translation
  **key** or a `KeyedLine { key, args }` object.
- `<out>/strings/<lang>.json` — flat `{ key: "localized text" }` map
  per supported D2R locale.
- `<out>/extras/` — audit folder (import report, missing-translations
  audit, etc.).
- `<out>/sprites/` — WebP item, UI and skill-icon sprites.
- `<out>/manifest.json` — written last: SHA-256 of every file this run
  produced, per-step status, `Succeeded: false` if any error occurred and
  `Complete: false` if a step was skipped or left a declared output unwritten.
  Except for replacing the previous manifest at the output phase, the tool never
  deletes files it did not attempt to write in this run, so the manifest —
  not the folder — says which files are current.

The schema, CLI flags, and config shape exist to serve **that one
downstream consumer** and may change whenever the mod or the site need
them to. Contributions that broaden the tool's scope into a "general
D2R data dumper" are out of scope and will likely be declined.

---

## Repository layout (high level)

| Path | Purpose |
|---|---|
| `D2RMultiExport.Lib/` | Core import → translation → export pipeline. |
| `D2RMultiExport.Lib/Config/` | Bundled JSON config + matching `*Config.cs` POCOs. |
| `D2RMultiExport.Lib/Import/` | One importer per `.txt` family (uniques, sets, runewords, cube recipes, …). |
| `D2RMultiExport.Lib/Translation/` | Property → translation-key resolution, synthetic-string registry. |
| `D2RMultiExport.Lib/Exporters/` | Final writers (`keyed/`, `strings/`, `sprites/`, `manifest.json`); all output goes through `ExportOutputs`. |
| `D2RMultiExport.Console/` | Headless CLI (`export` and `publish` commands). |
| `D2RMultiExport.Client/` | Avalonia desktop GUI front-end. |
| `D2RMultiExport.Tests/` | xUnit regression tests (output integrity, input handling). |
| `docs/` | Operational docs (`TESTING.md`, `RELEASING.md`, `WEBSITE-INTEGRATION.md`, and one per calculator export). |

Excel `.txt` parsing is delegated to the
[`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
NuGet package. Parser bugs are fixed **upstream** in the sibling
[`d2r-dotnet-tools`](https://github.com/D2R-Reimagined/d2r-dotnet-tools)
repo, not worked around inline here.

To debug an unreleased upstream fix locally, swap the
`<PackageReference Include="D2RReimaginedTools.FileExtensions" …/>`
in `D2RMultiExport.Lib/D2RMultiExport.Lib.csproj` for a local
`<ProjectReference>` pointing at your sibling `..\d2r-dotnet-tools`
checkout, validate via the smoke export, ship the upstream PR, then
bump the `<PackageReference>` `Version` once the upstream release is
out. **Revert the swap before committing** — the committed `.csproj`
must always restore from NuGet. This same recipe is summarized for agents
in [`AGENTS.md`](./AGENTS.md).

---

## Prerequisites

- **.NET SDK 10.0** (`global.json` pins `10.0.203` with
  `rollForward: latestFeature`).
- A D2R mod source tree with `data\global\excel\*.txt` and
  `data\local\lng\strings\*.json`.
- *(Recommended)* A CascView extraction of the base game's `data` tree,
  passed as `--casc-data`: it supplies vanilla sprites, base strings and
  tables the mod does not ship. See `README.md`.

Windows is the primary supported platform (the GUI is Avalonia and the
release workflow publishes `win-x64`). The library and console target portable
.NET 10; Linux execution was not verified locally because WSL virtualization
is disabled. Do not infer verified portability from the target framework alone.

---

## Build and run

Restore + build the whole solution:

```powershell
dotnet restore .\D2RMultiExport.sln
dotnet build   .\D2RMultiExport.sln -c Release --no-restore
```

Run the headless export (CLI):

```powershell
dotnet run --project .\D2RMultiExport.Console -c Release -- export `
    --mod-data  "<path-to-mod>" `
    --casc-data "<path-to-casc-extraction>" `
    --out       .\test-output
```

Stage a publishable bundle (the directory must be new/empty and not nested with output):

```powershell
dotnet run --project .\D2RMultiExport.Console -c Release -- publish `
    --from .\test-output --staging <new-or-empty-directory>
```

Staging verifies manifest schema 2 and copied payload hashes/lengths, refuses
failed runs, and refuses incomplete runs unless `--allow-incomplete` is explicit.
Deploy staging as a coherent bundle; hashes do not attest source freshness or
require browser-side verification. Current CLI options are in [README.md](README.md#run--cli).

Run the desktop GUI:

```powershell
dotnet run --project .\D2RMultiExport.Client -c Release
```

---

## Tests and smoke export

[docs/TESTING.md](./docs/TESTING.md) is authoritative for commands and test
selection: Level A for routine localized changes, Level B for targeted importer/
exporter regressions, Level C for shared arithmetic/schema, broad changes,
insufficient targeted coverage and releases. Documentation-only changes need diff
and link review, without builds or exports.

Build once in the selected configuration, then use `--no-build`. The Level C
script retains one content export for compatible final-JSON checks and the
independent Python auditor, preserving intermediate importer assertions and
independent desktop root transitions. Releases and CLI/publishing changes require
its CLI repeat/staging option. Review current-run reports and affected JSON.

Ordinary tests explicitly skip unavailable optional real-data cases. Full
validation fails if required roots/checks are unavailable or skipped. Record
passed, failed, skipped and unexecuted checks in the PR; a deterministic test pass
alone does not establish required real-data validation.

---

## Coding conventions (summary)

The full ruleset is in [`AGENTS.md`](./AGENTS.md). The minimum a human
contributor needs in their head:

- **Target framework:** `net10.0`, **C# 14**, nullable reference types
  enabled. 4-space indent, file-scoped namespaces, Allman braces.
- **Naming:** `PascalCase` types/methods/properties, `camelCase`
  locals, `_camelCase` private fields, `*Set` suffix on
  config-derived hashsets.
- **Dictionaries / sets keyed by D2R codes** (`"cjw"`, `"rin"`, …)
  must use `StringComparer.OrdinalIgnoreCase`.
- **Async all the way down** — no `.Result` / `.Wait()`. Static
  loaders follow the `LoadAsync(path)` pattern.
- **Importers don't throw** for per-row failures; they record into
  `ImportResult<T>` via `AddError` / `AddWarning`. Only fatal load
  failures propagate.

### Two architectural rules contributors trip on most often

1. **No hardcoding.** New lookup data, codes, item/affix lists, or
   magic numbers belong in `D2RMultiExport.Lib/Config/*.json` (with a
   matching strongly-typed `*Config.cs` property), **not** inline in
   importer or exporter code. The full "no-hardcoding" rule and its
   canonical pattern are in `AGENTS.md`.
2. **Exports are keyed.** Every player-facing string written under
   `<out>/keyed/*.json` must be a translation key (or a `KeyedLine
   { key, args }`) — never a baked English sentence. Three documented
   raw-English cases (cube recipe `Description`, the `PType`
   Prefix/Suffix discriminator, `RequiredClass`) are listed in
   `AGENTS.md`; new exceptions require an explicit decision.

If your change introduces a brand-new player-facing string with no
matching CASC translation key, register it in
`D2RMultiExport.Lib/Config/synthetic-strings.json` so the language
bundles include it.

---

## Pull-request workflow

1. **Fork and branch** from `main`. Keep branches focused; one logical
   change per PR.
2. **Conventional Commits.** At least one commit on the PR must
   follow:
   ```
   type(scope?)!?: short, lowercase description
   ```
   Allowed types: `feat | fix | docs | style | refactor | perf | test
   | build | ci | chore | revert`. Use `!` (e.g. `feat!:`) or a
   `BREAKING CHANGE:` body paragraph for breaking changes. The
   **Validate Commits** workflow (`pr-semantic-commits.yml`, status
   context `validate-commits`) enforces this on every PR; squash-
   merging with the PR title as the commit subject is the intended
   workflow.
3. **Validate locally before pushing** at the appropriate level in
   [docs/TESTING.md](./docs/TESTING.md); reuse an already passing matching
   build/test session. The **Build Check** workflow (`ci.yml`) is
   **manual-only** (`workflow_dispatch`); trigger it from
   **Actions → Build Check → Run workflow** before merge for a clean-runner
   confirmation. It builds Release and runs deterministic C#/Python suites,
   without private real-data inputs.
4. **Run Level C when required** by the change's scope; do not add another
   content smoke export to the same successful validation session.
5. **Don't leave a local `<ProjectReference>` swap** for
   `D2RReimaginedTools.FileExtensions` in `D2RMultiExport.Lib.csproj`
   — the committed `.csproj` must always restore from NuGet.
6. **Fill out the PR template.** It's not enforced, but it shortens
   review.

---

## Where to file things

- **Bug reports** → GitHub Issues, `Bug report` template.
- **Feature requests** → GitHub Issues, `Feature request` template.
  Note the project scope above; requests outside it will be declined
  with a pointer back here.
- **Questions / discussion** → GitHub Discussions.
- **Security vulnerabilities** → **Do not open a public issue.**
  Use [GitHub private vulnerability reporting](https://github.com/D2R-Reimagined/d2r-multi-export-tool/security/advisories/new).
  See [`SECURITY.md`](./SECURITY.md).
- **Code-of-conduct concerns** → see [`CODE_OF_CONDUCT.md`](./CODE_OF_CONDUCT.md)
  for the reporting channel.

---

## Releases

Tagged releases are produced manually from
**Actions → Release → Run workflow**. Full procedure in
[`docs/RELEASING.md`](./docs/RELEASING.md). The release notes are
auto-generated from Conventional-Commits subjects on the merged PRs;
**there is no `CHANGELOG.md`** — the
[GitHub Releases page](https://github.com/D2R-Reimagined/d2r-multi-export-tool/releases)
is the canonical changelog.

The release workflow:

1. Validates the SemVer `version` input.
2. Publishes `D2RMultiExport.Console` and `D2RMultiExport.Client`
   side-by-side as a self-contained, single-file `win-x64` build
   (`-p:PublishSingleFile=true`,
   `-p:IncludeNativeLibrariesForSelfExtract=true`,
   `-p:Version=<input>`).
3. Verifies the bundled `Config\` tree shipped (the
   `D2RMultiExport.Lib.csproj` `<None Update>` rules must keep
   propagating `export-config.json`, `synthetic-strings.json`,
   `stat-overrides.json`, `class-ranges.json`,
   `skill-desc-functions.json`, and the `Config\CASC_DATA\` tables
   `runes.txt`, `skillcalc.txt`, `misscalc.txt`).
4. Tags `v<version>`, zips into
   `D2RMultiExport-v<version>-win-x64.zip` (top-level folder
   `D2RMultiExport\`), and attaches it to a GitHub Release with notes
   generated by `.github/scripts/Generate-ReleaseNotes.ps1`.

Shared assembly metadata (`Company`, `Copyright`,
`PackageLicenseExpression=GPL-3.0-or-later`, `RepositoryUrl`, etc.) is
declared in the root `Directory.Build.props` and applies to every
project.

### Application icon

The branding artwork lives under `branding/` (the master PNG +
`build-icon.ps1`, which produces a multi-size PNG-in-ICO). The shipping
icon `D2RMultiExport.Client/Assets/icon.ico` is a copy of
`branding/icon.ico`; if the source PNG changes, re-run
`branding/build-icon.ps1` and copy the regenerated `icon.ico` over the
Client `Assets\` copy. Both `.csproj` files reference an
`<ApplicationIcon>` so the generated `.exe` files carry the icon as a
Win32 resource.

---

## License

By contributing, you agree that your contributions will be licensed
under the [GNU GPL-3.0-or-later](./LICENSE), the same license that
covers the rest of the project. Source files carry an
`// SPDX-License-Identifier: GPL-3.0-or-later` header; please keep
that header on any new `.cs` file you add.
