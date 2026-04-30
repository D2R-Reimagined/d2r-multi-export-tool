# AGENTS.md

Guidance for AI agents (Claude, Junie, Copilot, Codex, etc.) working on
`d2r-multi-export-tool` — the data exporter that produces the key-based
JSON bundle consumed by the companion D2R Reimagined website.

This file documents the project's non-obvious conventions so that
automated edits do not silently break the contract with the website
consumer. Read it in full before proposing changes.

> If you only have time to read three rules:
>
> 1. **No hardcoding.** New lookup data, codes, item lists, or magic numbers
>    belong in `D2RMultiExport.Lib/Config/*.json`, surfaced through a
>    strongly-typed `*Config.cs` class — *not* inline in importer/exporter
>    code.
> 2. **Exports are keyed.** Every player-facing string written under
>    `<out>/keyed/*.json` must be a translation `key` (or a `KeyedLine`
>    `{ key, args }`) — never a baked English sentence. The website resolves
>    keys against `<out>/strings/<lang>.json` at runtime.
> 3. **Four documented exceptions** to rule (2) — and only these four —
>    are allowed to ship raw English / raw codes: cube recipe
>    `Description` text, the `PType` Prefix/Suffix discriminator, the
>    `RequiredClass` literal used for class-required-equipment validation,
>    and the `propertygroups.txt` parent `code` (e.g. `Magnetic-Affix1`)
>    on a `KeyedLine` group node. See *Exceptions* below.

---

## Repository layout

| Path | Purpose |
|---|---|
| `D2RMultiExport.Lib/` | Core import → translation → export pipeline. All game-data parsing, property resolution, damage/defense math, synthetic-string registry, and JSON writers. |
| `D2RMultiExport.Lib/Config/` | Bundled JSON configuration (`export-config.json`, `synthetic-strings.json`, `stat-overrides.json`, `class-ranges.json`) plus matching `*Config.cs` POCOs. Also `CASC_DATA/` — currently ships only the canonical `runes.txt` used by `RunewordImporter` to flag vanilla runewords; users who hit missing translations should extract `data\local\lng\strings\*.json` themselves with CascView and pass the directory via `--base-strings`. **Optional, not committed**: `supplemental-translations.json` is consumed by `TranslationService.LoadSupplementalAsync`; the pipeline guards the read with `File.Exists`, so the file is absent in normal runs and intentionally not registered under `<None Update>`. Drop it next to the bundled configs only when you need to inject extra enUS keys for a one-off run. |
| `D2RMultiExport.Lib/Import/` | One importer per `.txt` family: `UniqueImporter`, `SetImporter`, `RunewordImporter`, `CubeRecipeImporter`, plus shared helpers (`PropertyMapper`, `EquipmentHelper`, `RequirementHelper`, `DamageArmorCalculator`, `PropertyCleanup`, `DataLoader`). |
| `D2RMultiExport.Lib/Translation/` | `PropertyKeyResolver`, `SyntheticStringRegistry`, `MultiLanguageTranslationService`, `TranslationService` (narrow: enUS descfunc probing + missing-translations audit), `EquipmentLineBuilder`, `CubeQualifierKeyMap`. The keyed wire format flows through here. There is **no parallel English-string property renderer** anymore — the previous `PropertyStringResolver` and its `DisplayString` / `PropertyString` plumbing on `CubePropertyExport` / `ExportProperty` were removed during the redundancy audit. New work that wants a "human-readable" rendering of a property must resolve the `KeyedLine` against `strings/enUS.json` instead of reaching for a non-existent English path. |
| `D2RMultiExport.Lib/Exporters/` | Final JSON writers: `KeyedJsonExporter` (the `keyed/*.json` files) and `LanguageBundleExporter` (`strings/<lang>.json`). |
| `D2RMultiExport.Lib/Models/` | DTOs shared between import and export (`ExportModels.cs`). |
| `D2RMultiExport.Lib/ErrorHandling/` | `ImportResult<T>` and per-phase error collection. |
| `D2RMultiExport.Lib/D2RMultiExportPipeline.cs` | Top-level orchestration (load → import → resolve → export → audit). |
| `D2RMultiExport.Console/` | Headless CLI front-end (`export` command). |
| `D2RMultiExport.Client/` | Avalonia desktop GUI front-end (MVVM under `Models/`, `ViewModels/`, `Views/`). |
| `docs/TESTING.md` | How to run the verified end-to-end export. |
| `test-output/` | Local scratch output from running the tool against the reimagined mod. **Not committed as a fixture.** |

