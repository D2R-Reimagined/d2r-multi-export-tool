# Drop calculator export

The normal console and GUI pipeline now emits `keyed/drop-calculator.json`.
Publish the bundle through the staging command in [TESTING.md](TESTING.md#22-publishing)
before refreshing the website's `static/data`; do not copy the output directory wholesale. There is no version
selector or separately maintained drop-rate catalog.

The bundle contains named item eligibility and rarity, rune item types,
equipment quality levels, expansion item ratios, generated equipment treasure
classes, source treasure-class weights, and monster/area/difficulty variants.
Names remain translation keys. Structural fields are excluded from the
translation audit, while `NameKey` and `AreaKey` are audited normally.

`dropRuneTypes` configures the rune search and `dropMiscCodes` the miscellaneous
item search. `dropMonsterLevelBonuses` holds the engine's spawn-kind level
bonuses (champion +2, unique +3, superunique +3; not applied to boss classes),
`dropGeneratedTreasureClassLevelStep` the width of the generated equipment
treasure-class bands (3), and `dropItemRatioVersion` the `itemratio.txt`
`Version` rows exported (1). `dropMonsterAreas` supplies fixed
act-boss placements. `dropSuperUniqueAreas` maps fixed
superunique placements to `levels.txt` IDs because generic monster pools do not
identify fixed map placements. Levels and translated area names are always
read from the current mod. Extend these mappings if a mod moves a fixed boss.
Unmapped superuniques are included only when they have a dedicated named
monster class; they do not inherit a generic class's unrelated areas.

Fixed vanilla placements were checked against Blizzard's Arreat Summit:
[Act I](https://classic.battle.net/diablo2exp/monsters/act1-superuniques.shtml),
[Act II](https://classic.battle.net/diablo2exp/monsters/act2-superuniques.shtml),
[Act III](https://classic.battle.net/diablo2exp/monsters/act3-superuniques.shtml),
[Act IV](https://classic.battle.net/diablo2exp/monsters/act4-superuniques.shtml),
[Act V](https://classic.battle.net/diablo2exp/monsters/act5-superuniques.shtml).
The Cow King and Reimagined's Cow Queen share the source's cowking class.

## Parser dependency

The drop-table DTOs/parsers (`DropTreasureClassParser`, `DropItemRatioParser`,
`DropNamedItemParser`, `DropLevelParser`) ship in the published
`D2RReimaginedTools.FileExtensions` package that `D2RMultiExport.Lib.csproj`
references; no local `ProjectReference` or sibling worktree is involved.

## Inputs, conditions and failure handling

The exporter needs `treasureclassex.txt`, `itemratio.txt`, `uniqueitems.txt`,
`setitems.txt`, `itemtypes.txt`, `armor.txt`, `weapons.txt`, `misc.txt`,
`monstats.txt`, `superuniques.txt` and `levels.txt` in the excel directory. If
any is absent the step is reported as skipped in `extras/import-report.txt` and
`manifest.json`; an earlier `keyed/drop-calculator.json` is left untouched but is
listed under the step's `NotWritten`, not in `Files[]`, so it is never presented
as current. A duplicate `itemtypes.txt` `Code` keeps the first
row and is reported; a repeated treasure-class name is reported and every row
is exported in file order; a unique/set row with no base item code is reported
and omitted.

`Condition` (treasure classes and named items) is exported as the raw BBE text
for the website to interpret per source. It is validated and, when difficulty-only,
also evaluated with
the same `CalcExpression` grammar the skill exporter uses
(`Import/DropConditionValidator.cs`): unbalanced or unknown syntax, a `cond`
name BBE does not define, or a `stat`/`skill`/`miss` reference to a row the mod
lacks is reported with its file and row. The current Reimagined data reports
none.

## Verified smoke export

Use the standard smoke export in [TESTING.md](TESTING.md) §2. The current
Reimagined checkout ships every input listed above, so
`keyed/drop-calculator.json` is produced directly from
`<mod>\data\global\excel` (any table the mod omits falls back to the CASC copy).
With a complete CASC extraction on `--casc-data`, the run reports 0 errors and 0
missing translations, and `manifest.json` lists the step as `Written`.

Each non-empty `Condition` additionally carries, without changing the raw text:
`ConditionDifficulties` — the difficulty indexes (0 normal, 1 nightmare, 2 hell) on
which a difficulty-only condition holds, solved with the same `CalcExpression`
grammar per difficulty — or `ConditionRuntime: true` when it reads live game state
(`stat(…)`, another `cond`, a skill), in which case the raw expression is the only
truth. Both are omitted for an empty condition. Current mod: the five Desecrated TCs
resolve to `[2]`; the 28 herald conditions are `ConditionRuntime`. Raw values keep
the source's surrounding double quotes (`"cond('Difficulty', hell)"`), which the
website strips before matching; the resolver parses them identically.

## Calculation scope

The website computes per-kill probability of at least one selected item,
including player/nearby-party no-drop scaling, positive and ordered negative
picks, six generated-item capacity, unique/set quality rolls and rarity, and
directly assigned named items. Magic find does not affect runes.

Calculations cover ordinary non-terrorized monster sources and separate quest
boss sources. They assume a unique has not already dropped in the current game.
For unrelated conditional drops, the engine evaluates both gate extremes and
only returns a percentage if their capacity effects cannot change the result.
Unsupported conditional eligibility or capacity effects remain unavailable,
rather than being treated as zero. Scripted sources without an exported area
use their source monster levels and are labeled fixed/summoned spawns.

The website currently consumes the raw literal Hell condition rather than the two
resolved metadata fields; adoption is pending ([WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md)).

The website regression suite covers known exact probabilities, ordered cap
behavior, conditional gates, nonrandom named items, quality and rarity,
suggestions, and graph resolution against this export in all difficulties.
