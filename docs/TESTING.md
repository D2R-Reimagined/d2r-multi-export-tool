# Regression testing and export validation

This is the authoritative guide for validation commands and test selection. Choose
one level for the change; do not cumulatively run every example in this document.
Build once per configuration/change set, then use matching `--no-build` commands.
Restore after package changes. Disclose skipped, excluded and unexecuted checks.

## 1. Validation levels

### Level A — routine development

Documentation/comments only: review the diff, links and command examples and run
`git diff --check`. No build or export is required. For localized code, build the
relevant test project (which builds its dependencies) or the solution when front
ends, packages or build settings change; run the affected deterministic suite.
The whole library suite is inexpensive and is a useful default:

```powershell
dotnet build .\D2RMultiExport.Tests
dotnet test .\D2RMultiExport.Tests --no-build --filter 'Category!=RealData'
```

The filter prevents inherited source variables from starting an unrequested full
export. For desktop interactions, build `D2RMultiExport.Client.Tests` instead and
run `dotnet test .\D2RMultiExport.Client.Tests --no-build --filter 'Category!=RealData'`.
Both projects also work with `dotnet test .\D2RMultiExport.sln --no-build` after a
solution build. Verify nonzero discovery counts and command exit codes.

### Level B — targeted regressions

Use existing .NET filters; retain all assertions. Shared property work runs the
final-JSON/real-row snapshot tests and independent auditor tests:

```powershell
dotnet build .\D2RMultiExport.Tests
dotnet test .\D2RMultiExport.Tests --no-build --filter 'Category!=RealData&(FullyQualifiedName~Property|FullyQualifiedName~TranslationAuditRegressionTests)'
python -B -m unittest discover -s tools -p test_property_range_audit.py -v
```

The auditor uses only the Python standard library; Build Check runs it on Python
3.12. Auditor-only changes do not
require a C# build. Run Python tests when audit semantics or shared property
calculations change. Select additional cohesive suites with this table:

| Change | `--filter` (combine with `Category!=RealData` during iteration) |
|---|---|
| Skill expressions/descriptions | `FullyQualifiedName~SkillRegressionTests|FullyQualifiedName~TranslationAuditRegressionTests` |
| Sources, typed columns, optional inputs | `FullyQualifiedName~SourceRootsTests|FullyQualifiedName~ImportReflectionTests|FullyQualifiedName~InputHandlingTests` |
| Named item artwork, contained artwork paths, sprite reuse/fallbacks | `FullyQualifiedName~ItemPresentationExporterTests|FullyQualifiedName~SourceRootsTests` |
| Writes, manifests | `FullyQualifiedName~OutputIntegrityTests|FullyQualifiedName~WriteSafetyTests` |
| Default/destructive export modes | `FullyQualifiedName~DestructiveExportTests` (desktop checkbox: `DestructiveExportSettingTests` in `D2RMultiExport.Client.Tests`) |
| Translation/reference audits | `FullyQualifiedName~TranslationAuditRegressionTests|FullyQualifiedName~PropertyRangeTests` |

`PropertyRangeTests` exercises final JSON as well as importer behavior using the
committed snapshots in `D2RMultiExport.Tests/Fixtures/PropertyRanges`.
Level B is sufficient for a localized correction with adequate deterministic
coverage. Use Level C for shared arithmetic/mapping semantics, parser upgrades,
schema changes, broad exporter/configuration changes, or gaps in targeted
coverage. IAS, health, loot and specialized drop JSON currently lack adequate
focused exporter suites, so changes to those behaviors still require Level C.
`ItemPresentationExporterTests` covers named-art compatibility, ambiguity, tiers,
mod/CASC precedence, contained relative paths, canonical sprite reuse and fallback
diagnostics in final JSON. Sprite decoding changes outside that coverage still
require Level C.
Desktop source-root/pipeline wiring changes require the real-data desktop case.

### Level C — comprehensive validation

Required for broad exporter changes, shared arithmetic/mapping/schema changes,
parser upgrades, releases, explicitly requested complete verification and gaps in
targeted coverage. From repository root:

