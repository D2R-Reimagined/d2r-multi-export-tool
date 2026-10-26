# Export snapshot comparison

The `compare` CLI command compares two existing export directories and writes a
versioned JSON change report. It does not run an export, alter either input, or
infer changes from files that the manifests do not vouch for.

```powershell
dotnet run --project .\D2RMultiExport.Console -- compare `
  --old "<old-export-directory>" `
  --new "<new-export-directory>" `
  --out "<comparison-output-directory>"
```

The comparison output may not be either input directory, a directory inside an
input, or a symlink/junction alias into an input. It contains `changes.json`;
`--pretty` is available and defaults to true.
No developer test paths are built into the application.

## Snapshot validation

Each input must contain a readable schema-2 `manifest.json`. The comparison reads
only `keyed/cube-recipes.json`, and only when all of the following are true:

- The manifest records `Succeeded: true`.
- Its `keyed-core` step is `Written` and lists `keyed/cube-recipes.json` as an
  output.
- The file is listed in `Files[]` with the matching producer and exists under
  the snapshot root.
- The recorded byte length and SHA-256 match when those values are available.

An absent manifest, failed export, skipped/not-written cube output, missing
`Files[]` entry, invalid payload or integrity mismatch prevents that category from
being compared. A physical file left by an earlier run is never treated as current
data or as proof of a recipe deletion. When the manifest has `Complete: false`, the
report warns; comparison is still allowed only if the cube file independently
passes the current-run and integrity checks above. Missing integrity metadata is
also reported as a warning.

The report records each snapshot's absolute path, manifest schema version,
success/completeness flags, manifest SHA-256 and validation diagnostics. The
manifest is unsigned: its hash detects later payload mismatch but does not
authenticate the manifest or prove source/code provenance.

## Report schema version 1

`changes.json` uses PascalCase property names and contains:

- `SchemaVersion`: currently `1` for this comparison-report contract.
- `Succeeded`: false if snapshot or category validation/comparison errors occurred.
- `OldSnapshot` and `NewSnapshot`: snapshot identification, manifest integrity
  metadata, `Errors` and `Warnings`.
- `Categories`: one `cube-recipes` entry with `File`, `Status`, `MatchKey`,
  `IgnoredFields`, verified file hashes, category diagnostics, counts, and the
  added/removed/modified recipe lists.
- `Counts`: aggregate `Added`, `Removed`, `Modified`, `Unchanged` and `Reindexed`
  counts for categories that were compared.

Recipes match by their stable `Id`, using ordinal string comparison. Row `Index`
is excluded from gameplay-change detection; matched recipes whose `Index` differs
contribute to `Reindexed`. Additions and removals include `Id`, `Index` and a
complete `Recipe` JSON value. A modified recipe includes its identity and old/new
indexes, sorted `ChangedFields`, `Changes` entries (`Path`, `Kind`, `OldValue`,
`NewValue`), and complete `Old` and `New` recipe values. A missing side of a field
change is omitted; an explicitly present JSON `null` remains a JSON null value.

Object property ordering and JSON whitespace are ignored. Objects are compared by
property name; equal-length arrays are compared element-by-element and arrays of
different lengths are reported as one change at the array path. Paths use dots for
object properties and brackets for array positions, for example
`Outputs.A.Lines[0].args[0]`. Added and removed recipe arrays are ordered by index
then `Id`; matched records are processed by old index then `Id`.

Missing or duplicate recipe `Id` values, or non-object array entries, invalidate
the whole category rather than generating a partial diff. A recipe whose stable
`Id` changes appears as a removal and an addition; replacement matching is
intentionally not guessed. This first version compares no categories other than
cube recipes.

## Exit codes

- `0`: report written and validation/comparison succeeded. A `Complete: false`
  snapshot can still yield this code if the selected category file is verified;
  the report retains the incompleteness warning.
- `1`: `changes.json` was written, but validation or category comparison errors
  prevented a successful result.
- `2`: fatal comparison or report-write failure.
- `3`: output path conflicts with either input snapshot.