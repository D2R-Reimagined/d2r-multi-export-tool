# AGENTS.md

Rules for AI agents editing `d2r-multi-export-tool` — exporter producing the keyed JSON bundle for the D2R Reimagined website. Human onboarding/build/PR/release/branding: [`CONTRIBUTING.md`](./CONTRIBUTING.md).

## Three rules that matter most

1. **No hardcoding** — content data goes in `D2RMultiExport.Lib/Config/*.json` + typed `*Config.cs`, never inline in importer/exporter code.
2. **Exports are keyed** — every player-facing string in `<out>/keyed/*.json` is a translation `key` or `KeyedLine { key, args }`, never a baked English sentence.
3. **Only three documented raw-English exceptions**: cube recipe `Description`, `PType` Prefix/Suffix discriminator, `RequiredClass`. A fourth requires updating this file in the same change.

## Repository layout

| Path | Purpose |
|---|---|
| `D2RMultiExport.Lib/` | Core import → translation → export pipeline. |
| `D2RMultiExport.Lib/Config/` | Bundled JSON config + `*Config.cs` POCOs. `CASC_DATA/runes.txt` ships; users supply `data\local\lng\strings\*.json` via `--base-strings`. **Optional, not committed**: `supplemental-translations.json` — `TranslationService.LoadSupplementalAsync` guards with `File.Exists`; intentionally not in `<None Update>`. |
| `D2RMultiExport.Lib/Import/` | One importer per `.txt` family: `UniqueImporter`, `SetImporter`, `RunewordImporter`, `CubeRecipeImporter`; helpers `PropertyMapper`, `EquipmentHelper`, `RequirementHelper`, `DamageArmorCalculator`, `PropertyCleanup`, `DataLoader`. |
| `D2RMultiExport.Lib/Translation/` | `PropertyKeyResolver`, `SyntheticStringRegistry`, `MultiLanguageTranslationService`, `TranslationService` (enUS descfunc probing + missing-translations audit), `EquipmentLineBuilder`, `CubeQualifierKeyMap`. **No parallel English-string property renderer exists** — old `PropertyStringResolver`/`DisplayString`/`PropertyString` was removed. To render English, resolve `KeyedLine` against `strings/enUS.json`. |
| `D2RMultiExport.Lib/Exporters/` | `KeyedJsonExporter` (`keyed/*.json`), `LanguageBundleExporter` (`strings/<lang>.json`). |
| `D2RMultiExport.Lib/Models/` | DTOs (`ExportModels.cs`). |
| `D2RMultiExport.Lib/ErrorHandling/` | `ImportResult<T>` + per-phase error collection. |
| `D2RMultiExport.Lib/D2RMultiExportPipeline.cs` | Orchestration (load → import → resolve → export → audit). |
| `D2RMultiExport.Console/` | Headless CLI (`export` command). |
| `D2RMultiExport.Client/` | Avalonia GUI (MVVM). |
| `docs/TESTING.md` | Verified end-to-end smoke export. |
| `test-output/` | Local scratch output. **Not a committed fixture.** |

## Coding rules

- **Target:** `net10.0`, **C# 14**, nullable enabled. 4-space indent, no tabs. Don't normalize line endings on unrelated edits.
- **Braces:** Allman. Single-line `if` without braces only for guard `return`/`continue` already used in the file.
- **Namespaces:** file-scoped only. **`using`s** outside namespace, match file's existing order/qualification.
- **Classes:** `public sealed` for leaves; `static` for stateless helpers.
- **Collections:** C# 12 expressions (`= []`, `[..src]`); don't revert to `new List<...>()`.
- **Dicts/sets keyed by D2R codes** (`"cjw"`, `"rin"`, …) **must** use `StringComparer.OrdinalIgnoreCase`.
- **Async** all the way: `async Task`/`async Task<T>`, no `.Result`/`.Wait()`. Static loaders: `LoadAsync(path)`.
- **Importers never throw** for per-row failures — record into `ImportResult<T>` via `AddError`/`AddWarning`. Only fatal load failures (missing file, unparseable JSON) propagate.
- **XML doc** `<summary>` on public types and non-obvious static lookup tables. Match existing comment density.
- `var` vs explicit type, comment frequency: match surrounding code.

