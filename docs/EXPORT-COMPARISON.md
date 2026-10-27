# Export snapshot comparison

The `compare` CLI command compares supported categories in two existing export
directories and writes a versioned JSON change report. It does not run an export,
alter either input, or infer changes from files that their manifests do not vouch
for.

```powershell
dotnet run --project .\D2RMultiExport.Console -- compare `
  --old "<old-export-directory>" `
  --new "<new-export-directory>" `
  --out "<comparison-output-directory>"
```

The comparison output may not be either input directory, a directory inside an
input, or a symlink/junction alias into an input. It contains `changes.json`,
`recipe-changes.md`, `item-changes.md`, `item-patch-notes.md`, and
`recipe-patch-notes.md`;
`--pretty` is available and defaults to true. No developer test paths are built
into the application.

## Recipe change Markdown

The same `compare` invocation also writes `recipe-changes.md` beside
`changes.json`. It is a human-readable view of the existing `cube-recipes`
category in the comparison report; it does not run a second comparison or alter
the schema-2 JSON. The summary identifies both snapshots and their manifest
hashes, reports validation and category status, counts, and diagnostics. Added,
removed, and modified entries include recipe IDs, row indexes, readable recipe
details, changed fields where applicable, and the complete original record JSON.
Unchanged recipes contribute to the summary count but are not repeated as
individual records.

English item and property text is used only when that snapshot's
`strings/enUS.json` is listed as a current-run output of its `strings` manifest
step, its producer matches, its size and SHA-256 pass the same snapshot checks,
and the manifest hash still matches the one captured by comparison. The old
snapshot's bundle is used for old records and the new snapshot's bundle for new
records. Missing, stale, malformed, or unverified bundles and unresolved or
unsupported keyed-line templates fall back to the original translation key and
raw arguments; no English is invented. Keyed-line values are formatted only for
the small supported placeholder subset, not through a recreation of the
website's full localization renderer. The complete raw record remains
available in a collapsible Markdown block in every listed entry.

Recipe `Id` values are derived from ingredients and conditions. An ingredient or
condition change can therefore become a removal plus an addition rather than a
modification. The Markdown does not guess replacement pairs; compare the stable
IDs and full records in both reports during review.

If either snapshot fails validation or the cube category is invalid or
unavailable, the Markdown explicitly says recipes were not successfully
compared and does not present empty change sections as zero changes. `changes.json`
and the existing comparison exit-code behavior remain authoritative; a fatal
failure while rendering or writing either report follows the existing fatal
comparison path.

## Item change Markdown

The same `compare` invocation writes `item-changes.md` beside the other reports.
It presents `uniques`, `sets`, `runewords`, `armors`, `weapons`, `magicprefix`,
and `magicsuffix` directly from the schema-2 report; it does not perform a second
comparison or infer record matches. Added, removed, and modified records include
their report identity, available variants and match metadata, readable fields,
and complete original JSON in collapsible sections. Modified keyed properties
are shown as localized before/after lines with their original numeric arguments;
set-piece and automagic-group differences use the report's explicit nested
changes. Automagic groups are possible associations, not guaranteed rolls.

The summary provides snapshot and translation validation information and counts
for every category. A `NotCompared` or missing category is explicitly unavailable
and is not represented as zero changes. Affix ambiguity warnings and unresolved
candidate identities remain visible as comparator-reported removals/additions;
the Markdown does not present them as confirmed modifications. A stable identity
change remains a separate removal and addition. As with recipe Markdown, English
text is used only from manifest-verified snapshot translations, and unresolved or
unsupported keys fall back to their original key and arguments.

## Player-facing item patch notes

The same `compare` invocation also writes `item-patch-notes.md` beside the
comparison reports. It is generated from the existing comparison report and
manifest-verified English strings; it does not run a second comparison or
change matching, `changes.json`, `recipe-changes.md`, or `item-changes.md`.

The notes start with added, removed, modified, and unchanged counts, followed by
collapsible Uniques, Sets, Runewords, Base armor, Base weapons, Magic prefixes,
and Magic suffixes. Each category summary shows its change counts. Within Uniques,
each affected equipment type is collapsible and shows its total added, removed,
and modified item counts; ordinary Added, Removed, and Modified headings contain
the individual items, with available Normal, Exceptional, or Elite tier details.
Each affected set has one collapsible section containing its changed partial and
full set bonuses and only its added, removed, or modified pieces. Other categories
retain independently collapsible change sections. Added and removed records
include readable identifying metadata and all available properties. Modified
records show only changed metadata and property lines.
Exact property lines are removed as a multiset; remaining lines
are paired only when their key, non-argument metadata, and ordered nonnumeric
arguments identify a unique line on both sides. Numeric arguments can then be
shown as `new (was old)`. A supported template with one numeric placeholder and
one surplus numeric endpoint renders those endpoints as a range (with equal
endpoints reduced to one value); the remaining arguments still use the original
template. A changed skill or trigger remains an addition and removal, and
ambiguous lines are not guessed. Empty property-array entries are omitted, and
readable base damage lines are not repeated as `DamageTypes` metadata when those
lines also changed.
Set-piece and shared-bonus changes remain distinct within each affected set,
runeword rune order is retained, and automagic groups are described as possible
associations, not guaranteed rolls. Unavailable categories are labeled unavailable, and
untranslated values retain their original key and arguments for review.

## Player-facing recipe patch notes

The same `compare` invocation writes `recipe-patch-notes.md` from the existing
`cube-recipes` comparison and each snapshot's manifest-verified English strings.
It does not run another comparison or change `changes.json`, recipe identity,
or any of the existing reports. The visible summary table keeps counts by the
first translated value in each record's `Notes` array; secondary notes remain
visible in the expanded recipe. Records without notes, or whose primary note
cannot be translated, appear under `Other recipes`. Modified recipes use the new
record's notes for placement; removed recipes use the old record's notes.

Each note category has one Wiki.js-compatible `<details>` section. Within it,
each non-empty change type has a nested `<details>` section whose summary retains
the existing label and record count: `Confirmed matched modifications`, `Added
recipe definitions`, or `Removed recipe definitions`. Individual recipe headings
and their properties remain ordinary expanded Markdown, not collapsible
sections. The summary table and category headings retain their counts; snapshot
paths, manifest hashes, recipe IDs, row indexes, and routine comparison
diagnostics are not shown. Each recipe still has an HTML comment linking its
record to `changes.json` for traceability. Within `Item Enchantment Recipe`,
affected definitions remain grouped in collapsible sections by the first
ingredient's translated keyed item type; each equipment type contains its own
non-empty change-type sections using the same summaries and counts.
Unavailable recipe comparisons remain explicitly labeled unavailable instead of
being represented as zero changes.

Instruction-style headings show ingredients and results without `1 ×` prefixes;
larger quantities remain visible. Translated quality and socket restrictions are
attached to the relevant item, and single-output headings omit internal output
slot labels. A recipe that returns the exact input is described as `Original …`,
while one that generates an item of the same base type is described as `New …
(same base type)`; these behaviors are not conflated. When a recipe has a
separate description heading, its ingredient and result instructions remain in
the details. Modified recipes use natural `Changed`, `Added`, and `Removed`
property labels, and do not repeat a complete ingredient list below an
instruction-style heading.

Unverified operation or property-selection conditions are not explained or
rendered as raw values. A confirmed modification limited to unsupported
conditions is described neutrally as a changed recipe condition, with the full
technical values retained in the detailed comparison. Identical visible recipe
headings that may differ by technical conditions carry the same neutral caveat.
Unmatched definitions retain the identity/restructuring caveat and are not
assumed to be genuine deletions and additions. Untranslated or unsupported
properties use one concise fallback pointing to the detailed comparison;
`changes.json` and `recipe-changes.md` retain their complete technical details.

## Snapshot validation

Each input must contain a readable schema-2 `manifest.json`. For each category,
the comparison reads its file only when the manifest records `Succeeded: true`,
the `keyed-core` step is `Written` and lists the file as an output, and the
matching `Files[]` entry names the same producer. Recorded byte length and SHA-256
are checked when available.

The compared files are:

| Category | File | Record identity |
|---|---|---|
| `cube-recipes` | `keyed/cube-recipes.json` | Stable `Id` |
| `uniques` | `keyed/uniques.json` | String `Index` |
| `sets` | `keyed/sets.json` | String `Index`; nested `SetItems` use their string `Index` within each set |
| `runewords` | `keyed/runewords.json` | String `Index`; same-name variants use the sorted `Types[].Index` identifiers |
| `armors` | `keyed/armors.json` | String `NameKey`; nested `AutoMagicGroups` use `Code` within each base item |
| `weapons` | `keyed/weapons.json` | String `NameKey`; nested `AutoMagicGroups` use `Code` within each base item |
| `magicprefix` | `keyed/magicprefix.json` | `NameKey`; duplicate names use structural metadata and keyed property signatures |
| `magicsuffix` | `keyed/magicsuffix.json` | `NameKey`; duplicate names use structural metadata and keyed property signatures |

A missing manifest, failed export, invalid manifest, or unsafe snapshot prevents
all categories from being compared. A missing, skipped/not-written, unlisted,
unreadable, malformed, or integrity-mismatched category file prevents only that
category from being compared when the snapshot manifest itself is valid. A
physical file left by an earlier run is never treated as current data or proof of
a deletion. When the manifest has `Complete: false`, the report warns; a category
can still be compared if its file independently passes current-run and integrity
checks. Missing integrity metadata is also reported as a warning.

The report records each snapshot's absolute path, manifest schema version,
success/completeness flags, manifest SHA-256 and validation diagnostics. Category
diagnostics identify their `Snapshot` side (`old` or `new`) when applicable. The
manifest is unsigned: its hash detects later payload mismatch but does not
authenticate the manifest or prove source/code provenance.

## Report schema version 2

`changes.json` uses PascalCase property names and contains:

- `SchemaVersion`: `2` for this comparison-report contract. Version 2 generalizes
  recipe-specific record fields to `RecordEntry`/`ModifiedRecord`-style fields and
  adds set-piece changes; consumers of schema 1 must update.
- `Succeeded`: false if snapshot validation or any category comparison has errors.
- `OldSnapshot` and `NewSnapshot`: snapshot identification, manifest integrity
  metadata, `Errors` and `Warnings`.
- `Categories`: one entry each for `cube-recipes`, `uniques`, `sets`, `runewords`,
  `armors`, `weapons`, `magicprefix`, and `magicsuffix`, in that order. Each has
  `File`, `Status`, `MatchKey`, `IgnoredFields`, verified file hashes, diagnostics, `Counts`, and
  `AddedRecords`, `RemovedRecords`, and `ModifiedRecords` arrays.
- `Counts`: aggregate added/removed/modified/unchanged records and `Reindexed`
  count across categories whose status is `Compared`. Set-piece counts are kept
  separate in that category's `NestedCounts` and do not double-count set records;
  automagic-group counts are likewise separate and do not double-count equipment
  records.

An added or removed record contains `Key`, optional `RowIndex`, and its complete
original `Record` JSON. A modified record contains the identity `Key`, applicable
old/new row indexes, sorted `ChangedFields`, `Changes` (`Path`, `Kind`, `OldValue`,
`NewValue`), and complete `Old`/`New` record JSON. The cube category uses the
numeric `Index` only as `RowIndex`; it is not an identity. Index-only shifts are
excluded from gameplay field changes and counted by `Reindexed`.
Affix modifications also include optional `MatchedBy`, identifying the matching
pass; it is omitted for all other categories.

For sets, `NestedField` is `SetItems`, `NestedMatchKey` is `Index`, and
`NestedCounts` counts child records only within sets matched by the parent set
identity. A changed set's optional `NestedChanges` identifies the field and key,
and includes full added/removed piece records plus modified pieces with field
changes and complete before/after JSON. Parent set additions/removals already
contain all pieces in their full `Record`, so those pieces are not repeated in
`NestedCounts`.

For runewords, `MatchKey` is `Index`, while `VariantField` is `Types` and
`VariantMatchKey` is `Index`. A name occurring once in each snapshot is matched
by `Index`, so changes to its rune sequence or eligible types are modifications.
When a name is duplicated on either side, variants are matched by the name plus
the ordinal-sorted `Types[].Index` set. `Types` ordering is non-semantic, while
`Runes` ordering is meaningful. Added, removed, and modified runeword records
include `VariantTypes` when the name is a variant group; their `Key` includes the
name and type identifiers, and complete original record JSON remains in
`Record`, or in `Old`/`New` for modifications.

For `armors` and `weapons`, `NameKey` is the base-item identity. `NormCode`,
`UberCode`, and `UltraCode` are tier relationships and are compared as ordinary
metadata, never used to match records. Equipment fields, damage data, and
`Lines` are compared structurally; ordering in ordinary arrays such as property
and damage lines remains meaningful. `AutoMagicGroups` is optional (an absent
or `null` field means no groups) and is matched by `Code` within its base item.
Reordering those groups is non-semantic. Added, removed, and modified groups
include their full original records, and `NestedCounts` counts groups only for
base items matched by `NameKey`; groups belonging to added/removed equipment are
already present in the complete parent record. These records describe possible
intrinsic affixes, not a guarantee that an item will roll a group. Group fields,
including level requirements, property lines, and nested selection metadata,
are compared as exported without interpreting gameplay formulas.

If the same name/type identity appears more than once on either side, pairing it
would be ambiguous. Those records receive side-specific occurrence suffixes in
their `Key`, are reported as additions/removals, and produce an
`ambiguous-variant-identity` category warning. Invalid `Types` identity data
invalidates only the runeword category.

For `magicprefix` and `magicsuffix`, each category is matched independently.
Affixes with a unique `NameKey` on both sides pair by name, so every changed
exported field (including level, group, class restrictions, equipment eligibility,
property lines, and property-group selection data) is reported as a modification.
Duplicate names are paired only when a projection is unique on both remaining
sides, in this order: `Group` + `Level` + sorted `Types` + property signature;
`Group` + `Level` + sorted `Types`; `Group` + sorted `Types` + property signature;
then `Group` + `Level` + property signature. The property signature preserves
line order and uses each line's nonnumeric JSON content (including property keys
and nonnumeric arguments), but omits numeric values so changed numeric rolls are
diffed as modifications instead of changing identity. The report's `MatchedBy`
values are `PropertySignature`, `StructuralMetadata`, `PropertiesWithoutLevel`,
or `PropertiesWithoutTypes` for those passes, and `NameKey` for direct name matches.

Affix record keys show the name, group, level, and ordinal-sorted `Types`; when
that structural identity collides within either snapshot, the key also contains
the property signature. Remaining duplicate candidates that cannot be uniquely
paired are emitted as additions/removals with `#old-N`/`#new-N` disambiguators
and an `ambiguous-affix-identity` warning listing the candidates. `Types` and
`ETypes` ordering is non-semantic, while the order of `Lines` is meaningful.
`ETypes` is compared as data, not used for identity.

