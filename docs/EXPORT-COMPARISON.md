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
input, or a symlink/junction alias into an input. It contains `changes.json`;
`--pretty` is available and defaults to true. No developer test paths are built
into the application.

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
- `Categories`: one entry each for `cube-recipes`, `uniques`, `sets`, and
  `runewords`, in that order. Each has `File`, `Status`, `MatchKey`, `IgnoredFields`, verified file
  hashes, diagnostics, `Counts`, and `AddedRecords`, `RemovedRecords`, and
  `ModifiedRecords` arrays.
- `Counts`: aggregate added/removed/modified/unchanged records and `Reindexed`
  count across categories whose status is `Compared`. Set-piece counts are kept
  separate in that category's `NestedCounts` and do not double-count set records.

An added or removed record contains `Key`, optional `RowIndex`, and its complete
original `Record` JSON. A modified record contains the identity `Key`, applicable
old/new row indexes, sorted `ChangedFields`, `Changes` (`Path`, `Kind`, `OldValue`,
`NewValue`), and complete `Old`/`New` record JSON. The cube category uses the
numeric `Index` only as `RowIndex`; it is not an identity. Index-only shifts are
excluded from gameplay field changes and counted by `Reindexed`.

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

If the same name/type identity appears more than once on either side, pairing it
would be ambiguous. Those records receive side-specific occurrence suffixes in
their `Key`, are reported as additions/removals, and produce an
`ambiguous-variant-identity` category warning. Invalid `Types` identity data
invalidates only the runeword category.

All identities use ordinal string matching. Missing identity values, duplicate
identities in categories that require unique keys, non-object records, malformed
category arrays, or malformed `SetItems` invalidate only that category and
produce no partial diff for it. Duplicate runeword names are allowed when their
type identities distinguish the variants; duplicate name/type identities are
reported as ambiguous instead. Unique and set `Index` values are strings
(translation identities), never numeric row positions. Set pieces are matched
within their parent set, not globally.

Object property ordering and JSON whitespace are ignored. Ordinary arrays are
compared element-by-element when lengths match and as one whole-array change when
lengths differ. Their ordering remains meaningful, including unique property
`Lines` and set bonus arrays. The set `SetItems` array is the exception: it is
matched by piece `Index`, so reordering pieces is not reported as a gameplay
change. Structural paths use dots, array indexes, and keyed set-piece selectors,
for example `Lines[0].args[1]` or `SetItems[Index="Civerb's Ward"].Lines[0]`.

Categories and records are emitted deterministically: categories use the order
above; cube records sort by row index then `Id`; uniques, sets, nested set pieces,
and runewords sort by ordinal identity. Object fields in field changes are sorted
by ordinal property name. A changed identity appears as a removal plus an
addition; replacement matching is intentionally not guessed.

## Exit codes

- `0`: report written and validation/comparison succeeded. A `Complete: false`
  snapshot can still yield this code if all four selected category files are
  verified; the report retains the incompleteness warning.
- `1`: `changes.json` was written, but validation or category comparison errors
  prevented a successful result.
- `2`: fatal comparison or report-write failure.
- `3`: output path conflicts with either input snapshot.
