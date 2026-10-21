# Project development standards

`d2r-multi-export-tool` produces the keyed JSON, localization and sprite bundle for the D2R Reimagined website. This file is authoritative for all coding agents and contributors. `CLAUDE.md` imports it; do not maintain a second ruleset. Read the linked contract document before changing its subsystem.

## 1. Purpose and architecture

The pipeline is **load → import → resolve → export → audit**, orchestrated by `D2RMultiExport.Lib/D2RMultiExportPipeline.cs`.

| Path | Responsibility |
|---|---|
| `D2RMultiExport.Lib/Config/` | Bundled JSON and typed `*Config.cs`; `CASC_DATA/` contains upstream reference tables. |
| `D2RMultiExport.Lib/Import/` | Typed row importers, shared property mapping, equipment arithmetic and BBE calculation scopes. |
| `D2RMultiExport.Lib/Translation/` | Property-key resolution, synthetic strings, equipment lines and multi-language bundles. Render English by resolving `KeyedLine` against `strings/enUS.json`; do not add a parallel English property renderer. |
| `D2RMultiExport.Lib/Exporters/` | Keyed documents, language bundles, sprites, reports and publish staging. `ExportOutputs` is the output gateway. |
| `D2RMultiExport.Lib/Models/`, `ErrorHandling/` | Wire DTOs and contextual phase/row diagnostics. |
| `D2RMultiExport.Lib/SourceRoots.cs` | Sole source-path authority for Mod Data and optional CASC Data. |
| `D2RMultiExport.Console/`, `D2RMultiExport.Client/` | CLI (`export`, `publish`) and Avalonia MVVM desktop front ends. |
| `D2RMultiExport.Tests/`, `D2RMultiExport.Client.Tests/` | Library regressions and Avalonia headless interaction/optional real-data tests. |
| `test-output/` | Ignored smoke-export scratch output, never a committed fixture. |

Keep the tool focused on this website bundle. Do not broaden it into a general D2R dumper or rewrite working systems without a demonstrated need.

## 2. Mandatory engineering standards

- Target `net10.0`, C# 14, nullable enabled. Use 4 spaces, no tabs, Allman braces, file-scoped namespaces and `using`s outside namespaces. Match existing order, qualification, `var` usage and comment density; do not normalize unrelated line endings.
- Single-line unbraced `if` is allowed only for guard `return`/`continue` already used in the file. Use `public sealed` for leaf classes and `static` for stateless helpers.
- Use collection expressions (`[]`, `[..source]`). D2R-code dictionaries/sets must use `StringComparer.OrdinalIgnoreCase`; translation keys are opaque, ordinal strings.
- Use `PascalCase` for types/methods/public properties/constants/static readonly tables; `camelCase` for locals/parameters; `_camelCase` for private fields. Config-derived sets end in `Set`.
- Keep I/O async (`Task`/`Task<T>`, static `LoadAsync(path)`); no `.Result`/`.Wait()`.
- Add XML `<summary>` to public types and non-obvious static tables. Preserve/add `// SPDX-License-Identifier: GPL-3.0-or-later` on C# sources.
- Mirror an analogous existing implementation when choosing routine patterns; do not create parallel interpretations or conventions.

## 3. Configuration and hardcoding

**Configurable game content belongs in `Config/*.json` with typed configuration, never inline in importer/exporter code.** This includes item/property/stat codes used as tunable lists, names to skip/override, vanilla row counts, automagic group ids, class allow-lists and legacy/doc-generator parity data. Engine-specific code paths below remain permitted.

For a new `ExportConfig` setting:

1. Add a `camelCase` key to `export-config.json` with a sibling `$<key>Note`.
2. Add `[JsonPropertyName(...)]` to its typed property, with empty defaults (`[]` or a comparer-aware dictionary).
3. For O(1) membership, expose `[JsonIgnore] *Set` and initialize it in `BuildLookups()` with `StringComparer.OrdinalIgnoreCase`.
4. Inject configuration through constructors; importers must not read config JSON directly.

Config JSON uses 4-space indentation and double-quoted strings. Preserve `$` documentation hints; the deserializer accepts trailing commas/comments and ignores unmapped properties. Leave upstream `Config/CASC_DATA/*.txt` formatting untouched. The whole reference folder is copied by the csproj. Optional `supplemental-translations.json` is guarded by `File.Exists`, is not committed and must not become a required `<None Update>` input.

### Authorized retained constants

These exceptions cover fixed engine/file-format/key-shape behavior, not arbitrary game content. Keep non-configurable lookup tables `private static readonly` with an XML explanation. Do not invent a rule language merely to relocate an implementation predicate.

