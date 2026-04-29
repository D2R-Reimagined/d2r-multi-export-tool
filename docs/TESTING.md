# Manual Regression Testing

This project has no automated test project. The verified end-to-end CLI
export against a real D2R Reimagined mod source tree is the de-facto
smoke test, and `test-output\` at the repo root is its scratch output
folder (overwriting it on every run is the intended workflow).

Re-run the steps below after any edit to the import / translation /
export pipeline, especially:

- `D2RMultiExport.Lib\Translation\PropertyKeyResolver.cs`
- `D2RMultiExport.Lib\Translation\SyntheticStringRegistry.cs`
- `D2RMultiExport.Lib\Translation\MultiLanguageTranslationService.cs`
- `D2RMultiExport.Lib\Import\PropertyMapper.cs`
- `D2RMultiExport.Lib\Import\PropertyCleanup.cs`
- `D2RMultiExport.Lib\Import\UniqueImporter.cs`
- `D2RMultiExport.Lib\Import\DamageArmorCalculator.cs`
- `D2RMultiExport.Lib\Models\KeyedLine.cs`

## 1. Build

```powershell
dotnet build .\D2RMultiExport.sln
```

`.txt` parsing is provided by the `D2RReimaginedTools.FileExtensions`
NuGet package. If a parsing regression is suspected, swap the
`<PackageReference>` in
`D2RMultiExport.Lib\D2RMultiExport.Lib.csproj` for a local
`<ProjectReference>` to the sibling `..\d2r-dotnet-tools` clone, rebuild,
re-run the smoke export below, and (once verified) open the fix as a PR
against `d2r-dotnet-tools` rather than patching around it here. Revert
the swap before committing — see `AGENTS.md` → *External parsing
dependency* for the full policy.

## 2. Export to `test-output\`

Run from the repo root, with relative paths to a sibling mod checkout
and a CascView-extracted base-strings dump (substitute the two `<...>`
placeholders for whatever local paths apply):

```powershell
dotnet run --project .\D2RMultiExport.Console -- export `
  --excel        "<path-to-mod>\data\global\excel" `
  --mod-strings  "<path-to-mod>\data\local\lng\strings" `
  --base-strings "<path-to-casc-extracted>\data\local\lng\strings" `
  --out          ".\test-output"
```

Expected stdout: `Export completed successfully.` followed by the
`enUS keys loaded` count. Any `missing translation key(s) referenced`
line points at a real regression — open
`test-output\extras\missing-translations.txt` to investigate.

## 3. Regression fixtures

These fixtures encode bugs that have shipped in the past. After running
the export, verify each one against `test-output\keyed\*.json`;
mismatches mean the corresponding fix has regressed.

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

Failure modes seen historically (do not let any of these reappear):

- `{ "key": "ModStre9t", "args": [0.07] }` — the original `/ 100f` math,
  floored by the website to "Repairs 0 durability per second".
- `{ "key": "ModStre9t", "args": [1, 10] }` — happens when descstr2 is
  empty on the stat row and the fallback uses `templateKey` instead of
  the hardcoded `"ModStre9u"`.
- `{ "key": "ModStre9u", "args": [1] }` — missing the seconds arg; the
  template renders with a stray unsubstituted `%d`.

### 3.2 Adding a new fixture

When a bug is fixed for a specific item, add a row to the table above
with the item name, code (from `armor.txt` / `weapons.txt`), the
relevant property, the parameter value, and the exact `KeyedLine`
shape expected. Future runs of section 3 then prove the fix still
holds.