### Naming

- Types/methods/public properties: `PascalCase`. Locals/parameters: `camelCase`. Private fields: `_camelCase` (`_excelPath`, `_data`, `_config`).
- Constants/`static readonly` tables: `PascalCase` (`IdentifierOnlyProperties`, `IngredientTokenLabels`).
- Config-derived hashsets: suffix `Set` on the derived property (`IgnoredUniqueItemsSet`); JSON source key stays plain (`ignoredUniqueItems`).
- Translation keys: opaque `Ordinal` strings. Synthetic keys in `SyntheticStringRegistry.Keys`, conventionally `str…` (e.g. `strCubeNoteEarlyGamePotion`).

### JSON conventions

- `Config/*.json`: 4-space indent, `camelCase` keys, double-quoted strings. `Config/CASC_DATA/runes.txt` is upstream data — leave its formatting untouched.
- Doc hints: sibling `$`-prefixed keys (e.g. `"$skippedCubeRecipesNote"`). Deserializer ignores them (`AllowTrailingCommas`, `JsonCommentHandling.Skip`, unmapped-property tolerance). **Preserve on edits.**
- Wire `<out>/keyed/`: property names **`PascalCase`** (matches `Models/ExportModels.cs`). **No** camel-case policy — adding one breaks every website query path. Player-facing values are `KeyedLine` or arrays of them.
- Wire `<out>/strings/<lang>.json`: flat `{ "key": "localized text", … }`.

## "No hardcoding" rule

If you'd inline any of these in importer/exporter code, **stop** and put it in `export-config.json`:

- D2R item codes (`"cjw"`, `"aqv"`, `"cm1"`, …)
- Unique/set/runeword names to skip or override
- Property/stat codes (`dmg-fire`, `fire-min`, `state`, …)
- Magic numbers for vanilla `*.txt` row counts (`441`, `142`, `100`)
- AutoMagic group ids, class-name allow-lists
- Any "doc-generator parity"/"legacy hardcoded" data

Canonical pattern:

1. Add to `export-config.json` under a `camelCase` key, with sibling `$<key>Note`.
2. Add an `ExportConfig` property with `[JsonPropertyName(...)]`, defaulting to empty (`= []` / `= new(StringComparer.OrdinalIgnoreCase)`).
3. For O(1) lookup, expose `[JsonIgnore]` `*Set`, populate in `BuildLookups()` with `StringComparer.OrdinalIgnoreCase`.
4. Inject `ExportConfig` via importer constructor (see `CubeRecipeImporter`, `UniqueImporter`, `PropertyMapper`, `EquipmentHelper`). Do **not** read JSON directly inside an importer.

If a value genuinely cannot live in JSON (code-shape lookup, not data), keep it `private static readonly` and document why in XML doc. **Content → config; wire-format/key-shape/`.txt` structure → may stay in code.**

## External parser — fix upstream, not here

`data\global\excel\*.txt` parsing is delegated to `D2RReimaginedTools.FileExtensions` NuGet (`D2RReimaginedTools.TextFileParsers`, `D2RReimaginedTools.Models.*`). Importers consume typed row DTOs (e.g. `D2RReimaginedTools.Models.CubeMain`) — they do **not** parse columns by hand.

Rejected workarounds: re-parsing raw `.txt` inline; hand-patching the typed DTO; shadowing a misparsed column via `export-config.json`.

Upstream: `..\d2r-dotnet-tools` (same org). To debug an unreleased fix, swap the `<PackageReference>` in `D2RMultiExport.Lib.csproj` for a local `<ProjectReference>`, validate via smoke export, ship the upstream PR, then bump `<PackageReference>` `Version`. **Revert the swap before committing** — committed `.csproj` must always restore from NuGet. Dependabot syncs versions via `.github/dependabot.yml`.

