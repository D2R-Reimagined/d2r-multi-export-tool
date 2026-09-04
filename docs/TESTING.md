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
- `D2RMultiExport.Lib\Import\SkillCalculator.cs`
- `D2RMultiExport.Lib\Import\SkillDescriptionImporter.cs`
- `D2RMultiExport.Lib\Exporters\SkillTreeExporter.cs`

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

The website also uses two export details when rendering save-backed characters:

- Each elemental skill carries its source `EType` as `ElementType` (for example,
  Frozen Orb is `cold`) so `item_elemskill` bonuses can be applied without guessing.
- Description tables extend 40 levels past the hard-point cap. Frozen Orb therefore has
  `Descriptions.MaxLevel: 65`, covering heavily geared characters whose effective skill
  rank is substantially above its `MaxLevel: 25` investment cap.

```powershell
$skills = Get-Content .\test-output\keyed\skills.json -Raw | ConvertFrom-Json
$allSkills = @($skills | ForEach-Object { $_.Tabs | ForEach-Object { $_.Skills } })
$allSkills | Where-Object Code -eq 'Frozen Orb' |
  Select-Object Code, ElementType, MaxLevel, @{Name='DescriptionMaxLevel'; Expression={$_.Descriptions.MaxLevel}}
```

Failure modes to watch for:

- **Mana cost `2`** — the `usmc / 256` division was truncated to an integer somewhere; the
  game shows 2.5. `SkillDescriptionImporter` divides in floating point on purpose.
- **`"values"` spread across many lines** — `LevelTableConverter` stopped being applied.
  The tables are ~300k numbers; indenting them multiplies the file several times over.
- **1 projectile at every level** — `skill('Fire Bolt'.blvl)` inside the skill's own
  `Calc1` resolved to 0 instead of the level being rendered. Self-references are the level;
  *other* skills are 0.
- **A jump in warning count** in `extras\import-report.txt`. The `SkillDescription`
  category is expected to warn (missile and minion-level symbols are documented gaps in
  `AGENTS.md`) — the count moving is the signal, not its presence.

The importer probes every key it emits, so a skill template the mod never translated shows
up in `extras\missing-translations.txt`. One is currently expected — `skillnameskele`, the
synergy-source name on Teeth / Bone Spear / Bone Spirit, which has no row in the mod's
string files. Fix it in `data\local\lng\strings`, not here.

### 3.3 Unique save IDs and named sprite assets

`UniqueExport.FileIndex` must use the parsed UniqueItems.txt row position after excluding
the legacy `Expansion` marker. Other section-header rows still consume binary IDs, while the
informational `*ID` column skips them and therefore drifts from the save format. Unique
artwork must be selected by normalized unique name from `hd/items/uniques.json`, not by
array position.

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

When a bug is fixed for a specific item, add a row to the table above
with the item name, code (from `armor.txt` / `weapons.txt`), the
relevant property, the parameter value, and the exact `KeyedLine`
shape expected. Future runs of section 3 then prove the fix still
holds.