| Permitted category | Existing scope and limits |
|---|---|
| Cube grammar | `CubeQualifierKeyMap`/`CubeRecipeImporter`: qualifier/protocol tokens (`low`, `mag`, `sock`, `qty=`, `pre=`, `usetype`, `useitem`, `any`, op 1/18). Labels remain `cubeIngredientLabels` config. |
| Structural sentinel | `Expansion` in unique/set/monstats loaders; not an item skip list. |
| Internal runeword scopes | `(Weapon)`/`(Armor)`/`(Shield)` in importer/cleanup; map to keyed-line `qualifier`, never display as raw text. |
| Fixed CASC key crosswalks | Class code → `AmaOnly`/`Amazon`-style keys and skill tab → `StrSklTabItem*` in `PropertyKeyResolver`; accepted class names stay in config. |
| Engine damage/defense formulas | Per-property arithmetic in `DamageArmorCalculator` (`dmg%`, `dmg-min`, `dmg/lvl`, `ac%`, `ac/lvl`, `ethereal`, `dur`, engine stat ids); each case selects a formula, not a tunable list. |
| Engine property functions | `PropertyKeyResolver` value-reuse/value-returning `properties.txt` function sets (slot-1 reuse 3/4/5-10/24, min 15, max 16, param 17); display grouping comes from ItemStatCost `dgrp` data, not a code list. |
| Engine equipment behavior | Shield types `shld`/`ashd` for smite/block; `boot` for kick. Internal `"Smite Damage"`/`"Kick Damage"` selectors choose synthetic keys; `DamageStringPrefix` must never serialize. |
| Special stat rendering/encoding | `PropertyKeyResolver`/`PropertyMapper`/`RequirementHelper` paths for `ease`, `item_req_percent`, `item_levelreq`, `item_indesctructible`, `item_cannotbefrozen`, `item_extrablood`, `reqlevel`, `fade`, `charm-weight`, `item_addclassskills`, `randclassskill`, `item_addskill_tab`, `tab-rand`, `skill`, `oskill`; engine `coldlength`/`poisonlength` frame-to-second conversion. |

**Deferred content exceptions remain narrowly scoped:**

- `CubeRecipeImporter.ProcessNotes`: existing compound input/output/param/name predicates (including `ka3`, `ooc`, `pk1`, `cx7`, Torch/Annihilus/Latent fissures, param 386, corruption/ethereal checks; `bag` is not in the configured reroll set). Extraction needs a predicate schema and recipe-by-recipe baseline; do not expand this into permission for new hardcoded content.
- `PropertyCleanup`: existing elemental merge/pairing table and priorities 100/98/96/92, partially overlapping `elementalDamagePairs`/`splitElementalCodes`. Consolidation must preserve merge order and compare uniques/sets output.
- Existing cube portal names (Cow, Pandemonium, Pandemonium Finale, Red Portal) mapped to synthetic keys.

## 4. Importer and exporter conventions