## "Keyed exports" rule

Every player-facing field in `<out>/keyed/*.json` is either a translation **key** present in `<out>/strings/<lang>.json`, or a `KeyedLine { "key", "args" }` whose `args` are primitives, nested `KeyedLine`s, or `KeyedLineArg` records.

- **Never** write a finalized English sentence (e.g. `"+25% Enhanced Damage"`). Resolve via `Translation.PropertyKeyResolver`, register via `SyntheticStringRegistry` if no CASC key exists, emit `KeyedLine.Of(key, args)`.
- **Numbers are finalized server-side** (here). Only positional `%d`/`%s` substitution is left to the client. Compute in C#, pass via `args` — don't split into "raw value + English template" on the wire.
- **Wire property names are `PascalCase`.** Do not add `JsonNamingPolicy.CamelCase` to the keyed exporter.
- New player-facing strings without a CASC key: register in `synthetic-strings.json` (human-readable English) so language bundles include them.

## Documented exceptions (only these three)

**1. Cube recipe descriptions.** `CubeRecipeExport.Description` ships raw English from `CubeMain.txt`, only when `CubeRecipeImporter.CubeRecipeUseDescription` is enabled. Ingredient-token labels: `export-config.json` → `cubeIngredientLabels` (separate `input`/`output` maps), also raw English (no CASC key for `"sock=#"`-style tokens). Recipe **content** (inputs, outputs, class restriction, notes) is keyed via `KeyedLine`, `RequiredClass`, `cubeRecipeNotes`.

**2. `PType` Prefix/Suffix discriminator.** `KeyedMagicAffix.PType` ships raw `"Prefix"`/`"Suffix"` — structural identifier the website branches on, not a player-facing string. Enumerated in `D2RMultiExportPipeline.IdentifierOnlyProperties` along with `Code`, `NormCode`, `UberCode`, `UltraCode`, `AutoPrefix`, `Vanilla`, `RequiredClass`, `Class`, `ClassSpecific`, so the missing-translations audit ignores them.

**3. Class-required equipment.** `KeyedEquipment.RequiredClass` ships the literal English class name (`"Sorceress"`, `"Barbarian"`, …). Allow-list: `export-config.json` → `validRequiredClassNames`, exposed as `ExportConfig.ValidRequiredClassNamesSet`. The list **only gates** — the resolved string is written through as-is. The website branches on this exact literal alongside the localized `KeyedLine` from `PropertyKeyResolver.TryGetClassOnlyKey`. Don't change to translation keys without coordinating a breaking change with the website.

## Property groups (not an exception)

When a unique's properties reference `propertygroups.txt` (e.g. crafted charms with `Magnetic-Affix1..6`/`Gelid-Affix*`), `UniqueImporter.ExpandPropertyGroup` emits a parent `KeyedLine`:

- `code` — raw English group code (`"Magnetic-Affix1"`, …), structural; lowercase `code` is in `IdentifierOnlyProperties` (same shape as `PType`). No CASC key exists.
- `nameKey` — synthetic `strPropertyGroupsProperty` (enUS: `"Random Grouped Affix"`, in `synthetic-strings.json`). The bucket label is keyed and resolved like any other; the parent line is **not** an exception.
- `pickMode` — group's `PickMode` column (typically `"2"` for crafted-charm affix groups). Structural numeric token.
- `children` — sub-property `KeyedLine`s, each carrying `chance` (per-row `ChanceN`: relative pick weight when `pickMode == "2"`, percentage otherwise).

```jsonc
{
  "key": "", "args": [],
  "code": "Magnetic-Affix1",
  "nameKey": "strPropertyGroupsProperty",
  "pickMode": "2",
  "children": [
    { "key": "...", "args": [...], "chance": 100 },
    { "key": "...", "args": [...], "chance":  50 }
  ]
}
```

Do **not** flatten children to siblings or stamp `pickMode` on every child — the parent/child hierarchy is part of the website contract.

## Verification (smoke test)

