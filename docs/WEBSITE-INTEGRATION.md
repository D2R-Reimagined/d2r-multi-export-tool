# Website integration status and contracts

Website adoption below was established by read-only inspection of the sibling
`d2r-reimagined-website` source (commit `064754b`) and direct calls into its
calculation functions. It is not deployed-site or browser-rendering verification;
re-check the website source before relying on an entry.

## Remaining website work

- [ ] Consume `ConditionDifficulties`/`ConditionRuntime` in both item and full-loot
  drop calculators. Keep runtime/unknown conditions unavailable when they can
  affect eligibility or capacity; do not assume false or zero. Add condition
  regression cases (including nonliteral difficulty-only expressions).
- [ ] If automagic buckets are displayed, model structural `Code` and optional
  `NameKey`; derive an unlabeled bucket's display from translated property lines.
  Current website source has no `AutoMagicGroups` consumer. This is a display
  capability gap, not a failing mandatory translation.
- [ ] Make data refresh use the exporter's `publish` staging command and deploy
  a coherent bundle from staging. No export-manifest ingest job was found.
  Do not add browser hash checks, cache requests or a browser manifest fetch.
- [ ] Refresh data from the intended exporter/source commits and run the site's
  existing focused skill/IAS/item-stat/recipe tests plus visual checks. A bundle
  whose hashes validate is not thereby the latest data.

Optional future compatibility improvement: `skill-points.ts` still hardcodes
effective cap 100; use `Descriptions.MaxLevel` if different configured caps are
supported. Current exported cap is 100, so this is not a current numerical defect.

## Verified adoption

| Contract | Current website evidence | Status |
|---|---|---|
| `values2d`, hard/bonus ranks and row 0 | `src/lib/skill-points.ts` chooses the row then clamps bonus; `SkillDetails.svelte` and save-backed skill view pass both rank maps | Implemented; current importer + site function gives Titans Covenant 0+100 = 55%. |
| `valuesUlvl` / character level | `skillLineNumbers` prefers this grid; `CharacterViewer.svelte` forwards save level; `SkillDetails.svelte` offers level input | Implemented; Thorns skill 1 gives 5 at character 1 and 10 at character 100. |
| `scaleCalc` / source missile percentages | `skillLineSynergyBonus` prefers line expression over skill percentage; `SkillDetails.svelte` applies it | Implemented; every scaled line with a non-empty percentage evaluated without an unsupported-expression error when reviewed. |
| Cross-skill calculation references | `skill-calculation.ts` resolves quoted `skill` Params/Calcs in the target row, using separate effective/hard maps | Implemented for the reviewed expressions. The complete exporter BBE grammar is broader. |
| Optional `NameKey` / `FallbackName` | `item-stat-presentation.ts` uses a name helper and optional chaining; health page uses translated key or fallback | Implemented in inspected consumers. |
| Structural automagic `Code` | No `AutoMagicGroups` reader/type found in `src` | No consumer; checklist above applies if buckets are displayed. |
| Stable cube `Id` | `cube-recipe-ids.ts`, build links, alias data and `data:remap-recipes` script | Implemented; refresh/migration remains an operational step. |
| Corrected IAS curves | `ias-calculator.ts` consumes exported curves; `ias-calculator.spec.ts` anchors match current values | Implemented; direct formula comparison matches every level of the four diminishing buffs. |
| `ConditionDifficulties`, `ConditionRuntime` | Neither field occurs in drop consumer types/evaluators | Pending. Raw literal Hell condition is recognized; runtime conditions retain limited support. |
| Sprite / icon references | Item presentation readers and `SkillDetails.svelte`/skill nodes use `/data/` plus emitted relative paths | Implemented in source; deploy referenced WebP files with JSON. Visual appearance not checked. |
| Export manifest / staging | No reader of export `manifest.json` or publish-stage job in website scripts/source | Operational integration pending; separate launcher/admin hash fields are unrelated. |

The inspected skill code is covered by the website's own `skill-points.spec.ts` /
`skill-calculation.spec.ts`. The website test runner was not launched for this
review; the direct checks loaded its TypeScript calculation functions against
imported descriptions and skill Calculation metadata.

## Skill data contract

