# d2r-multi-export-tool

A .NET 10 tool that imports D2R Reimagined mod game data (the `excel`
`.txt` files plus the `local\lng\strings` translation JSONs) and emits
the key-based, language-agnostic JSON bundle consumed by the
companion D2R Reimagined website.

## What it does

- Reads the mod's `data\global\excel\*.txt` rows (uniques, sets,
  runewords, magic prefixes/suffixes, cube recipes, base armor/weapon
  tables, etc.) and the matching `data\local\lng\strings\*.json`
  translation tables.
- Resolves each property (stat, skill grant, class restriction,
  damage/defense roll, requirement, etc.) to a translation **key** plus
  finalized numeric arguments.
- Exports [skill trees and calculated descriptions](docs/SKILL-CALCULATIONS.md),
  [drop](docs/DROP-CALCULATOR.md), [IAS](docs/IAS-CALCULATOR.md) and
  [monster-health](docs/HEALTH-CALCULATOR.md) calculator data, and
  [loot-filter data](docs/LOOT-FILTER.md).
- Supplies item presentation and stat-display metadata, rare-name lists,
  and converted WebP item art, skill icons and inventory UI art.
- Writes `keyed/*.json` content and calculator documents,
  `strings/<lang>.json` (one per supported D2R locale), converted assets
  under `sprites/`, and an `extras/` audit folder.

## Why it works this way

The downstream website renders item pages in 13 languages and switches
locale with a single extra fetch. Display text uses translation keys,
with the documented exceptions below:

- Localized display text in `keyed/*.json` uses translation key strings
  or `KeyedLine` objects `{ key, args }` whose `args` are primitives,
  nested `KeyedLine`s, or `KeyedLineArg` records.
- All numeric arithmetic — ED%, ethereal ×1.5, durability adjustments,
  smite/kick damage, elemental durations, requirement reductions, charm
  weight, per-character-level scaling, etc. — is finalized here, in C#.
  The client substitutes positional `%d` / `%s` against `strings/<lang>.json`;
  skill consumers also select precomputed axes and apply explicitly exported
  live synergy percentages. Calculator bundles expose inputs for website calculations.
- Raw English is retained for optional cube recipe `Description`, the
  `PType` Prefix/Suffix discriminator, and the `RequiredClass` literal
  used for class-required-equipment validation.