---

## Coding & style conventions

These mirror the existing codebase. New code must match.

### General C#
- **Target framework:** `net10.0`. Language version: **C# 14**. Nullable
  reference types are **enabled** project-wide.
- **Indentation:** 4 spaces, no tabs. LF or CRLF as already present in the
  file — do not normalize the whole file in unrelated edits.
- **Braces:** Allman style, opening brace on its own line. Single-line
  `if` bodies without braces are acceptable only for guard `return`s, in
  the same style already used (e.g. `if (entry.Enabled == false) continue;`).
- **Namespaces:** **File-scoped** (`namespace D2RMultiExport.Lib.Config;`).
  Do not introduce block-scoped namespaces.
- **`using` directives:** at the top of the file, *outside* the namespace,
  in the order the file already uses them. Prefer fully-qualified names for
  one-off references (e.g. `System.Text.Json.JsonDocument`) when the rest of
  the file does so.
- **Class declarations:** prefer `public sealed class` for leaf types
  (DTOs, importers, config records). Mark helpers `static` when they hold
  no state.
- **Collections:** use C# 12 collection expressions `[]` for empty defaults
  (`= []`), `[..source]` for spreads. Do not switch existing `new()`
  initializers back to `new List<...>()`.
- **Dictionaries / sets** keyed by D2R codes (`"cjw"`, `"rin"`, etc.) **must**
  use `StringComparer.OrdinalIgnoreCase`. The codebase is consistent on
  this; new code that misses it will silently regress lookups.
- **Async:** all I/O is `async Task` / `async Task<T>`; do not introduce
  `.Result` or `.Wait()`. Static factory loaders follow the
  `LoadAsync(path)` pattern (see `ExportConfig.LoadAsync`).
- **Comments / XML docs:** `<summary>` on public types and on any non-obvious
  static lookup table. Match the existing density — do not pepper trivial
  code with comments.
- **No `var` purity rule** — both `var` and explicit types are used; match
  the surrounding code.
- **Exceptions:** importers never throw to the caller for per-row failures;
  they record into `ImportResult<T>` via `AddError` / `AddWarning`. Only
  fatal load failures (missing file, unparseable JSON) propagate.

### Naming
- **Types / methods / public properties:** `PascalCase`.
- **Locals / parameters / private fields:** `camelCase`. Private instance
  fields use a leading underscore: `_excelPath`, `_data`, `_config`.
- **Constants / `static readonly` lookup tables:** `PascalCase`
  (`IdentifierOnlyProperties`, `IngredientTokenLabels`,
  `SkippedAutoMagicGroupsSet`).
- **Config-derived hashsets:** suffix `Set` on the derived lookup property
  (`IgnoredUniqueItemsSet`, `BlockedCubeInputCodesSet`, …) — the JSON
  source list keeps the plain name (`ignoredUniqueItems`).
- **Translation keys:** treat as opaque `Ordinal` strings. Synthetic keys
  introduced by this tool live in `SyntheticStringRegistry.Keys` and
  conventionally start with `str` (e.g. `strCubeNoteEarlyGamePotion`).

### JSON conventions
- All `*.json` files in `D2RMultiExport.Lib/Config/` use **4-space**
  indentation, `camelCase` property names, and double-quoted strings.
  This matches the project-wide 4-space convention used in C# sources.
  (The bundled CASC-derived `runes.txt` under
  `D2RMultiExport.Lib/Config/CASC_DATA/` is imported data and is left
  in its upstream formatting — do not reformat it.)
- Documentation hints inside config files are stored as sibling keys
  prefixed with `$` (e.g. `"$skippedCubeRecipesNote": "..."`). These are
  ignored by the deserializer (`AllowTrailingCommas = true`,
  `ReadCommentHandling = JsonCommentHandling.Skip`, plus unmapped-property
  tolerance) and **must** be preserved when editing.