```powershell
.\scripts\Validate-Full.ps1 -ModData '<path-to-mod>' -CascData '<path-to-casc-extraction>'
# Releases and CLI changes additionally require the independent CLI repeat:
.\scripts\Validate-Full.ps1 -ModData '<path-to-mod>' -CascData '<path-to-casc-extraction>' -CheckCli
```

Choose the appropriate command once. Defaults: Release, a new system-temp session
folder. `-Configuration Debug` and `-SessionPath <new-directory>` are supported;
source parameters default to `D2R_MOD_DATA`/`D2R_CASC_DATA`. Existing session paths
are rejected, never cleaned. The script:

1. Rejects unavailable required roots before building; supplied invalid layouts
   fail in `SourceRoots` validation rather than skipping.
2. Builds once and runs all library tests in required real-data mode. The existing
   integration case still executes the pipeline and all intermediate `GameData`
   assertions. It retains one complete, full-cube export (`--early-stop false`).
3. Requires manifest schema 2, success, completeness, written steps and no declared
   outputs missing. Runs all Python regressions, then the independent raw-TSV
   auditor against that same bundle, verifying payload hashes/lengths and parity.
4. Runs all desktop tests. The real-data case uses the retained bundle as its
   direct-pipeline parity baseline, then performs its own three desktop exports:
   with CASC, without CASC, restored CASC. No root-transition assertion is removed.
5. With `-CheckCli`, repeats the export through the actual CLI in the
   same bundle directory, compares all manifest `Path`/`Sha256`/`Bytes` entries,
   and checks an unrelated sentinel survived.

A nonzero command exit, auditor exit 1 or 2, zero discovered tests, or any skipped
required test fails the script. TRX files, audit JSON and per-check timings/status
in `validation-results.json` stay beside the bundle. Steps after a failure are
unexecuted, not passed. Review reports and affected JSON (§2.1): no new errors,
missing display keys, unexplained calculation drops or unreviewed new warnings.
Known optional art omissions need an explicit source-specific baseline. Automated
checks do not replace this review. Keep sources, configuration and code unchanged
within the session; hashes attest audit-time bytes, not an atomic source snapshot.
If the mod checkout is being edited or switched between branches, validate a
frozen copy instead (for example `git archive <commit> data` extracted into the
session folder) and record that commit.

The real-data library case asserts zero missing display keys against the
supplied mod. A translation the mod has not added yet (for example a new runeword
name) fails it; that is a mod-side gap to report, not a reason to relax the
assertion.

CLI invocation, root transitions/restoration, repeated-export determinism,
atomic/stale-output integrity and intermediate importer state
remain independent checks. A final bundle cannot establish intermediate
`GameData` state.

### Desktop runner and real-data status

`D2RMultiExport.Client.Tests` pins xUnit v3 3.2.2, the version
Avalonia.Headless.XUnit 12.1.3 is built against. Do not upgrade it independently:
xUnit v3 4.0.1 breaks Avalonia fact/theory discovery (`MissingMethodException`) and
its transitive Microsoft.Testing.Platform rejects the VSTest path used by
`dotnet test` and the manual CI workflow. The library stays on xUnit v2. Headless
cases exercise real mouse/keyboard/window interactions; successful
compilation/discovery alone is insufficient.

Both integration cases have `Category=RealData`. Without either input variable,
ordinary runs explicitly report **Skipped**, never Passed. With supplied but
invalid roots they **fail**. `D2R_REQUIRE_REAL_DATA=1` makes missing inputs a failure
with "Required real-data validation could not execute". Example standalone checks:

```powershell
$env:D2R_REQUIRE_REAL_DATA = '1'
dotnet test .\D2RMultiExport.Tests --no-build --filter 'Category=RealData'
dotnet test .\D2RMultiExport.Client.Tests --no-build --filter 'Category=RealData'
Remove-Item Env:D2R_REQUIRE_REAL_DATA
```

The full script sets and restores these variables automatically. Its internal
`D2R_VALIDATION_EXPORT` handshake requires the library to generate a **new** output
path, then lets desktop parity reuse it. Use it only within one unchanged session;
there is no validity claim for an older filesystem bundle.
Ordinary CI lacks game roots: it runs all deterministic suites and openly skips
these two optional cases. This is not full-data or release validation.