No unit-test project exists. The end-to-end export in `docs/TESTING.md` **is** the smoke test. Any change touching import/translation/export must run it; success prints `Export completed successfully.` with no new errors in `extras/import-report.txt`.

`test-output/` is the de-facto behavioural baseline owned by the smoke command. Agents may freely snapshot `test-output/` → `test-output-baseline/` before destructive work, diff `keyed/*.json` for parity, then delete the snapshot; or re-run the smoke export and overwrite `test-output/`.

Skip the smoke export only for documented non-pipeline changes (docs/comments/tooling); state so explicitly in the submit summary.

## Pre-submit checklist

- [ ] Indent/naming/namespaces match rules above.
- [ ] New lookup data is in `Config/*.json` (not inline); new JSON keys are `camelCase` with `$<key>Note` if non-obvious.
- [ ] New `ExportConfig` properties have `[JsonPropertyName(...)]`, default empty, and (if needed) `[JsonIgnore] *Set` populated in `BuildLookups()` with `StringComparer.OrdinalIgnoreCase`.
- [ ] No new player-facing raw English in `<out>/keyed/*.json` — `KeyedLine` keyed against CASC or registered in `synthetic-strings.json`.
- [ ] New raw-identifier fields added to `D2RMultiExportPipeline.IdentifierOnlyProperties`.
- [ ] Fourth keyed-export exception (if genuinely needed) documented in this file in the same change.
- [ ] Smoke export from `docs/TESTING.md` ran successfully (when pipeline touched).
- [ ] `dotnet build .\D2RMultiExport.sln` clean locally (matches the manual-only **Build Check** workflow `ci.yml`, run on demand via `workflow_dispatch` before merge — no automatic PR trigger).
- [ ] At least one commit follows Conventional Commits (`type(scope?)!?: lowercase description`; types: `feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert`; `!` or `BREAKING CHANGE:` body for breaks). Enforced by `pr-semantic-commits.yml` (status `validate-commits`). Squash-merge with PR title is the intended flow.
- [ ] No leftover local `<ProjectReference>` swap for `D2RReimaginedTools.FileExtensions` — committed `.csproj` uses `<PackageReference>`.

Release/branding/icon workflow and full human-contributor onboarding: [`CONTRIBUTING.md`](./CONTRIBUTING.md), [`docs/RELEASING.md`](./docs/RELEASING.md).

## Quick reference — where things live

| You want to … | Edit this |
|---|---|
| Skip a unique/set/runeword | `export-config.json` → `ignoredUniqueItems` / `vanilla*MaxRow` / `vanillaUniqueOverrides` |
| Drop a stat code | `export-config.json` → `ignoredPropertyCodes` |
| Add a magic-quality name override | `export-config.json` → `magicNameOverrides` |
| Skip a cube recipe by op/param/value | `export-config.json` → `skippedCubeRecipes` |
| Block a cube input code | `export-config.json` → `blockedCubeInputCodes` |
| Annotate cube recipes | `export-config.json` → `cubeRecipeNotes` (`notes` are translation keys) |
| Add a synthetic player-facing string | `synthetic-strings.json` |
| Override a stat's display formatting | `stat-overrides.json` (+ `StatOverrideConfig.cs`) |
| Adjust per-class skill/level ranges | `class-ranges.json` (+ `ClassRangeConfig.cs`) |
| Resolve a property to a translation key | `Translation/PropertyKeyResolver.cs` |
| Change wire shape of a keyed export | `Models/ExportModels.cs` + `Exporters/KeyedJsonExporter.cs` together |
| Add a new identifier-only property | `D2RMultiExportPipeline.IdentifierOnlyProperties` |
| Fix a `.txt` parser bug / missing column | Upstream in `..\d2r-dotnet-tools`; bump `<PackageReference>` `Version` after release |
| Bump/add/remove a NuGet package | Edit `<PackageReference>`, then `dotnet restore .\D2RMultiExport.sln` |

When in doubt, mirror an analogous existing case (e.g. `magicNameOverrides` flowing JSON → `ExportConfig` → `EquipmentHelper`).
