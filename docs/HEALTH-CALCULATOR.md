# Health calculator export

`MonsterHealthExporter` writes `keyed/health-calculator.json`: the raw
contributors to monster life, so the website can compute HP per difficulty,
player count and monster kind. Numbers are copied from the mod's tables; the
exporter does not pre-compute HP. Inputs resolve through `SourceRoots` as mod
copy, then CASC copy; configure only the two data roots, never an inferred
sibling of the Excel directory.

## Inputs

| Input | Used for |
|---|---|
| `monstats.txt` | per-monster level, `MinHP`/`MaxHP`, resistances (`ResDm/Ma/Fi/Li/Co/Po`, each with `N`/`H` variants), flags, `MonProp`, `DamageRegen` |
| `monlvl.txt` | per-level `HP`/`LHP` multipliers for the three difficulties |
| `levels.txt` | area ids, `LevelName` keys, `MonLvlEx`/`N`/`H`, and spawn pools (`mon*`, `nmon*`, `umon*`) |
| `monumod.txt` | the minion/champion/unique HP bonus constants |
| `monprop.txt` | hashed only (listed in `Sources`) so the website can tell which copy it reads `MonProp` against |

If any of the first four is absent the step is skipped, reported in
`extras/import-report.txt` and listed `Skipped` in `manifest.json`; any
previous `health-calculator.json` is left untouched and listed as `NotWritten`,
never in `Files[]`.

Malformed required input or invalid bonus configuration fails this isolated
step with contextual errors; later exporters, reports and schema-2 manifest
still run. Publish only a validated staging bundle
([WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#manifest-schema-2-and-publishing)).

## Configuration (`export-config.json` → `monsterHealth`)

| Key | Meaning |
|---|---|
| `additionalPlayerPercent` | HP added per extra player (engine rule, 50). |
| `maximumPlayers` | Player-count cap (8). |
| `maximumLife` | Engine life ceiling (8388607). |
| `minionBonusModifierId` / `championBonusModifierId` / `uniqueBonusModifierId` | `monumod.txt` row id of the normal-difficulty bonus; nightmare and hell are the next two rows (1, 4, 7). A missing row or empty `constants` fails the step with the row id in the error. |

`dropMonsterAreas` (shared with the drop calculator) adds fixed boss areas.

## Output

| Field | Shape |
|---|---|
| `Rules` | `{ AdditionalPlayerPercent, MaximumPlayers, MaximumLife }` (the monumod ids are inputs, not wire fields) |
| `Monsters[]` | `{ Id, NameKey?, FallbackName, Enabled, Killable, Boss, NoRatio, Align, DamageRegen, MonProp, Stats[3], Areas[3] }`; `NameKey` is omitted when the `NameStr` has no translation (render `FallbackName`) |
| `Monsters[].Stats[d]` | `{ Level, MinHP, MaxHP, PhysicalResist, MagicResist, FireResist, LightningResist, ColdResist, PoisonResist }` for difficulty `d` |
| `Monsters[].Areas[d]` | `levels.txt` ids the monster spawns in on difficulty `d` |
| `Levels[]` | `{ Level, HP[3], LHP[3] }` |
| `Areas[]` | `{ Id, NameKey, Levels[3] }` |
| `Bonuses` | `{ Minion[3], Champion[3], Unique[3] }` |
| `Sources[]` | `{ Code, Hash }` — SHA-256 of each input table present |

## Regression check

After the smoke export in [TESTING.md](TESTING.md):

```powershell
$h = Get-Content .\test-output\keyed\health-calculator.json -Raw | ConvertFrom-Json
$h.Bonuses                          # three arrays of three integers from monumod.txt
($h.Monsters | Where-Object Id -eq 'skeleton1').Stats[0]
```