- Existing unnamed skill and untranslated monster contracts can omit
  `NameKey` and retain raw `FallbackName`; consumers display the fallback
  when the key is absent. See the
  [name contracts](docs/WEBSITE-INTEGRATION.md#existing-name-and-asset-contracts)
  and [AGENTS.md](AGENTS.md) for the translation rules.

Lookup data (item-code allow/deny lists, stat-display overrides,
class-name validation, vanilla row counts, cube-recipe note keys, etc.)
lives in JSON under `D2RMultiExport.Lib\Config\` rather than inline in
importer code, so content tweaks don't require a code change.

## How it's structured

| Project | Description |
|---|---|
| `D2RMultiExport.Lib` | Core import / translation / export pipeline. Game-data parsing, property resolution, damage & defense math, synthetic-string registry, and JSON writers. |
| `D2RMultiExport.Console` | Headless CLI front-end (`export` command). |
| `D2RMultiExport.Client` | Avalonia desktop GUI front-end for running exports interactively. |
| `D2RMultiExport.Lib\Config` | Bundled config: `export-config.json`, `synthetic-strings.json`, `stat-overrides.json`, `class-ranges.json`, description layouts and canonical `CASC_DATA\runes.txt`, `skillcalc.txt`, `misscalc.txt`. |

The pipeline runs **load → import → resolve → export → audit** and is
orchestrated from `D2RMultiExportPipeline.cs`. Importers collect per-row
failures in `ImportResult<T>`. Phase exceptions are recorded and abort
by default; `--continue-on-exception` permits continuation.

Exporter exceptions are isolated so later exporters and reports still run.
If the output phase finishes, `manifest.json` is written last, recording
whether the run succeeded and was complete. Failed exporter steps then
throw unless `--continue-on-exception` is set; the CLI returns a failure
exit code for recorded errors either way. An abort before the output phase
can leave the previous bundle and manifest untouched. See
[TESTING.md](docs/TESTING.md) for run and report behavior.

Excel `.txt` parsing is delegated to the
[`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
NuGet package; this repo only consumes the strongly-typed row DTOs it
returns. Parser bugs (wrong column mapping, missing columns, row-count
mismatches, etc.) are fixed upstream in the sibling `d2r-dotnet-tools`
repo rather than worked around inline here — see
[CONTRIBUTING.md](CONTRIBUTING.md#parser-dependency) for the local-debug swap recipe.

## Intended use

This tool is purpose-built to produce the JSON bundle the D2R Reimagined
website consumes. The `keyed/*.json` schema, CLI flags, and config
shape exist to serve that one consumer. Breaking wire changes require
coordination with the website. Cube recipe `Id` values are a published
stable contract; changes that rotate them require a coordinated migration.
See the [integration contracts](docs/WEBSITE-INTEGRATION.md) and
[stable cube recipe identity](docs/WEBSITE-INTEGRATION.md#stable-cube-recipe-identity).
It is not a general-purpose D2R data dumper.

## Prerequisites

- .NET SDK **10.0**
- A D2R mod source tree containing `data\global\excel\*.txt` and
  `data\local\lng\strings\*.json`
- Strongly recommended: a CascView extraction of the base game's `data` tree
  (`data\` or the nested `data\data\`), passed as `--casc-data`. It provides
  available vanilla HD sprites, base strings and tables as fallbacks for
  files the mod does not ship. IAS deliberately reads `global/animdata.d2`
  from the mod only; vanilla animation timings are never substituted
  ([IAS inputs](docs/IAS-CALCULATOR.md#inputs)). The exporter decodes D2R's
  SpA1 `.sprite` files and writes quality-92 WebP files; it does not
  redistribute source game assets.

## Releases

Tagged releases are published from **Actions → Release → Run workflow**
in the GitHub UI and attached to the corresponding `v<version>` GitHub
Release as
`D2RMultiExport-v<version>-win-x64.zip`. The zip's top-level folder is
`D2RMultiExport\` and contains the CLI (`D2RMultiExport.Console.exe`),
the Avalonia GUI (`D2RMultiExport.Client.exe`), the bundled `Config\`
tree, `LICENSE.txt`, `AUTHORS.md` and a short `README.txt`. Both executables are
self-contained, single-file `win-x64` builds — no .NET install
required.

See [`docs/RELEASING.md`](docs/RELEASING.md) for the full procedure.

### Windows SmartScreen on first launch

The published `.exe` files are not code-signed. Windows SmartScreen
will therefore show a blue "Windows protected your PC" warning the
first time each one runs after extraction. Click **More info** →
**Run anyway** to dismiss it. The warning is expected for unsigned
self-contained .NET single-file binaries downloaded from GitHub
Releases; Windows reputation policy determines whether it appears. If that warning is unacceptable
in your environment, build the tool yourself from source instead
of downloading the prebuilt zip.

## Build

```powershell
dotnet build .\D2RMultiExport.sln
```

The **Build Check** workflow (`.github/workflows/ci.yml`, status
context `build-check`) is manual-only (`workflow_dispatch`). It builds Release
and runs the library, Python auditor and headless desktop tests without private
game data; build/test locally before submission ([TESTING.md](docs/TESTING.md)).

## Run — CLI

The console tool exposes a single `export` command.

| Option | Aliases | Description |
|---|---|---|
| `--mod-data` | `-m` | The mod's `data` directory (or the folder containing it) |
| `--casc-data` | `-c` | *(optional, recommended)* The extracted vanilla `data` directory (`data\`, `data\data\`, or the extraction root) |
| `--out` | `--export`, `-o` | Output directory for the generated bundle |
| `--config` | | *(optional)* Override the bundled `Config\` directory |
| `--destructive` | | Replace **all** existing contents of `--out` with only this export after a run without errors (default `false`; see [Export modes](#export-modes)) |
| `--pretty` | | Pretty-print JSON (default `true`) |
| `--early-stop` | | Stop importing `CubeMain.txt` at the first sentinel row (default `true`) |
| `--cube-recipe-descriptions` | | Include the raw English `Description` column from `CubeMain.txt` on each cube recipe (default `false`) |
| `--continue-on-exception` | | Continue past per-phase failures instead of aborting the run; failures are still recorded in `extras\import-report.txt` (default `false`) |

### Source roots

`SourceRoots` derives every input from these two directories:

| Root | Accepted selections | Must contain |
|---|---|---|
| `--mod-data` | the mod's `data\` folder, or the folder containing it | `global\excel` and `local\lng\strings` |
| `--casc-data` | the CASC `data\` root, the extraction root containing `data\`, or CascView's nested `data\data\` | any of `global\excel`, `hd\global`, `local\lng\strings` |

Every input resolves as *mod copy, else CASC copy* (bundled reference tables last
where supported). Without `--casc-data`, vanilla sprites, CASC base strings (the
only source for keys the mod does not override, such as town NPC names, controller
action labels and object names) and any table the mod does not ship are
unavailable: they are reported, never substituted. The only input deliberately
read from the mod root alone is `global\animdata.d2`
([IAS inputs](docs/IAS-CALCULATOR.md#inputs)).

Example:

```powershell
dotnet run --project .\D2RMultiExport.Console -- export `
  --mod-data  "..\d2r-reimagined-mod" `
  --casc-data "<path-to-CascView-extraction>" `
  --out       ".\test-output"
```

An unsupported directory is rejected up front (exit code 3) with the selected path,
the expected relative location and what was found. `extras\source-report.txt` lists
which root supplied every input; `extras\asset-report.txt` explains every unresolved
sprite.

### Export modes

`--out` (the GUI's **Output Files Directory**) is the one and only destination. It
can be any directory, including the website checkout's `static\data`: export, then
review and commit the website changes. There is no separate install step.

- **Default:** the run writes directly into `--out`. Files with matching paths are
  overwritten, new files are created, and every other file is left untouched;
  extra or obsolete files never block an export. `manifest.json` lists only the
  files this run generated.
- **Destructive** (`--destructive`, or the GUI's **Destructive export — replace all
  existing destination contents** checkbox): the run is generated in full inside a
  hidden `.d2r-export-new-*` working directory in `--out` first. Only a run without
  errors then replaces the previous contents: every existing entry, including
  obsolete and unrelated files, is moved into a hidden `.d2r-export-old-*` directory,
  the new export is moved in, and the old copy is deleted. A run with errors, an
  aborted run or a failed rename leaves (or restores) the previous contents, and the
  run's reports are then only in the log or console output. Skipped optional outputs
  are simply absent afterwards. Destructive exports are refused for a drive or
  file-system root, a Git repository root (a folder containing `.git`), user and
  system folders or any folder containing one, the exporter's own folder, and any
  folder containing or inside the Mod Data, CASC Data or configuration directory.

Neither mode creates, moves or deletes anything outside `--out`. A destructive run
killed mid-replacement can leave the hidden working directories behind; the next
destructive export removes them with the other previous contents.

On success the tool prints `Export completed successfully.` along with
the number of `enUS` keys loaded and a count of any referenced-but-
missing translation keys (full list written to
`extras\missing-translations.txt`).

## Run — GUI

```powershell
dotnet run --project .\D2RMultiExport.Client
```

The Avalonia window has folder pickers for the Mod Data, CASC Data and output
directories (each validated as you type, with the reason when a selection is not a
supported layout), a remembered **Destructive export** checkbox (off by default; see
[Export modes](#export-modes)), a "Continue on exception" toggle, a single
**Import and Export** button and a live log panel. Clicking
the log opens a non-modal viewer with the complete log of the current run —
including the full import report once it ends — with search, Info/Warnings/Errors
filters, free selection, Copy All and Save. It can stay open during an export.

## Output layout

```
<out>/
├── manifest.json          # schema 2; current-run payload hashes and step status
├── keyed/                 # 16 potential content/calculator/presentation JSON documents
├── sprites/
│   ├── items/             # successfully converted item art as WebP
│   ├── skills/            # successfully converted skill icons
│   └── ui/inventory.webp
├── strings/               # enUS plus 12 other D2R locales, flat key/text maps
└── extras/
    ├── import-report.txt
    ├── missing-translations.txt
    ├── casc-fallback-report.txt
    ├── source-report.txt
    └── asset-report.txt
```

Missing optional inputs can omit individual documents; consult `manifest.json`
for current-run files and completeness.

In the default mode the exporter leaves skipped/stale/unrelated files untouched;
only the manifest identifies files produced by its output phase.
A destructive export leaves only the manifest and the files it lists. Hashes verify
payload integrity, not source freshness or release authenticity. No browser
manifest requests or cache checks are required.

Website-facing wire contracts, stable recipe ids and the manifest
contract are documented in [WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md).

## License

GPL-3.0-or-later — see [`LICENSE`](LICENSE) for the full license text
and [`AUTHORS.md`](AUTHORS.md) for the project's copyright and
authorship line.

Copyright © 2026 D2R-Reimagined.
