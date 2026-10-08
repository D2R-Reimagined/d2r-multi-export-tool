# Attack-speed (IAS) calculator export

`AttackSpeedExporter` writes `keyed/ias-calculator.json`: weapon base speeds,
`animdata.d2` frame data and the mod's skill speed curves, solved with the
same `SkillCalculator` the skill descriptions use.

## Inputs

| Input | Used for |
|---|---|
| `weapons.txt` | `Speed`, weapon class (`WClass`/`2HandedWClass`), handedness, item types |
| `skills.txt` | animation mode, sequence, restrictions, states, item-type gates, and the stat/calc columns of buff skills |
| `itemtypes.txt` | the `Equiv1`/`Equiv2` type graph and class restriction |
| `global/animdata.d2` resolved by `SourceRoots` from Mod Data only | frame counts, animation speed and action frames (optional — absent means `Animations: []` and a warning; vanilla timings are never substituted) |

If one of the three `.txt` files is absent the step is skipped and reported; any
previous `ias-calculator.json` is left untouched and listed as `NotWritten` in
`manifest.json`, never as current. A duplicate `itemtypes.txt` `Code` keeps the
first row and is reported.

## Configuration (`export-config.json` → `attackSpeed`)

| Key | Meaning |
|---|---|
| `curveLevels` | skill levels each curve is solved for (100). Formerly hardcoded. |
| `classTokens`, `formTokens` | class/shape-shift animation tokens; also filter which `animdata.d2` records are exported |
| `animationModes` | `skills.txt` `anim` values that make a skill a speed-relevant attack |
| `excludedSkills`, `commonSkills` | skills dropped / non-class skills kept |
| `buffSkills`, `attackRateStat`, `buffCalcOverrides` | which skills contribute an IAS buff, the stat that marks it, and corrected calc text where the mod cell is not the engine's arithmetic |
| `skillRules` | per-skill frame rules (`kind`, `rollbackCalc`, `hitsCalc`, `selfSpeedCalc`, `speedAdjustment`, …) |
| `startingFrames` | per-mode starting frame |

## Output

`SchemaVersion`, `Sources[] { Code, Hash }`, `Classes[] { ClassCode, Token, NameKey }`,
`Weapons[]`, `Skills[]`, `Buffs[] { Code, NameKey, Values }`,
`Animations[] { Code, Frames, AnimationSpeed, ActionFrames }`, `StartingFrames`,
`FormTokens`.

A curve (`Values`, `Rollback`, `Hits`, `SelfSpeed`) is `null` when the rule does
not apply. A curve whose expression is present but cannot be solved is also
`null`, and is reported in `extras/import-report.txt` with the level and the
`CalcTrace` root cause — it is never silently zero.

Buff curves are read from aura stat slots 1–6 and passive stat slots 1–14 of
`skills.txt` through typed accessors (`SkillCalcSource`), in the order aura N
then passive N.

## Regression check

The diminishing-returns family (`dm12`, `dm34`, …) must use the engine's
truncated percentage (`export-config.json` → `calcFormulaOverrides`; see
[SKILL-CALCULATIONS.md](SKILL-CALCULATIONS.md)). A level-20 Fanaticism speed of 770 instead of 35 means `dmXY`
is being treated as linear growth. The current four curves match all 100
levels with two-stage truncation and no implicit max clamp:

| Buff | Level 1 | Level 20 | Level 60 | Level 100 |
|---|---:|---:|---:|---:|
| Fanaticism | 14 | 35 | 40 | 40 |
| Frenzy | 7 | 42 | 50 | 51 |
| Wearwolf | 20 | 68 | 80 | 82 |
| Quickness | 21 | 52 | 60 | 61 |

Verify the generated data locally (no website modification needed):

```powershell
$iasBundle = Get-Content .\test-output\keyed\ias-calculator.json -Raw | ConvertFrom-Json
$iasRanges = @{ Fanaticism = @(10,40); Frenzy = @(0,50); Wearwolf = @(10,80); Quickness = @(15,60) }
foreach ($iasCode in $iasRanges.Keys) {
    $iasValues = ($iasBundle.Buffs | Where-Object Code -eq $iasCode).Values
    if ($iasValues.Count -ne 100) { throw "$iasCode curve is incomplete" }
    for ($iasIndex = 0; $iasIndex -lt $iasValues.Count; $iasIndex++) {
        $iasPct = [Math]::Truncate(110 * ($iasIndex + 1) / ($iasIndex + 7))
        $iasMin, $iasMax = $iasRanges[$iasCode]
        $iasExpected = $iasMin + [Math]::Truncate(($iasMax - $iasMin) * $iasPct / 100)
        if ($iasValues[$iasIndex] -ne $iasExpected) { throw "$iasCode level $($iasIndex + 1) mismatch" }
    }
}
```

Adapt ranges to intentional source balance changes. `Maul.Values: null` is the
configured empty override, not a failed evaluation. The current website's IAS
test anchors already agree with these corrected values.
