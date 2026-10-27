# Drop calculator export

The console and GUI pipeline emits `keyed/drop-calculator.json` into the output
directory together with the rest of the bundle
([WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#manifest-schema-2-and-output-integrity)).
There is no version selector or separately maintained drop-rate catalog.

The bundle contains named item eligibility and rarity, rune item types,
equipment quality levels, expansion item ratios, generated equipment treasure
classes, source treasure-class weights, and monster/area/difficulty variants.
Names remain translation keys. Structural fields are excluded from the
translation audit, while `NameKey` and `AreaKey` are audited normally.
The document follows `--pretty` (indented by default, compact with
`--pretty false`) without changing any member or value.

`dropRuneTypes` configures the rune search and `dropMiscCodes` the miscellaneous
item search. `dropMonsterLevelBonuses` holds the engine's spawn-kind level
bonuses (champion +2, unique +3, superunique +3; not applied to boss classes),
`dropGeneratedTreasureClassLevelStep` the width of the generated equipment
treasure-class bands (3), and `dropItemRatioVersion` the `itemratio.txt`
`Version` rows exported (1). `dropMonsterAreas` supplies fixed
act-boss placements. `dropSuperUniqueAreas` maps fixed
superunique placements to `levels.txt` IDs because generic monster pools do not
identify fixed map placements. Area levels and name keys come from the resolved
`levels.txt` (see [Inputs](#inputs-conditions-and-failure-handling)), never from
a built-in area list. Extend these mappings if a mod moves a fixed boss.
Unmapped superuniques are included only when they have a dedicated named
monster class; they do not inherit a generic class's unrelated areas.

Reference for the vanilla superunique placements (Blizzard's Arreat Summit):
[Act I](https://classic.battle.net/diablo2exp/monsters/act1-superuniques.shtml),
[Act II](https://classic.battle.net/diablo2exp/monsters/act2-superuniques.shtml),
[Act III](https://classic.battle.net/diablo2exp/monsters/act3-superuniques.shtml),
[Act IV](https://classic.battle.net/diablo2exp/monsters/act4-superuniques.shtml),
[Act V](https://classic.battle.net/diablo2exp/monsters/act5-superuniques.shtml).
The Cow King and Reimagined's Cow Queen share the source's cowking class.

## Inputs, conditions and failure handling

The exporter needs `treasureclassex.txt`, `itemratio.txt`, `uniqueitems.txt`,
`setitems.txt`, `itemtypes.txt`, `armor.txt`, `weapons.txt`, `misc.txt`,
`monstats.txt`, `superuniques.txt` and `levels.txt`, each resolved through
`SourceRoots` (mod copy, else CASC copy). The drop-table parsers
(`DropTreasureClassParser`, `DropItemRatioParser`, `DropNamedItemParser`,
`DropLevelParser`) come from the published `D2RReimaginedTools.FileExtensions`
package. If any input is absent the step is reported as skipped in
`extras/import-report.txt` and `manifest.json`; an earlier
`keyed/drop-calculator.json` is left untouched but is
listed under the step's `NotWritten`, not in `Files[]`, so it is never presented
as current. A `dropGeneratedTreasureClassLevelStep` below 1 fails the step.
A duplicate `itemtypes.txt` `Code` keeps the first
row and is reported; a repeated treasure-class name is reported and every row
is exported in file order; a unique/set row with no base item code is reported
and omitted.

`Condition` (treasure classes and named items) is exported as the raw BBE text
for the website to interpret per source. It is validated and, when difficulty-only,
also evaluated with
the same `CalcExpression` grammar the skill exporter uses
(`Import/DropConditionValidator.cs`): unbalanced or unknown syntax, a `cond`
name BBE does not define, or a `stat`/`skill`/`miss` reference to a row the mod
lacks is reported with its file and row.

## Condition metadata

Each non-empty `Condition` on named `Items[]` and `TreasureClasses[]`
additionally carries, without changing the raw text:
`ConditionDifficulties` — the difficulty indexes (0 normal, 1 nightmare, 2 hell) on
which a difficulty-only condition holds, solved with the same `CalcExpression`
grammar per difficulty, together with `ConditionRuntime: false` — or only
`ConditionRuntime: true` when the condition reads live game state (`stat(…)`,
another `cond`, a skill) or cannot be resolved, in which case the raw expression
is the only truth. Both are omitted for an empty condition. For example, Desecrated treasure
classes resolve to `[2]` and herald conditions are `ConditionRuntime`. Raw values
keep the source's surrounding double quotes (`"cond('Difficulty', hell)"`);
consumers strip them before matching, and the resolver parses them identically.

Live state cannot be recovered from this static bundle. Consumers must keep
runtime or unrecognized conditions unavailable wherever they can affect
eligibility or capacity; never treat them as false or zero.

## Calculation scope

The bundle supports computing the per-kill probability of at least one selected item,
including player/nearby-party no-drop scaling, positive and ordered negative
picks, six generated-item capacity, unique/set quality rolls and rarity, and
directly assigned named items. Magic find does not affect runes.

Calculations cover ordinary non-terrorized monster sources and separate quest
boss sources. They assume a unique has not already dropped in the current game.
For unrelated conditional drops, the calculator evaluates both gate extremes and
only returns a percentage if their capacity effects cannot change the result.
Unsupported conditional eligibility or capacity effects remain unavailable,
rather than being treated as zero. Scripted sources without an exported area
use their source monster levels and are labeled fixed/summoned spawns.

The calculator itself, its exact scope and its regression suite live in the
website repository; this section describes the intended use of the bundle and
is not verified by this repository's tests.
