# Website integration contracts

This document specifies the export contracts the D2R Reimagined website consumes:
exporter behavior and the requirements it places on consumers. Website
implementation status, pending website work and deployment procedures belong in
the website repository. Update this file in the same change whenever a keyed
field is added or reshaped; breaking changes require a coordinated website change.

## Skill data contract

[SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md) is authoritative for the line
members (`values`, `values2d`, `valuesUlvl`, `valuesCalc`, `pluralKey`, `scale`, `scaleCalc`),
their axes, BBE semantics and exporter limits. Skill DTO members are PascalCase;
description-line members are explicitly camel-cased. `Descriptions` carries
`MaxLevel` (effective ceiling), `HardPointCap` (source `maxlvl`) and
`CharacterLevelCap`, with `Stats`, `Details` and `Synergies`. Consumers must:

- Clamp every axis to its last serialized element: constant tails are trimmed,
  and `values2d` bonus rows can have different lengths. The caps are
  configurable; prefer reading them from `Descriptions` over hardcoding them.
- Prefer `valuesUlvl`, then `values2d`, then flat `values` (the extra axes
  currently occur on separate lines). `values` remains present alongside them.
  Do not confuse effective levels with hard points; hard row 0 is meaningful.
- Evaluate `scale`/`scaleCalc` damage percentages against live effective and
  hard rank maps; the exporter does not invent a synergy allocation. In
  `scaleCalc`, bound source Params are already source values and quoted synergy
  references remain live.