See [SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md) for BBE semantics and exporter
limits. Skill DTO members are PascalCase; description-line members are explicitly
camel-cased. `Descriptions` carries `MaxLevel` (effective ceiling, configured
100), `HardPointCap` (source `maxlvl`, currently 25) and `CharacterLevelCap`
(configured 100), with `Stats`, `Details`, `Synergies`.

| Line member | Shape and selection |
|---|---|
| `key`, `args`, `pluralKey` | Keyed-line translation contract; plural chosen when the rendered value is not exactly 1. |
| `values` | Per numeric argument, `[skillLevel - 1]`; remains present with additional axes. |
| `values2d` | Per argument, `[hardPoints][bonusLevels]`, only when evaluation reads `blvl`. Hard row 0 is meaningful. Rows cover bonuses `0..(MaxLevel-hardPoints)` before trimming. |
| `valuesUlvl` | Per argument, `[skillLevel - 1][characterLevel - 1]`, only when evaluation reads `ulvl`; flat values use maximum character level. |
| `scale` | `physical`/`elemental`/`elementalLength`, selects skill `Calculation` percentage. |
| `scaleCalc` | Overrides the skill percentage with the actual missile/referenced-row expression. Bound source Params remain source values; quoted synergy references remain live. |

Clamp every axis to its last serialized element: constant tails are trimmed,
and bonus rows can have different lengths. Prefer `valuesUlvl`, then `values2d`,
then flat `values` in the currently separate axis cases. Do not confuse effective
levels with hard points. The website must evaluate damage percentages against
live rank maps; the exporter does not invent a synergy allocation.

Description difficulty is fixed by `skillDescriptionDifficulty` (default Hell),
including Raise Skeleton/Mage minion damage. There is no exported difficulty axis
for skills. A future selector needs an explicit contract change.

Hard row 0 extends through the full bonus range, damage scaling follows
calculation aliases, missile lines carry the missile's own percentage and
referenced-skill percentages bind that row's own Params (for example, Summon
Tainted's projectile uses Tainted Fire Ball's Param, not the summoner's). Healing
lines do not borrow a damage percentage. Referenced percentages that depend on
bare non-Param quantities are a bounded exporter limitation, documented with
its diagnostic gap in SKILL-CALCULATIONS.

## Names, properties and assets

### Property roll preservation

Property `key`/`args` remain the display contract. A genuine roll uses two
arguments when endpoints differ; equal endpoints may use one argument. Damage
minimum and maximum are independent components and keep two slots even when
equal. Paired components that each roll use four slots in this order:
`[minimumLow, minimumHigh, maximumLow, maximumHigh]`. New localized templates
`strFireDamageRolls`, `strColdDamageRolls`, `strLightningDamageRolls` and
`strPoisonDamageRolls` preserve this distinction; poison adds duration in seconds
as a fifth argument. `strElementalDamageRange` retains the same four-slot shape
in equipment summaries. `strDurabilityRolls` carries current and maximum
durability bounds as four arguments; `strDurabilityBonus` displays flat
durability contributions on affixes. These are finalized values, not consumer
calculation inputs. Cold/poison duration lines use native `StrSkill13`/`StrSkill20`
with frame values divided by 25, preserving fractional seconds.

An optional `selection` member now preserves function-12 identifier pools and
grouped skill-tab `ParMin`/`ParMax` bounds independently of the displayed bonus:

```json
{
    "key": "strSkillRandomClass",
    "args": [2, "Sorceress"],
    "selection": { "stat": "item_singleskill", "min": 36, "max": 65 }
}
```

`selection.stat` is a structural engine identifier. `min`/`max` are original
skill/class/tab identifiers, not roll endpoints of the +2 bonus; absent bounds
are omitted. Existing key/args readers can ignore this optional member. It is
needed because the class label alone cannot distinguish narrowed selection
pools. Group aliases retain both selection bounds and modifier rolls.

Website source inspection found support for ordinary argument ranges and flat
multi-slot templates; refresh strings together with keyed JSON. Its current
types/readers ignore `selection`, so identifier-pool presentation is not yet
adopted. No website changes, browser verification or website test run are claimed.
`src/utilities/format-template.ts` currently formats fractions through
`Number(value.toFixed(1))`: Headstriker's exported growth `0.375` still renders
as `0.4`. This is a reproducible website presentation precision gap; the
exporter retains `0.375`. See [PROPERTY-RANGES.md](PROPERTY-RANGES.md) for audit
coverage, fixtures and remaining unresolved mappings.