- Consume the `D2RReimaginedTools.FileExtensions` NuGet typed DTOs. Fix parser bugs in sibling `../d2r-dotnet-tools`; do not reparse raw `.txt` inside importers, patch typed DTOs or shadow misparsed columns in config. The local ProjectReference debug procedure is in [CONTRIBUTING.md](CONTRIBUTING.md#parser-dependency); committed csproj files must use the published PackageReference.
- Every raw property column uses `PropertyMapper.MapRows`/`MapExpanded`, including set bonuses, runewords, gems/runes, cube outputs, affixes, automagic and property-group children. Never call `Map` directly on an unexpanded column code. Preserve param/min/max, `itemsRequired`, `qualifier`, `ModChance` and row metadata on expanded/merged lines.
- Resolve root-relative inputs through `SourceRoots`: mod first, CASC next, bundled reference tables last where supported. Accept layouts only there; never infer parents/siblings of Excel, use `..` to find sources or recursively search. Lookup is case-insensitive per segment. `global/animdata.d2` is deliberately mod-only via `ResolveIn(SourceOrigin.Mod, ...)`.
- Register each exporter with `RunExportStepAsync`, declare its `OutputPath`(s), and route a non-null warning callback to `Export/<producer>`. Gate optional table sets through `ExcelInputs.RequireAll`; a missing optional input skips only that output.
- Never `ToDictionary` over keyed `.txt` rows without duplicate handling. Use `ExcelInputs.IndexFirst` (report, first wins) or `DataLoader.Put` (report, last wins). Preserve intentionally row-based affixes/treasure classes rather than deduplicating them by name.
- Use typed numbered-column accessors (`SkillCalcSource`, exporter accessors), not unchecked reflection. Legacy `ImportReflection` slot readers distinguish empty cell (`null`), invalid integer (warn with file/row, `null`) and missing/wrongly typed DTO property (`ImportColumnException` with DTO/column/caller; programming error).
- For skill work, read [SKILL-CALCULATIONS.md](docs/SKILL-CALCULATIONS.md) and the CASC Data Guide `dataguide/docs/bbe-calc.html` before changing semantics. Use shared `CalcExpression`/`ICalcScope`; formulas and column aliases come from skillcalc/misscalc tables, not transcribed C#. Description layouts belong in `skill-desc-functions.json`; arithmetic belongs in the calculator.

## 5. Error handling and file integrity

- Importers record per-row failures in `ImportResult<T>` (`AddError`/`AddWarning`) and continue. Fatal load/configuration failures may propagate; do not disguise programming errors as empty data.
- Missing optional inputs produce contextual warnings and omissions, never exceptions. Malformed required calculator input or invalid config fails its isolated export step; later exporters, reports and the manifest still run. Existing optional art-manifest/sprite readers explicitly warn and omit invalid art while retaining item rows; do not conflate that supported degradation with malformed required tables.
- Report phase/producer, file, row/identifier, expression/column and root cause as applicable. Use `CalcTrace` expansion chains for unresolved calculations; drop an unsolved description line rather than guessing. Asset diagnostics include mapping, searched paths, reason and fallback, summarized once per reason in the import report.
- **All export writes go through `ExportOutputs.WriteJsonAsync`/`WriteTextAsync`/`WriteAsync`.** Never directly `File.Create`/`File.WriteAll*` an output. Writes use temp + flush + atomic rename. Reject rooted/escaping paths and links/junctions below the output root, including on deletion.
- **The exporter does not own the output directory.** Never sweep `keyed`, `strings`, `sprites` or `extras`; never delete stale, skipped or unrelated files. Delete only paths a failed step attempted during this run, and the previous manifest at the start of the output phase. Declared-but-unwritten files are `NotWritten`, never vouched for.
- Abandoned temp cleanup is limited to the same output's directory and exact `.<name>.<32 lowercase hex>.tmp` shape, not open files or links; report `AbandonedTempFilesRemoved`. No general cleanup.
- Preserve opened-handle containment checks on Windows/Linux and path revalidation before rename. The final check-to-rename link race remains a documented limit ([SECURITY.md](SECURITY.md)); other platforms have path checks only. Do not claim an atomic filesystem snapshot.
- Manifest schema **2** is written last and lists current-run files and every output/report step. `Succeeded` requires no recorded errors; `Complete` additionally requires all steps written with no declared output missing. Translation harvest/audits read current-run outputs only. Field definitions: [WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md#manifest-schema-2-and-publishing).
- Publish through `PublishStager`, never copy the export directory wholesale. Keep its preconditions (new/empty, non-nested staging; schema 2; successful run; complete unless explicitly allowed; matching copied hashes/lengths) and commit the captured manifest last; failed staging has no completed manifest. Never modify the source export to repair staging.
- Every emitted `Sprite`/`Icon` must resolve to a file this run wrote; otherwise record `Completeness/asset-references`. Run skill icons before skill JSON and emit only successful icon references.

## 6. Data and website compatibility

**Player-facing values under `keyed/` are translation keys or `KeyedLine { key, args }`, not finalized English.** Resolve properties through `PropertyKeyResolver`; register new strings in `synthetic-strings.json`/`SyntheticStringRegistry.Keys` (conventionally `str…`). Arguments are primitives, nested keyed lines or `KeyedLineArg` records.

- Keyed DTO properties are **PascalCase**; keyed-line members deliberately use `key`/`args` and their annotated casing. Never add a global camel-case policy. `strings/<lang>.json` remains a flat key → localized text map.
- Finalize numeric arguments/tables here. Consumers substitute positional `%d`/`%s`, select/clamp table axes and evaluate the explicitly exported live synergy percentages/calculator inputs; do not replace finalized values with raw values plus English templates.
- The three authorized raw-English cases remain: optional cube `Description` (configured ingredient-token labels also belong to that existing cube allowance), structural `PType` (`Prefix`/`Suffix`), and `RequiredClass` English literals gated by `validRequiredClassNames`. Do not convert `RequiredClass` to keys without a coordinated breaking website change. A new display exception requires updating this file in the same change.
- Structural identifiers are separate from display exceptions: register new identifier-only fields in `D2RMultiExportPipeline.IdentifierOnlyProperties`. Loot-filter selectors preserve literal UnHoarder case/whitespace and source type graph ([LOOT-FILTER.md](docs/LOOT-FILTER.md)). Automagic `Code` is the row name; optional `NameKey` is emitted only when resolvable.
- `NameKey` is a promise of a resolvable translation, never a raw identifier. For existing unnamed skill/monster contracts, omit null `NameKey` and retain `FallbackName`; consumers render the fallback when absent. Audit actual display references against final enUS (the language fallback), including unprobed keys/arguments/composite tables; exclude structural fields. Zero missing keys does not mean every source identifier is translated.
- Preserve grouped-property parent/child shape: structural `code`, keyed `nameKey` (`strPropertyGroupsProperty`), parent `pickMode`, children with `chance` (relative weights for mode `"2"`, percentages otherwise). Never flatten children or copy `pickMode` to each child.
- Preserve skill axes (`lvl` effective, `blvl` hard points, `ulvl` character level), `values` alongside conditional `values2d`/`valuesUlvl`, meaningful hard row 0, trimmed constant tails and `LevelTableConverter`. Keep actual source percentage context for `scale`/`scaleCalc`; never borrow the describing row's Params or invent approximations. Contracts and limits: [SKILL-CALCULATIONS.md](docs/SKILL-CALCULATIONS.md).
- Cube recipe `Id` is a published stable contract, including raw-cell normalization and collision suffix ordering. Do not change its algorithm/columns or allow a parser bump to rotate ids without a coordinated migration ([WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md#stable-cube-recipe-identity)).
- Update [WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md) in the same change whenever adding/reshaping a keyed field. Coordinate breaking changes; document exporter behavior and consumer requirements, not website implementation status. Manifest hashes are export/publishing integrity metadata, not a requirement for browser verification or additional requests.

## 7. Testing and validation

- [TESTING.md](docs/TESTING.md) owns commands, test selection and report review. Level A: localized code uses an affected build and fast deterministic suites; documentation/comments use diff/link review only. Level B: importer/exporter corrections use applicable regression and final-JSON tests, plus the Python auditor tests for audit/shared property calculations.
- Level C (the full-validation script, run once) is required for broad exporter changes, shared arithmetic/mapping/schema changes, parser upgrades, releases, explicitly requested complete verification, or inadequate targeted coverage; reuse its retained export for compatible content checks. CLI/publishing changes and releases add its CLI repeat/staging option. Add meaningful regressions for defects and failure paths.
- Build once per configuration/change set and use matching `--no-build`; repeat only after new changes, failures or unresolved concerns. Inspect reports/affected JSON: no new errors, unexplained calculation drops or missing display keys; review new warnings.
- Disclose excluded, skipped and unexecuted checks. Optional real-data tests skip without inputs; Level C must fail when required inputs/checks cannot execute. Never equate deterministic passes with required full-data validation. Keep scratch sessions separate and never clean unrelated output/source directories. Headless desktop tests are not manual GUI verification, source inspection is not browser rendering, and portable code is not verified Linux execution.

## 8. Documentation and commits

- Keep docs/examples aligned with actual CLI options, two-root layouts, failure behavior and wire contracts. Preserve known limitations without turning each into a redesign. Investigation history, run counts, commit hashes and local paths belong in commit/PR descriptions, not committed docs; do not add transient counts or claims of pending website work to permanent standards.
- Follow Conventional Commits: `type(scope?)!?: lowercase description`; types `feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert`, `!` or `BREAKING CHANGE:` for breaks. At least one compliant commit is required by `validate-commits`; squash merge with PR title is intended.
- Build Check is manual `workflow_dispatch`, not an automatic PR build. Build/test locally; the manual workflow can confirm on a clean runner before merge. Follow [CONTRIBUTING.md](CONTRIBUTING.md) and the PR template; run `dotnet restore .\D2RMultiExport.sln` after package changes and leave no local parser ProjectReference swap.

## 9. Detailed references

- [README.md](README.md): supported source layouts, CLI/GUI usage and output layout.
- [CONTRIBUTING.md](CONTRIBUTING.md): onboarding, parser debugging and PR workflow. [RELEASING.md](docs/RELEASING.md): release and branding procedures.
- [TESTING.md](docs/TESTING.md): validation levels, smoke commands, reports, fixtures and real-data/desktop tests.
- [SKILL-CALCULATIONS.md](docs/SKILL-CALCULATIONS.md): skill wire axes, BBE scopes/vocabulary and bounded limitations.
- [WEBSITE-INTEGRATION.md](docs/WEBSITE-INTEGRATION.md): consumer-facing wire contracts, stable recipe ids and the manifest/publishing contract.
- [DROP-CALCULATOR.md](docs/DROP-CALCULATOR.md), [IAS-CALCULATOR.md](docs/IAS-CALCULATOR.md), [HEALTH-CALCULATOR.md](docs/HEALTH-CALCULATOR.md), [LOOT-FILTER.md](docs/LOOT-FILTER.md): specialized exporter contracts.
- [SECURITY.md](SECURITY.md): threat model, private reporting and filesystem/integrity limits.
- [PROPERTY-RANGES.md](docs/PROPERTY-RANGES.md): current property mapping, range preservation and independent audit limits.
