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
- Writes a bundle of `keyed/*.json` (one file per content family) and
  `strings/<lang>.json` (one per supported D2R locale), plus an
  `extras/` audit folder.

## Why it works this way

The downstream website renders item pages in 13 languages and switches
locale with a single extra fetch. That only works if the bulk JSON
contains no baked English sentences:

- Every player-facing field in `keyed/*.json` is either a translation
  key string or a `KeyedLine` object `{ key, args }` whose `args` are
  primitives, nested `KeyedLine`s, or `KeyedLineArg` records.
- All numeric arithmetic — ED%, ethereal ×1.5, durability adjustments,
  smite/kick damage, elemental durations, requirement reductions, charm
  weight, per-character-level scaling, etc. — is finalized here, in C#.
  The client only performs positional `%d` / `%s` substitution against
  `strings/<lang>.json`.
- Three structural exceptions ship raw English by design (cube recipe
  `Description`, the `PType` Prefix/Suffix discriminator, and the
  `RequiredClass` literal used for class-required-equipment validation).
  See `AGENTS.md` for the full rule set.

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
| `D2RMultiExport.Lib\Config` | Bundled config: `export-config.json`, `synthetic-strings.json`, `stat-overrides.json`, `class-ranges.json`, plus the canonical `CASC_DATA\runes.txt`. |

The pipeline runs **load → import → resolve → export → audit** and is
orchestrated from `D2RMultiExportPipeline.cs`. Per-row failures are
collected into `ImportResult<T>` rather than thrown; only fatal load
failures abort the run (unless `--continue-on-exception` is passed).

Excel `.txt` parsing is delegated to the
[`D2RReimaginedTools.FileExtensions`](https://www.nuget.org/packages/D2RReimaginedTools.FileExtensions)
NuGet package; this repo only consumes the strongly-typed row DTOs it
returns. Parser bugs (wrong column mapping, missing columns, row-count
mismatches, etc.) are fixed upstream in the sibling `d2r-dotnet-tools`
repo rather than worked around inline here — see `AGENTS.md` →
*External parsing dependency* for the local-debug swap recipe.

## Intended use

This tool is purpose-built to produce the JSON bundle the D2R Reimagined
website consumes. The `keyed/*.json` schema, CLI flags, and config
shape exist to serve that one consumer and may change whenever the mod
or the site needs them to. It is not a general-purpose D2R data dumper.

## Prerequisites

- .NET SDK **10.0**
- A D2R mod source tree containing `data\global\excel\*.txt` and
  `data\local\lng\strings\*.json`
- Optional: a CASC dump of the base game's `local\lng\strings`
  directory, used to fill in keys the mod doesn't override. The repo
  does not ship Blizzard's verbatim string tables. If
  `extras\missing-translations.txt` reports keys after a run, extract
  `data\local\lng\strings\*.json` from a D2R install with CascView and
  pass that directory via `--base-strings`.

## Releases

Tagged releases are published from **Actions → Release → Run workflow**
in the GitHub UI and attached to the corresponding `v<version>` GitHub
Release as
`D2RMultiExport-v<version>-win-x64.zip`. The zip's top-level folder is
`D2RMultiExport\` and contains the CLI (`D2RMultiExport.Console.exe`),
the Avalonia GUI (`D2RMultiExport.Client.exe`), the bundled `Config\`
tree, `LICENSE.txt`, and a short `README.txt`. Both executables are
self-contained, single-file `win-x64` builds — no .NET install
required.

See [`docs/RELEASING.md`](docs/RELEASING.md) for the full procedure.

### Windows SmartScreen on first launch

The published `.exe` files are not code-signed. Windows SmartScreen
will therefore show a blue "Windows protected your PC" warning the
first time each one runs after extraction. Click **More info** →
**Run anyway** to dismiss it. The warning is expected for unsigned
self-contained .NET single-file binaries downloaded from GitHub
Releases and will subside automatically once enough downloads
accumulate against a given release. If that warning is unacceptable
in your environment, build the tool yourself from source instead
of downloading the prebuilt zip.

## Build

```powershell
dotnet build .\D2RMultiExport.sln
```

CI runs the same command on every pull request via
`.github/workflows/ci.yml`.

## Run — CLI

The console tool exposes a single `export` command.

| Option | Aliases | Description |
|---|---|---|
| `--excel` | `-e` | Path to the mod's `data\global\excel` directory |
| `--mod-strings` | `--translations`, `-t` | Path to the mod's `data\local\lng\strings` directory |
| `--out` | `--export`, `-o` | Output directory for the generated bundle |
| `--base-strings` | `-b` | *(optional)* Path to a CASC dump of the base-game `local\lng\strings` dir, used as fallback for keys the mod doesn't override |
| `--config` | | *(optional)* Override the bundled `Config\` directory |
| `--pretty` | | Pretty-print JSON (default `true`) |
| `--early-stop` | | Stop importing `CubeMain.txt` at the first sentinel row (default `true`) |
| `--cube-recipe-descriptions` | | Include the raw English `Description` column from `CubeMain.txt` on each cube recipe (default `false`) |
| `--continue-on-exception` | | Continue past per-phase failures instead of aborting the run; failures are still recorded in `extras\import-report.txt` (default `false`) |

Example:

```powershell
dotnet run --project .\D2RMultiExport.Console -- export `
  --excel        "..\d2r-reimagined-mod\data\global\excel" `
  --mod-strings  "..\d2r-reimagined-mod\data\local\lng\strings" `
  --base-strings "<path-to-CascView-extracted-strings>" `
  --out          ".\test-output"
```

On success the tool prints `Export completed successfully.` along with
the number of `enUS` keys loaded and a count of any referenced-but-
missing translation keys (full list written to
`extras\missing-translations.txt`).

## Run — GUI

```powershell
dotnet run --project .\D2RMultiExport.Client
```

The Avalonia window provides folder pickers for the same paths, a
"Continue on exception" toggle, and a scrollable log panel that streams
import/export pipeline progress and per-phase issues live.

## Output layout

```
<out>/
├── keyed/
│   ├── armors.json
│   ├── weapons.json
│   ├── magicprefix.json
│   ├── magicsuffix.json
│   ├── runewords.json
│   ├── sets.json
│   ├── uniques.json
│   └── cube-recipes.json
├── strings/
│   ├── enUS.json
│   ├── deDE.json
│   ├── esES.json
│   ├── esMX.json
│   ├── frFR.json
│   ├── itIT.json
│   ├── jaJP.json
│   ├── koKR.json
│   ├── plPL.json
│   ├── ptBR.json
│   ├── ruRU.json
│   ├── zhCN.json
│   └── zhTW.json
└── extras/                # audit artifacts (not shipped to the website)
    ├── import-report.txt
    ├── missing-translations.txt
    └── synthetic-strings.txt
```

## License

GPL-3.0-or-later — see [`LICENSE`](LICENSE) for the full license text
and [`AUTHORS.md`](AUTHORS.md) for the project's copyright and
authorship line.

Copyright © 2026 D2R-Reimagined.