### Existing name and asset contracts

- Monster-only item-stat skills and untranslated non-combat health rows omit
  `NameKey`, retaining raw `FallbackName`. Do not resolve a missing field as a
  translation or put identifiers into mandatory `NameKey` fields.
- `AutoMagicGroups[].Code` retains the `automagic.txt` row name; `NameKey` appears
  only for names resolvable in mod/synthetic/CASC enUS strings. Existing internal
  names such as `Bow 1`/`of Cinders` are identifiers, not missing translations.
- All property sources apply `propertyCodeExpansions` through shared mapping.
  `dmg-elem` consistently yields fire/cold/lightning range lines while preserving
  values, requirements/chances and qualifiers. No consumer rewrite is required.
- Merged runeword contributions retain `qualifier` (lowercase): Phoenix mana
  leech is weapon-only and maximum fire resistance shield-only; Desire retains
  weapon/armor/shield scopes. The website already has qualifier handling in
  `i18n.ts`; refresh data to pick up the corrections.
- Property groups keep a parent with structural `code`, keyed `nameKey`,
  `pickMode` and `children[].chance`. Weights are relative for mode `"2"`,
  percentages otherwise; do not flatten these into independent properties.
- Asset maps layer CASC then mod entry by entry, preserving last-wins duplicate
  resolution. Existing normalized name matches precede compatibility aliases;
  aliases remove apostrophes and the separator before a trailing numeric suffix,
  preserving word separators and suffix values. Ambiguous aliases are rejected;
  unique/set artwork never uses manifest array positions.
- Category-relative artwork paths are canonicalized within `hd/global/ui/items`.
  Contained references such as `../misc/body_part/fragment_cold` reuse the same
  cache entry and output name as `misc/body_part/fragment_cold`; absolute,
  malformed and escaping paths are rejected. General source-root rules do not
  change.
- A named variant without available art uses base art and is diagnosed in
  `extras/asset-report.txt`, with the requested mapping, failure reason and chosen
  fallback. Explicit shared artwork is not a lookup failure. Only successfully
  written icons are emitted. Every referenced `Sprite`/`Icon` must be a current-run
  output; stale files cannot satisfy completeness. No item JSON fields change.
- Recorded complete-CASC baseline: 547 item sprite files (base and variant art),
  240 skill icons, one
  unresolved `scz` mapping (`misc/scepter/scepter`). The available local mod
  checkout still has that old mapping: this is a source checkout mismatch, not
  an exporter path defect. The corrected mod checkout was not supplied here.

## Stable cube recipe identity

Each cube recipe has unique `Id` plus unchanged row-order `Index`. Keep this
algorithm stable; saved build links depend on it:

1. Take raw typed `cubemain.txt` cells in this order: `class`, `op`, `param`,
   `value`, `input 1` through `input 7`. Trim each; retain empty strings,
   original case and quotes.
2. Join with U+001F and hash UTF-8 bytes using FNV-1a 64-bit (offset
   `0xcbf29ce484222325`, prime `0x100000001b3`, wrap unsigned arithmetic).
3. Emit `cr-` + 16 lowercase hexadecimal digits. First collision keeps it;
   later rows receive `-2`, `-3`, etc. in file order, with every group reported.

Test vectors: FNV-1a of `a` = `af63dc4c8601ec8c`; empty class/op/param/value,
inputs `amu`, `ka3`, then five empty cells = `cr-8a68ef961baebb59`.
Do not emit ids beginning `#` or `restored-`: those are website-reserved.
Identity survives row moves/output/balance changes, but changes to matching
inputs/op create a new identity. A parser change stripping/requoting raw cells
can rotate ids; treat that as a coordinated breaking migration.

`cube-recipe-ids.ts` uses exporter Id, otherwise interim `#<Index>`, with legacy
aliases from `src/lib/data/cube-recipe-aliases.json`. The site already has
`pnpm run data:restore-removed` and `pnpm run data:remap-recipes`; its maintainer
should review/run the applicable migration steps after a data refresh, preserving
the previous snapshot needed for alias remapping. These scripts are not exporter
features and were not run during this read-only review.

## Drop condition contract