There is no persistent export cache. Any future cache must fingerprint exporter
code including uncommitted changes, mod/CASC contents, configuration, options and
schema, and verify manifest integrity/completion; timestamps alone cannot
establish validity.

## 2. Inspect a session export or run a focused CLI smoke

Reuse the Level C bundle for all compatible content checks below. Run the focused
CLI example only when invocation/arguments need separate coverage, after a matching
Release build. Accepted `--mod-data`/`--casc-data` layouts and root precedence are
documented in [README.md](../README.md#source-roots):

```powershell
dotnet run --project .\D2RMultiExport.Console -c Release --no-build -- export `
  --mod-data  "<path-to-mod>" `
  --casc-data "<path-to-casc-extraction>" `
  --out       "<new-session-output>" --early-stop false
```

Hundreds of missing item sprites usually mean an incomplete CASC dump (one
without `hd\global\ui\items`), not a path bug. A mapping whose sprite exists in
neither root is a mod-side data issue reported in `asset-report.txt`; do not edit
the mod repository to hide it.

Expected stdout: `Export completed successfully.` followed by the
`enUS keys loaded` count. Any `missing translation key(s) referenced`
line needs investigation (§2.1) — open `extras\missing-translations.txt`
in the output folder.

### 2.1 Read the reports

Once the run reaches the output/report phase it writes these files under
`extras\` in the output folder. Fatal configuration/load failures before that phase
can leave the previous bundle and manifest untouched; check exit status as well:

| File | What it tells you |
|---|---|
| `import-report.txt` | Every error and warning, grouped by phase. The header line is `=== Import Report: N error(s), M warning(s) ===`. **Any error is a failure.** Review every warning: a `SkillDescription` warning is a regression or a new mod data gap (its tail names the empty column or token), and an unresolved-asset warning names a mapping whose sprite neither root has. |
| `missing-translations.txt` | Actual display keys referenced by current-run `keyed\*.json` that the final enUS fallback bundle does not resolve, grouped by the exported document that referenced them. Expected count: 0 (see below). |
| `casc-fallback-report.txt` | Keys that had to come from the CASC base strings instead of the mod's own strings. |
| `asset-report.txt` | Every unresolved item sprite / skill icon with its identifier, asset mapping, expected source path, every location searched, the reason and the fallback (if any); plus supported fallbacks and output-name collisions. `import-report.txt` carries one grouped line per reason. |
| `source-report.txt` | The two roots and, for every data table, asset manifest, sprite sheet and string folder, which root supplied it. A `missing` entry with `--casc-data` supplied deserves a look. |

The run also fails (`Completeness/asset-references` error) if any `Sprite`/`Icon` in a
keyed document points at a file this run did not write — a stale `.webp` from an
earlier export never counts.

A `SkillDescription` warning includes the root cause and expansion chain: read
its tail, not only the outer alias. Prose-only quantities (`sctk`), live-unit
conditions and per-cast `rand` are legitimate limits; empty columns or unknown
tokens identify mod data gaps. The supported vocabulary, axes and isolated
preview rules are in [SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md).

The expected final enUS missing-display-key count is **0** with the complete
CASC extraction. This audits explicit keys, keyed-line arguments, composite
tables and core identity references, excluding structural identifiers and
FallbackName. Any entry needs investigation; it may be an exporter regression,
an incomplete CASC fallback or a source translation gap. Fix source gaps with
mod authors, not by inventing translations. `casc-fallback-report.txt` records
promoted vanilla keys separately.

`DataLoader` reports a repeated primary key (`itemstatcost.txt`
`Stat`, `itemtypes.txt` `Code`, `skills.txt` `skill`, `monstats.txt`
`Id`, `charstats.txt` `class`, `missiles.txt` `Missile`) instead of
overwriting it silently. A new duplicate warning is a mod data error.

Skill ids, monster indices and property-group ids are engine row indices
(`EngineRowIndex`): the row position after the `Expansion` marker is dropped, so
they cannot collide. The informational `*Id`/`*hcIdx` columns are never read as
identifiers; one warning per table summarizes non-blank cells that disagree with
the row position (blank cells are not reported). Such a warning is stale mod
metadata, not an exporter defect, and does not change any exported id. In an
editor's 1-based line numbering (header on line 1), a `monstats.txt` row is index
line − 2 above the `Expansion` line and line − 3 below it.

The game numbers distinct `monstats.txt` `Id` names separately from rows; a
repeated `Id` (case-insensitive) or a second blank `Id`/blank line makes the two
diverge, which `DataLoader` reports once. Name references then reach other rows
in game; numeric indices are unaffected. This warning is a mod data error.

Item sprite files include named variants, so their count is not the number of
base-item rows. Without `--casc-data` the import report adds "no CASC" warnings
and grouped sprite omissions.

`test-output\` is in `.gitignore` — scratch output, never a committed fixture.
By default the tool does not own the output folder: files it did not write in this
run stay on disk, and only `manifest.json` identifies current files and run status.
`--destructive` instead leaves only this run's files after a run without errors
([README](../README.md#export-modes)). Manifest fields and their limits are
specified in
[WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#manifest-schema-2-and-output-integrity).
Delete `test-output\` yourself when you want a clean tree.

### 2.1.1 Independent property range audit

The Level C script already runs this auditor. To inspect that same full-cube
bundle separately, use its retained path; do not generate another export:

```powershell
python -B tools/property_range_audit.py --export '<session>/export' > '<session>/property-audit.json'
$LASTEXITCODE
```

The auditor reads raw TSV independently of the production parser/mapper and
compares source occurrences with final JSON. It uses only paths recorded by
`SourceRoots` in the current manifest's `extras/source-report.txt`. Manifest
hashes and lengths are verified first; unmanifested/stale JSON cannot satisfy a
property. Source/config hashes describe bytes at **audit time**, not an atomic
export-time snapshot. Keep inputs unchanged between export and audit.

Exit `0` means all evaluated occurrences pass without unresolved coverage;
`1` means missing/collapsed/incorrect values or a failed/incomplete manifest
(input/integrity failures also terminate with an error); `2` means unresolved
semantics, unavailable tables or unclassified source eligibility remain. Review
`Counts`, `TableCoverage`, `UnresolvedEligibility`, and every non-verified
occurrence's `Reason`; `Unsupported`, `Unevaluated` and `NotExported` are never
correctness passes. The report includes original code/param/min/max, functions,
expected transformation, exported arguments and source row/column/record.

`Function17Coverage` summarizes all occurrences containing `ItemModsSetParam`,
including compound damage definitions. Primary function-17 expectations record
parameter precedence, endpoint/default-zero fallback and stat contribution
values separately from display arguments. Value-less templates are checked by
key and scope without invented numeric arguments. `IntentionalNonDisplayStat`
and `UnreferencedAutomagicDefinition` inventory proven omissions; they are not
correctness passes. Recorded source hashes must be compared when auditing an
older export after upstream data changes.

See [PROPERTY-RANGES.md](PROPERTY-RANGES.md) for supported semantic categories,
current evidence and limits. New functions require independently established
expectations and final-JSON fixtures; do not add ignored findings to make a run
green.

### 2.2 Optional base-game inputs degrade, they do not abort

A bare mod checkout carries only overrides; each input below falls back to the
CASC copy when `--casc-data` provides it (paths are relative to a data root). When
neither root has it, these inputs may be
absent. Each is skipped with a warning in `import-report.txt` rather
than throwing. Export steps are isolated: a malformed required input or
unexpected exception fails that step, while later steps/reports still run:

| Missing input | Consequence |
|---|---|
| `global\excel\rareprefix.txt` / `raresuffix.txt` | no `keyed\rare-names.json` |
| `hd\items\items.json` in neither root (or malformed) | no item sprites in `keyed\item-presentation.json`; the mod's mapping is overlaid entry by entry on the vanilla one |
| `hd\items\uniques.json` / `sets.json` | no named variant art; variants use the base item sprite |
| any of `treasureclassex`, `itemratio`, `uniqueitems`, `setitems`, `itemtypes`, `armor`, `weapons`, `misc`, `monstats`, `superuniques`, `levels` `.txt` | no `keyed\drop-calculator.json` |
| any of `monstats`, `monlvl`, `levels`, `monumod` `.txt` | no `keyed\health-calculator.json` |
| any of `weapons`, `skills`, `itemtypes` `.txt` | no `keyed\ias-calculator.json` |
| `global\animdata.d2` **in the mod** | `ias-calculator.json` written with empty `Animations` (CASC timings are never substituted) |
| any of `weapons`, `armor`, `misc`, `itemtypes`, `uniqueitems`, `setitems` `.txt` | no `keyed\loot-filter.json` |
| `global\excel\itemstatcost.txt` | no `keyed\item-stat-presentation.json` |
| a class's HD skill sheet / `skillIconSheets` entry | that class's `sprites\skills\*.webp` |

Each skipped exporter is listed as `Skipped` in `manifest.json` with its
declared outputs under `NotWritten`; any previous copy of its output is
**left untouched** and is not listed in `Files[]`, so the manifest never
vouches for it, and the run is `Succeeded: true, Complete: false`. An exporter
that **throws** (malformed input, invalid config) is recorded as an error and
`Failed`; only the files it attempted to write during this run are removed
(`Removed`), a declared output it never reached is left alone (`NotWritten`),
the remaining exporters, every `extras\` report and the manifest
(`"Succeeded": false`) are still written, and the CLI then exits non-zero.

To exercise these paths end to end, export into a scratch copy of a complete
output that contains hand-placed files in its subfolders:

- **Failed step:** point `--config` at a copy of `Config\` whose
  `monsterHealth.minionBonusModifierId` names a missing `monumod.txt` row. Expect
  the health step `Failed` with `NotWritten: keyed/health-calculator.json` and
  nothing `Removed` (it throws before writing), the previous file unchanged and
  absent from `Files[]`, hand-placed files intact, reports written,
  `Succeeded: false` and a non-zero CLI exit.
- **Skipped steps:** delete optional tables (for example `treasureclassex.txt`,
  `monumod.txt`) from a copied source tree that neither root can backfill, and
  duplicate one `itemtypes.txt` row. Expect `Skipped` steps with their
  `NotWritten` paths, a reported duplicate `Code`, previous outputs unchanged and
  absent from `Files[]`, hand-placed files intact, 0 errors and
  `Succeeded: true`, `Complete: false`.

The deletion path itself — a step that throws *after* writing some outputs — is
covered by `OutputIntegrityTests.InvalidationRemovesOnlyPathsTheProducerAttempted`,
not by an end-to-end run: no bundled exporter can be made to fail mid-write
from configuration alone.

## 3. Regression fixtures

Monster-health checks are in [HEALTH-CALCULATOR.md](HEALTH-CALCULATOR.md#regression-check).
Attack-speed curve checks are in [IAS-CALCULATOR.md](IAS-CALCULATOR.md#regression-check);
run them when changing skill calculation or the IAS bundle.

These fixtures encode bugs that have shipped in the past. Verify each one against
the bundle under inspection (the commands use `.\test-output`; substitute the
Level C `<session>\export` path when reusing it). Mismatches mean the
corresponding fix has regressed.

### 3.1 `descfunc 11` — self-repair / replenish (`rep-dur`, `rep-charges`)

Three uniques exercise the three branches of `PropertyKeyResolver`
case 11:

| Item | Code | Property | Param | Expected `KeyedLine` |
|---|---|---|---|---|
| **Copperbite** | `9bw` | `rep-dur` | `7` | `{ "key": "ModStre9u", "args": [1, 7] }` → "Repairs 1 durability in 7 seconds" |
| **Gangrene Reaper** | `9gi` | `rep-dur` | `15` | `{ "key": "ModStre9u", "args": [1, 15] }` → "Repairs 1 durability in 15 seconds" |
| **Goblin Touch** | `lgl` | `rep-charges` | `10` | `{ "key": "ModStre9u", "args": [1, 10] }` → "Replenishes 1 charge in 10 seconds" |

Quick check:

```powershell
Select-String -Path .\test-output\keyed\uniques.json `
  -Pattern '"Copperbite"|"Gangrene Reaper"|"Goblin Touch"' -Context 0,40
```

Regressed shapes to reject:

- `{ "key": "ModStre9t", "args": [0.07] }` — `/ 100f` rate math, which renders
  as "Repairs 0 durability per second" once floored.
- `{ "key": "ModStre9t", "args": [1, 10] }` — happens when descstr2 is
  empty on the stat row and the fallback uses `templateKey` instead of
  the hardcoded `"ModStre9u"`.
- `{ "key": "ModStre9u", "args": [1] }` — missing the seconds arg; the
  template renders with a stray unsubstituted `%d`.

### 3.2 Skill descriptions — per-level value tables

`keyed/skills.json` must carry a `Descriptions` block per class skill. Fire Bolt is the
canonical check because it exercises every moving part: elemental damage through
`HitShift`, a self-referential `Calc` column, a fractional mana cost, and the synergy list.

```powershell
Select-String -Path .\test-output\keyed\skills.json -Pattern '"Code": "Fire Bolt"' -Context 0,24
```

Expected (levels 1 / 10 / 25 shown; tables are indexed by `level - 1`):

| Line | Key | Level 1 | Level 10 | Level 25 |
|---|---|---|---|---|
| Fire damage | `StrSkill5` | 3-6 | 17-22 | 80-100 |
| Projectiles | `StrSkill120` | 1 | 3 | 5 |
| Mana cost | `StrSkill3` | 2.5 | 2.5 | 2.5 |

Plus three `Synergies` entries: `Sksyn` (`args: ["skillname36"]`), and `Firedplev` for
`skillname47` / `skillname56` with `values: [[14]]`.

Also check the two details save-backed character rendering depends on:

- Each elemental skill carries its source `EType` as `ElementType` (for example,
  Frozen Orb is `cold`) so `item_elemskill` bonuses can be applied without guessing.
- Description tables extend to the absolute `skillDescriptionMaxLevel` cap (100),
  rather than maxlvl plus headroom. Frozen Orb has `Descriptions.MaxLevel: 100`
  and investment cap `MaxLevel: 25`. Every `values2d` hard row covers its remaining
  bonus range through effective 100 before constant-tail trimming.

```powershell
$skills = Get-Content .\test-output\keyed\skills.json -Raw | ConvertFrom-Json
$allSkills = @($skills | ForEach-Object { $_.Tabs | ForEach-Object { $_.Skills } })
$allSkills | Where-Object Code -eq 'Frozen Orb' |
  Select-Object Code, ElementType, MaxLevel, @{Name='DescriptionMaxLevel'; Expression={$_.Descriptions.MaxLevel}}
```

#### Hard points vs. `+skills` (`values2d`)

A line whose formula reads `blvl` must also carry a two-dimensional table indexed
`[hardPoints][bonusLevels]`, because Reimagined pays soft points at a lower rate than
points the character spent. Fire Mastery is the canonical check — `passivecalc1` is
`blvl*par1 + min((lvl-blvl)*par2, par3)`:

```powershell
$skills = Get-Content .\test-output\keyed\skills.json -Raw | ConvertFrom-Json
$all = @($skills | ForEach-Object { $_.Tabs | ForEach-Object { $_.Skills } })
$line = ($all | Where-Object Code -eq 'Fire Mastery').Descriptions.Stats |
  Where-Object key -eq 'StrSkillFireDamagePercent'
$line.values[0][0..4]          # flat table, all hard points: 6,12,18,24,30
$line.values2d[0][0][0..5]     # zero hard points, gear only:  2,2,4,6,8,10
$line.values2d[0][-1][0..5]    # 25 hard points + gear:        150,152,154,156,158,160
```

Failure modes:

- **No `values2d` on Fire Mastery** — `SkillCalcContext` collapsed `lvl` and `blvl` back
  onto one number, or `CalcLevels.UsedBaseLevel` stopped being recorded.
- **`values2d[0][0]` equal to `values[0]`** — the grid was solved with
  `BaseLevel == Level`, which makes it pointless.
- **A large jump in `skills.json` size** — the grid is meant to be emitted only for the
  small minority of `blvl`-sensitive lines, not for every line.

#### Missile damage percentages (`scaleCalc`)

Skills that deal damage through a missile carry that missile's `*SymPerCalc` expression,
because it exists nowhere else on the wire. Source-scoped referenced skill
percentages can also use `scaleCalc` when bare source Params bind safely;
see [SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md#referenced-percentage-contexts).

```powershell
$all | Where-Object Code -eq 'Meteor' | ForEach-Object { $_.Descriptions.Stats } |
  Where-Object scaleCalc | Select-Object key, scale, scaleCalc
```

Expected: `scale` is `elemental` and `scaleCalc` is `skill('Inferno'.blvl)*3`, taken from
the `meteorfire` row of `missiles.txt` (Meteor's `descmissile1`).

Failure modes:

- **Missing expected `scaleCalc`** — inspect the evaluator's observed source reads
  and percentage binding. Do not add regex-based expression discovery as a
  workaround for table aliases or interior-digit missile codes.
- **A percentage borrowed from the wrong skill/missile** — check source aliases and
  source Params. Range-only missile symbols (`m1rn`) do not identify damage;
  newly scaled aura/summon/attack lines can be legitimate corrections, not regressions.

#### Arguments that read another skill's allocation (`valuesCalc`)

Summon Spirit Wolf, Summon Fenris and Summon Grizzly each carry a `SynergyCap` synergy
line with baseline `values` `[[0],[100]]` and
`valuesCalc: ["min(clc6+(skill('Raven'.clc5)-blvl),pa10)", "pa10"]`
(`SkillRegressionTests` covers the same rows deterministically):

```powershell
$all | Where-Object Code -like 'Summon *' | ForEach-Object { $_.Descriptions.Synergies } |
  Where-Object key -eq 'SynergyCap' | Select-Object key, values, valuesCalc
```

A `SynergyCap` without `valuesCalc` means `CalcLevels.UsedUnallocatedSkill` stopped being
recorded; changed `values` mean the baseline evaluation itself changed.

#### Other failure modes to watch for

- **Mana cost `2`** — the `usmc / 256` division was truncated to an integer somewhere; the
  game shows 2.5. `SkillDescriptionImporter` divides in floating point on purpose.
- **`"values"` spread across many lines** — `LevelTableConverter` stopped being applied.
  The tables are very large; indenting them multiplies the file several times over.
- **1 projectile at every level** — `skill('Fire Bolt'.blvl)` inside the skill's own
  `Calc1` resolved to 0 instead of the level being rendered. Self-references are the level;
  other skills' rank terms are 0, but their expression columns are evaluated
  at level 0 and can refer back to the described skill (see
  [SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md#documented-limits-and-the-isolated-preview)).
- **Any `SkillDescription` warning** in `extras\import-report.txt`. That phase's
  expected count is zero, so one is either a regression in the calc evaluator or a
  column the mod has newly left empty — the warning's tail says which.

The importer probes every key it emits, so a skill template the mod never translated shows
up in `extras\missing-translations.txt`. None is expected (see §2.1). A new entry is a
missing row in `data\local\lng\strings`, to be fixed there and not here.

### 3.3 Unique save IDs and named sprite assets

`UniqueExport.FileIndex` must use the engine row index (`EngineRowIndex`): the
UniqueItems.txt row position after excluding the legacy `Expansion` marker. Other
section-header rows (`Armor`, `Rings`, …) and blank lines still consume binary IDs, while the
informational `*ID` column skips them and therefore drifts from the save format. Unique
artwork must be selected by normalized unique name from `hd/items/uniques.json`, not by
array position. Alias, contained-path and fallback rules are specified in
[WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#existing-name-and-asset-contracts);
`ItemPresentationExporterTests` carries the named cases (Griswold's Edge, Head
Hunter's Glory, Blood Raven's Charge, embedded apostrophes, alias collisions,
numbered suffixes and the six `../misc/body_part/fragment_*` mappings).

| Unique | Code | Expected `FileIndex` | Expected sprite |
|---|---|---:|---|
| **Magefist** | `tgl` | `105` | `sprites/items/armor-glove-light_gauntlets.webp` |
| **The Spirit Shroud** | `xui` | `209` | `sprites/items/armor-armor-quilted_armor.webp` |
| **Skin of the Vipermagi** | `xea` | `210` | `sprites/items/armor-armor-leather_armor.webp` |
| **Sorcerer's Cache** | `ci1` | `1015` | `sprites/items/armor-circlet-coronet.webp` |
| **Asheara's Slippers** | `xvb` | `1102` | `sprites/items/armor-boot-heavy_boots.webp` |
| **Duskwreath** | `ulc` | `1137` | `sprites/items/armor-belt-sash_l.webp` |

Quick check:

```powershell
Select-String -Path .\test-output\keyed\item-presentation.json `
  -Pattern '"FileIndex": 105|"FileIndex": 209|"FileIndex": 210|"FileIndex": 1015|"FileIndex": 1102|"FileIndex": 1137' `
  -Context 0,3
```

### 3.4 Set save IDs and miscellaneous set names

Set items use the same parsed-row convention as uniques. `item-presentation.json` must
also attach `SetSprites` to miscellaneous bases such as rings and amulets, not only armor
and weapons.

| Set item | Code | Expected `FileIndex` | Expected sprite |
|---|---|---:|---|
| **Angelic Halo** | `rin` | `52` | `sprites/items/misc-ring-ring.webp` |
| **Kingpin's Signet** | `rin` | `296` | `sprites/items/misc-ring-ring.webp` |
| **Draven Coil** | `rin` | `307` | `sprites/items/misc-ring-ring.webp` |
| **Holy Ring of Amaunator** | `rin` | `384` | `sprites/items/misc-ring-ring.webp` |

Quick check:

```powershell
Select-String -Path .\test-output\keyed\item-presentation.json `
  -Pattern '"NameKey": "Angelic Halo"|"NameKey": "Kingpin''s Signet"|"NameKey": "Draven Coil"|"NameKey": "Holy Ring of Amaunator"' `
  -Context 1,1
```

### 3.5 Adding a new fixture

When a bug is fixed for a specific item, add a row to the matching table in
this section with the item name, code (from `armor.txt` / `weapons.txt` /
`misc.txt`), the relevant property, the parameter value, and the exact
`KeyedLine` shape expected. Prefer an equivalent deterministic test as well.
Future runs of section 3 then prove the fix still holds.

## 4. Deterministic and real-data coverage notes

The Level A/B library suites cover calculation aliases, hard-point-zero and
effective-level boundaries, parameter binding across skill contexts, merged
property qualifiers, mixed-case source directories, unprobed display-key auditing
and write integrity/containment failures. Do not repeat them after a passing
matching run unless code changes, a failure or an unresolved concern requires it.
`WriteSafetyTests` covers interrupted writes, abandoned-temp cleanup recorded in
the manifest, and links planted after path validation.

Set `D2R_MOD_DATA` and `D2R_CASC_DATA` to enable the real-mod checks in
`PropertyConsistencyTests`, `EngineRowIndexTests` and the desktop suite.
`EngineRowIndexTests` only loads the real tables (no export) and checks vanilla-fixed
skill/monster indices, the mod's numeric `reanimate` targets and the §3.3/§3.4 save
IDs. It also compares against the game's compiled tables, so the CASC extraction must
include `global/excel/*.bin`: every `.bin` record count must equal the derived row
count, and each `monstats.bin` record's `Id` text must match the loaded
`MonStatsByIndex` entry at that index. The other real-mod assertions
include Holy Fire/Holy Freeze/FireGolem scaling, Titans Covenant at hard 0 /
bonus 100, Phoenix scope qualifiers and the final display-key audit.

Releases use Level C with `-CheckCli` once (§1); do not add further
content-only exports. Headless tests are not manual GUI testing; portable targets
do not establish verified Linux execution.