This strategy is intentionally bounded: numeric-only properties do not separate
variants, and two otherwise indistinguishable duplicate rows cannot be
reconciled across snapshots. Such candidates are not paired, even if their
positions happen to correspond. The comparator reports an ambiguity warning
rather than guessing; no exporter identity field is added for this limitation.

All identities use ordinal string matching. Missing identity values, duplicate
identities in categories that require unique keys, non-object records, malformed
category arrays, or malformed `SetItems`/present `AutoMagicGroups` invalidate
only that category and produce no partial diff for it. Duplicate runeword names
are allowed when their type identities distinguish the variants; duplicate
name/type identities are reported as ambiguous instead. Unique and set `Index`
values are strings (translation identities), never numeric row positions. Set
pieces are matched within their parent set, not globally.

Object property ordering and JSON whitespace are ignored. Ordinary arrays are
compared element-by-element when lengths match and as one whole-array change when
lengths differ. Their ordering remains meaningful, including unique property
`Lines`, equipment `Lines`/`DamageTypes`, and set bonus arrays. The `SetItems`
and `AutoMagicGroups` arrays are exceptions: set pieces match by `Index` and
automagic groups by `Code`, so reordering either is not reported as a gameplay
change. Structural paths use dots, array indexes, and keyed selectors, for
example `Lines[0].args[1]`, `SetItems[Index="Civerb's Ward"].Lines[0]`, or
`AutoMagicGroups[Code="of the Titan"]`.

Categories and records are emitted deterministically: categories use the order
above; cube records sort by row index then `Id`; other categories and nested
records sort by ordinal identity. Object fields in field changes are sorted by
ordinal property name. A changed identity appears as a removal plus an addition;
replacement matching is intentionally not guessed.

## Exit codes

- `0`: report written and validation/comparison succeeded. A `Complete: false`
  snapshot can still yield this code if all eight selected category files are
  verified; the report retains the incompleteness warning.
- `1`: `changes.json` was written, but validation or category comparison errors
  prevented a successful result.
- `2`: fatal comparison or report-write failure.
- `3`: output path conflicts with either input snapshot.