- Optional `valuesCalc` (present only on lines whose numbers read another skill's
  allocation, such as the Druid summons' `SynergyCap`) holds one source calc cell per
  numeric argument, aligned with `values`. Evaluate each non-null entry in the
  describing skill's own scope against the live effective and hard rank maps, exactly
  as `Calculation.ElementalDamage`, and use the result for that argument. A `null`
  entry, an absent member or an expression the evaluator cannot resolve keeps the
  exported tables, which remain the no-other-points baseline.
- Treat `scaleCalc` and `valuesCalc` as exporter BBE grammar. An evaluator that supports only the
  currently emitted subset must be re-checked when new `miss`, `sklvl`, `sksrc`,
  `cond`, `%`, `^` or live-state uses appear.

Description difficulty is fixed by `skillDescriptionDifficulty` (default Hell),
including Raise Skeleton/Mage minion damage. There is no exported difficulty axis
for skills; a future selector needs an explicit contract change.

## Names, properties and assets

### Property roll preservation

Property `key`/`args` remain the display contract. A genuine roll uses two
arguments when endpoints differ; equal endpoints may use one argument. Damage
minimum and maximum are independent components and keep two slots even when
equal. Paired components that each roll use four slots in this order:
`[minimumLow, minimumHigh, maximumLow, maximumHigh]`. The localized templates
`strFireDamageRolls`, `strColdDamageRolls`, `strLightningDamageRolls` and
`strPoisonDamageRolls` preserve this distinction; poison adds duration in seconds
as a fifth argument. `strElementalDamageRange` retains the same four-slot shape
in equipment summaries. `strDurabilityRolls` carries current and maximum
durability bounds as four arguments; `strDurabilityBonus` displays flat
durability contributions on affixes. These are finalized values, not consumer
calculation inputs. Cold/poison duration lines use native `StrSkill13`/`StrSkill20`
with frame values divided by 25, preserving fractional seconds.

An optional `selection` member preserves function-12 identifier pools and
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

Refresh `strings/<lang>.json` together with keyed JSON: templates and argument
shapes must match. Finalized fractional arguments are exact (for example
Headstriker's per-level growth `0.375`); any display rounding is a consumer
presentation decision. See [PROPERTY-RANGES.md](PROPERTY-RANGES.md) for the
property semantics, audit coverage and fixtures.

### Existing name and asset contracts

- Monster-only item-stat skills and untranslated non-combat health rows omit
  `NameKey`, retaining raw `FallbackName`. Do not resolve a missing field as a
  translation or put identifiers into mandatory `NameKey` fields.
- `AutoMagicGroups[].Code` retains the `automagic.txt` row name; `NameKey` appears
  only for names resolvable in mod/synthetic/CASC enUS strings. Existing internal
  names such as `Bow 1`/`of Cinders` are identifiers, not missing translations.
  A consumer displaying an unlabeled bucket derives its label from the bucket's
  translated property lines.
- All property sources apply `propertyCodeExpansions` through shared mapping.
  `dmg-elem` consistently yields fire/cold/lightning range lines while preserving
  values, requirements/chances and qualifiers. No consumer rewrite is required.
- Merged runeword contributions retain `qualifier` (lowercase): Phoenix mana
  leech is weapon-only and maximum fire resistance shield-only; Desire retains
  weapon/armor/shield scopes.
- One source property can emit several lines: a multi-stat definition renders
  one line per displayable stat unless its stats share a complete display group
  (for example `stats%` → `ModAllStatsPercent`, `abs-all%` → four absorb lines).
  The lines are ordered by display priority and carry the same scope metadata.
- Property groups keep a parent with structural `code`, keyed `nameKey`,
  `pickMode` and `children[].chance`. Weights are relative for mode `"2"`,
  percentages otherwise; do not flatten these into independent properties.
  As for `dmg-elem` and cold duration, a sub-property emitting several lines
  yields sibling children carrying the same `chance`.
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
  output; stale files cannot satisfy completeness. Sprite and icon paths are
  relative to the bundle root; deploy the referenced WebP files with the JSON.

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
Do not emit ids beginning `#` or `restored-`: the website reserves them for its
own interim and restored identifiers.
Identity survives row moves/output/balance changes, but changes to matching
inputs/op create a new identity. A parser change stripping/requoting raw cells
can rotate ids; treat that as a coordinated breaking migration (the website
owns the alias remapping of saved links).

## Calculator bundles

The drop, IAS, monster-health and loot-filter documents are specified in their
own contracts: [DROP-CALCULATOR.md](DROP-CALCULATOR.md) (including `Condition`,
`ConditionDifficulties` and `ConditionRuntime`),
[IAS-CALCULATOR.md](IAS-CALCULATOR.md) (including the two-stage diminishing
curve truncation), [HEALTH-CALCULATOR.md](HEALTH-CALCULATOR.md) and
[LOOT-FILTER.md](LOOT-FILTER.md).

## Manifest schema 2 and output integrity

The output directory may be the website's `static/data` itself; there is no
separate installation step. By default the exporter overwrites only its generated
files. Skipped/stale/unrelated output files remain on disk, and only current-run
files appear in `Files[]`. A failed
step removes only its attempted paths (`Removed`); untouched declared outputs
are `NotWritten`, not current. The previous manifest is removed **at the start
of the output phase**, and the new one is written last. A fatal failure before
that phase leaves the previous bundle/manifest untouched; timestamps/hashes are
not proof that an attempted later run succeeded or used particular source commits.

A destructive export (`--destructive` / desktop checkbox) generates the run in a
hidden working directory inside the output directory and, only when it recorded no
errors, replaces every previous entry with it, so the directory then holds exactly
the manifest and its `Files[]`. Otherwise the previous contents, including their
manifest, are kept or restored. Skipped optional outputs are then absent rather
than stale ([README](../README.md#export-modes)).

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

Schema 1's error-only `Complete` corresponds to schema 2's `Succeeded`; schema 2
additionally distinguishes incomplete successful runs. No browser schema handling
is required for this export mechanism.

Hashes detect changes, truncation and mixed payloads **when checked**, but do not
authenticate an unsigned manifest, attest source/code versions, verify game
formulas or establish current-run freshness from directory contents. Atomic
file writes do not make an adversarial filesystem namespace snapshot.
Preserve export-side hashing. Do not make browser verification, additional
requests or cache-busting depend on the manifest without a separate justified
website decision. See [SECURITY.md](../SECURITY.md).