- Wire format under `<out>/keyed/`:
  - Property names: **`PascalCase`** (matches the C# DTOs in
    `Models/ExportModels.cs` and `Exporters/KeyedJsonExporter.cs`). The
    serializer does **not** apply a camel-case policy for keyed exports.
  - Player-facing values: `KeyedLine` objects `{ "key": "...", "args": [...] }`
    or arrays of them.
- Wire format under `<out>/strings/<lang>.json`: a flat
  `{ "key": "localized text", … }` map per language.

### Tests / smoke-test
There is no automated unit-test project today; the verified end-to-end run
documented in `docs/TESTING.md` and `README.md` is the project's smoke
test. Any change that touches the import or export pipeline must be
validated with that command before submitting.

### Working with `test-output\`

`test-output\` at the repo root is a **scratch test folder owned by the
smoke-test command** (`docs/TESTING.md` §2). It is the de-facto behavioural
baseline for any refactor that touches the import / translation / export
pipeline. Agents may freely:

- Take a baseline snapshot (`test-output\` → `test-output-baseline\`) before
  destructive work, diff `keyed\*.json` between baseline and post-refactor
  to confirm parity, and then delete the baseline snapshot at the end.
- Re-run the smoke export and let it overwrite `test-output\` — that is the
  intended workflow, not a violation of the "don't touch files you didn't
  create" rule.

The only reason to skip the smoke export is if the change is documented as
non-pipeline (docs / comments / unrelated tooling). When skipping, say so
explicitly in the submit summary so the next agent doesn't waste time
re-running it.

---

## The "no hardcoding" rule (in detail)

If you find yourself typing **any** of the following inline in importer
or exporter code, **stop** and put it in `export-config.json` instead:

- Lists of D2R item codes (`"cjw"`, `"aqv"`, `"cm1"`, …)
- Lists of unique / set / runeword names to skip or override
- Property codes (`stat` codes like `dmg-fire`, `fire-min`, `state`, …)
- Magic numbers that represent vanilla `*.txt` row counts
  (`441`, `142`, `100`)
- AutoMagic group ids
- Class-name allow-lists
- Any "doc-generator parity" / "legacy hardcoded" data

The canonical pattern is:

1. Add the data to `D2RMultiExport.Lib/Config/export-config.json` under a
   `camelCase` key, with a sibling `$<key>Note` describing intent.
2. Add a matching property to `ExportConfig` with `[JsonPropertyName(...)]`,
   defaulting to an empty collection (`= []` / `= new(StringComparer.OrdinalIgnoreCase)`).
3. If the consumer needs O(1) lookup, expose a `[JsonIgnore]` `*Set`
   property and populate it inside `BuildLookups()` with the correct
   comparer.
4. Inject the `ExportConfig` instance through the importer's constructor
   (see `CubeRecipeImporter`, `UniqueImporter`, `PropertyMapper`,
   `EquipmentHelper` for the established pattern). Do **not** read the
   JSON directly inside an importer.

If a value genuinely cannot live in JSON (e.g. it's a code-shape lookup,
not data) call it out in an XML doc comment explaining why, and prefer a
`private static readonly` table over scattered literals.

> Any importer- or exporter-level constant that meaningfully describes
> *content* (not *structure*) belongs in config. Constants that describe
> the wire format, translation key shape, or the structure of the input
> `.txt` files may stay in code.

---

## External parsing dependency — fix upstream, not here

All `data\global\excel\*.txt` parsing is delegated to the
**`D2RReimaginedTools.FileExtensions`** NuGet package (consumed via
namespaces `D2RReimaginedTools.TextFileParsers` and
`D2RReimaginedTools.Models.*`). Importers under
`D2RMultiExport.Lib/Import/` consume the strongly-typed row DTOs that
package returns (e.g. `D2RReimaginedTools.Models.CubeMain`); they do
**not** parse `.txt` columns by hand, and they must not start to.

If you encounter a parsing bug — wrong column mapping, a column the
row DTO doesn't expose, a row count off-by-one, locale handling, etc.
— **the fix belongs in the library, not in this repo.** Specifically,
the following workarounds are out of bounds and will be rejected at
review:

- Re-parsing the raw `.txt` inline inside an importer to recover a
  missing/wrong column.
- Hand-patching the typed row DTO after the fact (mutating fields in
  the importer to compensate for a parser bug).
- Shadowing a misparsed column via `export-config.json` so the
  exporter "happens to" produce the right answer.

The upstream source lives at the sibling clone
`..\d2r-dotnet-tools` (same `D2R-Reimagined` org). To reproduce a
parsing issue against an unreleased fix, swap the package reference
in `D2RMultiExport.Lib/D2RMultiExport.Lib.csproj` for a local
`<ProjectReference>`:

```xml
<!-- Replace, locally only, while debugging an upstream parser fix: -->
<!-- <PackageReference Include="D2RReimaginedTools.FileExtensions" Version="..." /> -->
<ProjectReference Include="..\..\d2r-dotnet-tools\<...>.FileExtensions\<...>.FileExtensions.csproj" />
```

Validate locally with the smoke export in `docs/TESTING.md`, open the
PR against `d2r-dotnet-tools`, and bump the
`D2RReimaginedTools.FileExtensions` `Version` pin in this repo only
after the upstream PR ships a release. Revert the
`<ProjectReference>` swap before committing — the committed `.csproj`
must always restore from NuGet so CI and the release workflow stay
reproducible.

Dependabot PRs (configured in `.github/dependabot.yml`) keep the
`<PackageReference>` versions in sync — no extra config needed.

---

## The "keyed exports for the website" rule (in detail)

The downstream `d2r-reimagined-website` swaps languages with a single
extra fetch. That only works because every player-facing field in
`<out>/keyed/*.json` is either:

- a translation **key** string that exists in `<out>/strings/<lang>.json`,
  or
- a `KeyedLine` object `{ "key": "<key>", "args": [ … ] }` whose `args`
  are themselves either primitives, nested `KeyedLine`s, or `KeyedLineArg`
  records.

Therefore:

- **Never** write a finalized English sentence (e.g.
  `"+25% Enhanced Damage"`) into a keyed export. Resolve the key via
  `Translation.PropertyKeyResolver`, register a synthetic key via
  `SyntheticStringRegistry` if no CASC key exists, and emit
  `KeyedLine.Of(key, args)`.
- **Numeric arithmetic is finalized server-side** (here). Only the
  positional `%d` / `%s` substitution is left to the client. Compute the
  number in C#, pass it through `args` — do not split a single property
  into "raw value + English template" on the wire.
- **Property names on the wire are `PascalCase`.** Don't introduce a
  `JsonNamingPolicy.CamelCase` for the keyed exporter — it would break
  every existing website query path.

If you have to introduce a brand-new player-facing string with no
matching CASC key, register it in `synthetic-strings.json`
(human-readable English) so the language bundles include it.

---

## Documented exceptions

These four — and only these four — are allowed to bypass the
key-everything rule. New exceptions require an explicit decision; do not
add a fifth without updating this file.

### 1. Cube recipe descriptions

`CubeRecipeExport.Description` is the raw English description string from
`CubeMain.txt`. It is included **only** when
`CubeRecipeImporter.CubeRecipeUseDescription` is enabled (see
`CubeRecipeImporter.cs`). The matching ingredient-token labels (now stored
in `export-config.json` → `cubeIngredientLabels`, with separate `input` /
`output` maps) are also raw English by design, because there is no CASC
translation key for tokens like `"sock=#"` ("Item with Sockets (#)").
Editing those labels does not require a code change. Cube recipe
*content* (inputs, outputs, class restriction, notes) **is** keyed via
`KeyedLine`, `RequiredClass`, and `cubeRecipeNotes` in
`export-config.json`.

### 2. `PType` Prefix/Suffix discriminator

`KeyedMagicAffix.PType` (and the corresponding column read in
`D2RMultiExportPipeline.cs`) ships the raw `"Prefix"` / `"Suffix"` token
because it is a **structural identifier** the website branches on, not a
player-facing string. It is enumerated in
`D2RMultiExportPipeline.IdentifierOnlyProperties` along with `Code`,
`NormCode`, `UberCode`, `UltraCode`, `AutoPrefix`, `Vanilla`,
`RequiredClass`, `Class`, and `ClassSpecific`, so the
missing-translations audit correctly ignores their values.

### 3. Class-required equipment validation

`KeyedEquipment.RequiredClass` ships the literal English class name
(`"Sorceress"`, `"Barbarian"`, …). The allow-list of accepted values
lives in `export-config.json` → `validRequiredClassNames` and is
exposed as `ExportConfig.ValidRequiredClassNamesSet`. The list acts
**only as a gate** — it validates that the value resolved from the base
item's `Equiv2` is a real class name; the resolved string itself is
written through to the wire as-is. The website branches on this exact
literal for class-restriction styling, alongside the localized
`KeyedLine` produced via `PropertyKeyResolver.TryGetClassOnlyKey`. Do
not change the allow-list to translation keys without coordinating a
breaking change with the website.

### 4. `propertygroups.txt` parent `code`

When a unique item's property list references a `propertygroups.txt`
entry (e.g. crafted charms reference `Magnetic-Affix1..6` /
`Gelid-Affix*` groups), `UniqueImporter.ExpandPropertyGroup` emits a
single parent `KeyedLine` carrying:

- `code` — the raw English group code (`"Magnetic-Affix1"`,
  `"Gelid-Affix3"`, …) verbatim from the propertygroups row,
- `pickMode` — the group's `PickMode` column (typically `"2"` for
  crafted-charm affix groups), and
- `children` — the resolved sub-property `KeyedLine`s, each carrying
  its own `chance` (the per-row `ChanceN` column verbatim — a relative
  pick weight when `pickMode == "2"`, a percentage otherwise).

The parent `code` is a **structural identifier** the website branches
on (to label and group the affix bucket); there is no CASC translation
key for these tokens. The lowercase `code` field is registered in
`D2RMultiExportPipeline.IdentifierOnlyProperties` so the
missing-translations audit ignores its value. `pickMode` is also a
structural identifier (a numeric mode token) and `chance` is a number,
so neither needs an audit-set entry. The keyed wire schema for the
parent line therefore looks like:

```jsonc
{
  "key": "",                 // parent has no template of its own
  "args": [],
  "code": "Magnetic-Affix1", // raw English passthrough
  "pickMode": "2",
  "children": [
    { "key": "...", "args": [...], "chance": 100 },
    { "key": "...", "args": [...], "chance":  50 }
  ]
}
```

Do **not** flatten the children back into siblings or stamp `pickMode`
on every child — the parent/child hierarchy is part of the website
contract.

---

## Working checklist for AI agents

Before submitting a change, verify:

- [ ] Touched code follows the **indentation, naming, and namespace**
      rules above (4-space, file-scoped, PascalCase types, `_camelCase`
      private fields).
- [ ] Any new lookup data lives in `D2RMultiExport.Lib/Config/*.json`,
      not inline in C#. New JSON keys are `camelCase` and have a sibling
      `$<key>Note` if their purpose isn't obvious.
- [ ] New `ExportConfig` properties use `[JsonPropertyName(...)]`,
      default to an empty collection, and (if needed for O(1) lookup)
      have a matching `[JsonIgnore] *Set` populated in `BuildLookups()`
      with `StringComparer.OrdinalIgnoreCase`.
- [ ] No newly-introduced player-facing string is written as raw English
      into `<out>/keyed/*.json`. It is either a `KeyedLine` keyed
      against an existing CASC key or registered in
      `synthetic-strings.json`.
- [ ] If a new field carries a raw identifier (item code, class id,
      flag), it is added to `D2RMultiExportPipeline.IdentifierOnlyProperties`
      so it does not pollute the missing-translations audit.
- [ ] If a fifth exception to the keyed-export rule is genuinely
      required, this file (`AGENTS.md`) is updated in the same change to
      document it.
- [ ] The verified end-to-end command from `README.md` /
      `docs/TESTING.md` was run and `Export completed successfully.` was
      printed; `extras/import-report.txt` contains no new errors.
- [ ] Solution builds cleanly: `dotnet build .\D2RMultiExport.sln`. (The
      **Build Check** workflow — `.github/workflows/ci.yml`, status
      context `build-check` — runs the same command on every PR.)
- [ ] At least one commit on the PR follows Conventional Commits
      (see *Commits & releases* below). The **Validate Commits**
      workflow — `.github/workflows/pr-semantic-commits.yml`, status
      context `validate-commits` — enforces this.
- [ ] Any `.txt` parsing issue encountered was fixed upstream in
      `..\d2r-dotnet-tools` (the `D2RReimaginedTools.FileExtensions`
      package), **not** worked around inline in this repo. The
      committed `D2RMultiExport.Lib.csproj` still uses
      `<PackageReference>` (no leftover local `<ProjectReference>`
      swap).

---

## Commits & releases

### Conventional Commits

The **Validate Commits** workflow (`pr-semantic-commits.yml`, status
context `validate-commits`, mirroring the policy used in the sibling
`reimagined-launcher` repo) enforces that **at least one** commit on
the branch follows the Conventional Commits format:

```
type(scope?)!?: short, lowercase description
```

Allowed `type` values:

```
feat | fix | docs | style | refactor | perf | test | build | ci | chore | revert
```

- Use `!` (e.g. `feat!:` / `feat(api)!:`) for breaking changes — or add
  a `BREAKING CHANGE:` paragraph in the commit body. Both feed the
  release-notes generator.
- Squash-merging with the PR title as the commit subject is the
  intended workflow; the squashed subject becomes the entry shown in
  the auto-generated release notes.

### Releases

Tagged releases are produced manually from
**Actions → Release → Run workflow** in the GitHub UI (see
`docs/RELEASING.md` for the full procedure). The workflow:

1. Validates the SemVer `version` input.
2. Publishes `D2RMultiExport.Console` and `D2RMultiExport.Client`
   side-by-side as a self-contained, single-file `win-x64` build
   (`-p:PublishSingleFile=true`,
   `-p:IncludeNativeLibrariesForSelfExtract=true`,
   `-p:Version=<input>`).
3. Verifies the bundled `Config\` tree shipped (the
   `D2RMultiExport.Lib.csproj` `<None Update>` rules must keep
   propagating `export-config.json`, `synthetic-strings.json`,
   `stat-overrides.json`, `class-ranges.json`, and
   `Config\CASC_DATA\runes.txt`).
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

## Quick reference — where things live

| You want to … | Edit this |
|---|---|
| Skip a unique / set / runeword | `export-config.json` → `ignoredUniqueItems` / `vanilla*MaxRow` / `vanillaUniqueOverrides` |
| Drop a stat code from the export | `export-config.json` → `ignoredPropertyCodes` |
| Add a magic-quality name override | `export-config.json` → `magicNameOverrides` |
| Skip a cube recipe by op/param/value | `export-config.json` → `skippedCubeRecipes` |
| Block a cube input code | `export-config.json` → `blockedCubeInputCodes` |
| Annotate cube recipes with extra notes | `export-config.json` → `cubeRecipeNotes` (the `notes` entries are translation keys) |
| Add a synthetic player-facing string | `synthetic-strings.json` |
| Override a stat's display formatting | `stat-overrides.json` (+ `StatOverrideConfig.cs`) |
| Adjust per-class skill/level ranges | `class-ranges.json` (+ `ClassRangeConfig.cs`) |
| Resolve a property to a translation key | `Translation/PropertyKeyResolver.cs` |
| Change the wire shape of a keyed export | `Models/ExportModels.cs` and `Exporters/KeyedJsonExporter.cs` together |
| Add a new identifier-only property | `D2RMultiExportPipeline.IdentifierOnlyProperties` |
| Fix a `.txt` parser bug / missing column / wrong row mapping | Upstream in `..\d2r-dotnet-tools` (`D2RReimaginedTools.FileExtensions`); bump the `<PackageReference>` `Version` here after the upstream release |
| Bump / add / remove a NuGet package | Edit the `<PackageReference>`, then `dotnet restore .\D2RMultiExport.sln` |

When in doubt, search for an existing analogous case (e.g. how
`magicNameOverrides` flows from JSON → `ExportConfig` → `EquipmentHelper`)
and mirror it.