Named `Items[]` and `TreasureClasses[]` retain raw `Condition`, including source
quotes. Nonempty difficulty-only expressions gain `ConditionDifficulties` (int
indexes 0/1/2); expressions reading live state gain `ConditionRuntime: true`.
Both are omitted for empty conditions. For example, Desecrated TCs resolve to
`[2]` and herald TCs require runtime state. The exporter checks syntax/referenced
rows and solves difficulty-only expressions through shared BBE.

The site still recognizes literal `cond('Difficulty', hell)` in both
`drop-calculator.ts` and `monster-drop-calculator.ts`. Item drop probability may
compare conditional-gate extremes and refuse affected results; full-loot mode
refuses unsupported conditions. Preserve that caution while adopting resolved
metadata; live state cannot be recovered from this static bundle.

## IAS contract and verification

All four diminishing curves use integer truncation in **two** stages, with no
implicit max clamp:

```text
pct = (110 * lvl) / (lvl + 6)
value = min + ((max - min) * pct) / 100
```

| Buff (min/max) | Level 1 | Level 20 | Level 60 | Level 100 |
|---|---:|---:|---:|---:|
| Fanaticism (10/40) | 14 | 35 | 40 | 40 |
| Frenzy (0/50) | 7 | 42 | 50 | 51 |
| Wearwolf (10/80, source spelling) | 20 | 68 | 80 | 82 |
| Quickness (15/60) | 21 | 52 | 60 | 61 |

Fanaticism level 3 is **20** under this formula (the single combined division
gives 21). `Maul.Values: null` is an intentional empty buff override.
See [IAS-CALCULATOR.md](IAS-CALCULATOR.md) for the reproducible local check.

## Manifest schema 2 and publishing

The exporter overwrites only its generated files. Skipped/stale/unrelated output
files remain on disk, and only current-run files appear in `Files[]`. A failed
step removes only its attempted paths (`Removed`); untouched declared outputs
are `NotWritten`, not current. The previous manifest is removed **at the start
of the output phase**, and the new one is written last. A fatal failure before
that phase leaves the previous bundle/manifest untouched; timestamps/hashes are
not proof that an attempted later run succeeded or used particular source commits.

| Manifest field | Meaning |
|---|---|
| `SchemaVersion` | `2`; breaking schema changes require a bump. |
| `Succeeded` | False if any error was recorded. |
| `Complete` | Succeeded and every step Written, with no declared output missing. Optional skips make it false. |
| `Steps[]` | `{ Producer, Status, Outputs, NotWritten?, Removed?, Message? }`; `Written`/`Skipped`/`Failed`, including each extras report. |
| `Files[]` | `{ Path, Bytes, Sha256, Producer }`; root-relative `/` paths, ordinal order, lowercase SHA-256, current-run payloads only. Manifest does not list itself. |
| `AbandonedTempFilesRemoved` | Count of same-output abandoned temps removed by the next write. |

Each `Files[].Producer` matches its owning `Steps[].Producer`. Diagnostic reports
use their relative path (for example, `extras/import-report.txt`) as both identifiers.

Schema 1's error-only `Complete` is now `Succeeded`; schema 2 distinguishes
incomplete successful runs. No browser schema handling is required for this
export/publishing mechanism.

```powershell
dotnet run --project .\D2RMultiExport.Console -- publish `
  --from .\test-output --staging <new-or-empty-directory>
```

`PublishStager` validates schema, success/completeness and every copied file's
SHA-256/length against captured manifest bytes, then writes that manifest last.
It refuses nonempty staging and output/staging nesting in either direction;
physical roots/parents are checked on Windows/Linux. `--allow-incomplete` is an
explicit operational choice for skipped optional outputs, never permission to
publish a failed run. Copy/deploy staging as a coherent bundle; how the website
replaces its owned static data is a separate deployment decision.

Hashes detect changes, truncation and mixed payloads **when checked**, but do not
authenticate an unsigned manifest, attest source/code versions, verify game
formulas or establish current-run freshness from directory contents. Atomic
file writes/staging do not make an adversarial filesystem namespace snapshot.
Preserve export-side hashing. Do not make browser verification, additional
requests or cache-busting depend on the manifest without a separate justified
website decision. See [SECURITY.md](../SECURITY.md).
