# Property handling and range preservation

This is the current contract for shared property handling and the independent
source-to-final-JSON auditor. Validation levels and commands belong to
[TESTING.md](TESTING.md#211-independent-property-range-audit); wire fields and
consumer limits belong to [WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md).

## Shared pipeline

Production reads typed parser DTOs through `SourceRoots`. Every raw property
column uses `PropertyMapper.MapRows`/`MapExpanded`, including unique/set items,
set bonuses, runewords and gem/rune contributions, cube outputs A/B/C, affixes,
automagic and property-group children. Shared mapping expands shorthand and
groups, resolves property definitions and ItemStatCost descriptions, then
applicable item rows undergo cleanup and equipment calculation. Affixes resolve
at export without item equipment cleanup. Keyed DTOs serialize through
`ExportOutputs`; English display comes from resolving keyed lines.

## Value and scope rules

- Preserve distinct roll endpoints, including zero and negative values. Equal
  endpoints may collapse to a scalar. One existing endpoint is a scalar, never
  a roll against an invented zero; repeating that scalar in a two-component
  template is a rendering requirement, not a second roll. Socket parameter
  fallback applies only when neither endpoint exists; ordinary function-1
  numeric properties have no parameter fallback.
- Distinguish independent damage components, event chance/skill level,
  charges/skill level, selection IDs and per-level growth from random rolls.
  Two source columns alone do not establish a roll. Descfunc 31 retains both
  component slots even when equal; grouped aliases retain all numeric arguments.
- Preserve exact power-of-two-divided growth and both descfunc-19 fallback
  bounds; display growth is never rounded before serialization. Engine rounding
  remains only for descfunc-5 integer scaling, poison total-damage ceiling,
  charged-skill defaults/caps and final equipment arithmetic.
- Sum supported additive contributions before pairing damage components, only
  within matching parameter, suffix, item count, full-set marker, modifier/group
  chance and qualifier. Honor `propertyMergeBlocklist` in all scopes. Keep group
  parents and function 11/12/19 independent encodings separate.
- Retain four roll bounds when damage components vary. Preserve physical maximum
  and paired raw damage internally for equipment arithmetic after clearing
  duplicate tooltips. Poison rates remain raw internally; keyed arguments and
  the established equipment DPS summary each perform their required conversion.
- Convert explicit cold/poison duration values from frames to seconds, retaining
  zero and fractional values. Ranged poison durations stay separate; arbitrary
  duration/rate correlations are outside the established wire contract. These
  duration checks do not prove that cold length appears in the game tooltip.
- Equipment durability sums all low/high contributions and uses
  `strDurabilityRolls`. Elemental equipment ranges use four-slot
  `strElementalDamageRange`. Requirement/equipment and fixed skill-bonus paths
  use the existing endpoint when the other is absent. Requirement reductions
  retain signed crossing-zero rolls and ordered reduction magnitudes.
- Keep identifier selection bounds separate from bonus rolls in optional
  `KeyedLine.selection`, including cloned rune lines. `selection.stat` is
  structural. Automagic deduplication compares the complete serialized line,
  including children, selection and activation metadata.
- Mapping exceptions reach the existing row/step error boundaries; group failures
  include group/code context. Unknown or unsupported semantics are findings,
  never silently successful validation.
- A line whose final enUS template has a substitution slot but no source value
  (for example a function-1 row with only a parameter) is omitted, never shipped
  as a raw `%+d`. Only that line is dropped; the row keeps its other properties
  and the import report names the importer row (or, for export-time affixes,
  the export step), property cells and template.

## Function-17 evaluation

The independent authority is CASC Data Guide `properties.js`, function 17,
`ItemModsSetParam`: use the parameter, otherwise min/max, otherwise zero.
An absent parameter differs from a present zero. An invalid integer parameter
is unsupported, not permission to fall back. One endpoint supplies one value;
equal endpoints are fixed, distinct endpoints retain both values and signs.
Primary function-17 audit records retain the original cells and record
`ValueSource`, `StatValues` and `StatOperation`.

The guide's `set1` distinguishes additive contributions (blank/zero) from
replacement (`1`). Item/runeword contributions sum only within the existing
merge identity and blocklist; parameters take precedence over ignored endpoints.
Replacement values are not summed. Cube modifiers remain separate contributions.
Upgrade and storage arguments describe increments/debits, not accumulated totals.

Presentation uses ItemStatCost description metadata, existing stat overrides and
the actual manifested `strings/enUS.json` template. Match the exact key and
complete argument shape; per-level descriptions require `perLevel:true`, repair
descriptions retain their two-template shape and damage components retain
independent expectations. Missing templates and unmodeled descriptions remain
unsupported. Escaped `%%` is not a substitution slot.

`rep-quant` (`ModStre9v`) and `fade` (`fadeDescription`) have no substitution
slots: validate their key, scope and empty arguments without inventing a rate.
Boolean templates such as `nofreeze`/`half-freeze` need no numeric argument;
ordinary boolean lines may retain unused arguments faithful to source values
and description conversion. This verifies tooltip representation, not hidden
rates or live activation. A function-17 live-stat label (descfunc 3, descval 2,
slotless template, endpoint-sourced, e.g. `lab_kill_display`) may likewise carry
its configured endpoint unused: the engine appends the player's current stat,
which the catalog cannot know. Parameter-sourced values never qualify.

Matching requires the correct source record/container, cube output, item-count,
full-set, qualifier and property-group/chance scope. Another item, equipment
line, argument or unrelated group cannot satisfy an occurrence. Each attached
automagic host must contain its expected line. Identical contributions may
share a legitimate aggregate line; the public JSON has no source-slot identifier.

## Exclusions and evidence limits

The auditor reuses configured ignored properties/items, cube filters, automagic
exclusions, merge blocklist, shorthand expansion and stat remapping. D2R code
membership is case-insensitive; translation keys are exact strings. Hidden
helpers without description templates and parameter-only function-1 duration
rows are intentional non-display inventory, not correctness passes. A duration
parameter alone never establishes a displayed duration.

Automagic host eligibility uses the recorded armor/weapon `AutoPrefix` tables.
Proven hostless definitions are `UnreferencedAutomagicDefinition`; missing tables
cannot establish host absence. A referenced host without its exported bucket
remains an eligibility gap. Standalone definitions, disabled/filtered rows and
`monprop` outside the keyed monster-property contract remain explicit inventory.

Annihilus/Torch cap and cross-counter enchantment expectations use supplied
project context: final upgrades can transform away the counter, and recipe
eligibility may test a different counter from the output modifier. Audit actual
output contributions; do not derive recipe counts from template caps or add
recipe-ID exceptions. No live gameplay verification follows from these checks.

The auditor reads literal TSV cells independently of production parsers using
resolved paths in the current-run source report, and checks actual manifested
JSON, payload integrity, source/config hashes and occurrence scope. Canonical
[source snapshots](../D2RMultiExport.Tests/Fixtures/PropertyRanges/README.md)
exercise published parsers, importers and final JSON with focused seeded lookups.
They complement full-source validation rather than replacing it.

Missing required integrity or invalid cells fail; unsupported functions and
unclassified eligibility remain visible. Exit 1 means defects or invalid run
integrity; exit 2 means unresolved coverage/semantics; exit 0 requires neither.
Omissions never count as numerical passes. Zero unresolved occurrences for one
dataset does not establish universal engine correctness, complete derived
equipment/IAS arithmetic, cube modifier probability or every label/description.

Manifest hashes attest payload integrity; source/config hashes describe
audit-time bytes, not an atomic export-time input snapshot. Keep inputs and code
unchanged within validation, and retain manifest, reports, input revision and
run options together. A successful old manifest cannot establish parity with
subsequently changed source data. Website formatting, optional selection
consumption and browser verification remain separate from exporter checks.
