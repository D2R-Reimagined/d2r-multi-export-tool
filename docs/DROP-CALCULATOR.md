# Drop calculator export

The normal console and GUI pipeline now emits `keyed/drop-calculator.json`.
Copy this file and the accompanying `strings/*.json` bundles to the website's
`static/data` directory whenever mod data is refreshed. There is no version
selector or separately maintained drop-rate catalog.

The bundle contains named item eligibility and rarity, rune item types,
equipment quality levels, expansion item ratios, generated equipment treasure
classes, source treasure-class weights, and monster/area/difficulty variants.
Names remain translation keys. Structural fields are excluded from the
translation audit, while `NameKey` and `AreaKey` are audited normally.

`dropRuneTypes` configures the rune search. `dropMonsterAreas` supplies fixed
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

## Local parser dependency

The exporter uses four new header-mapped drop-table DTOs/parsers in the sibling
`d2r-dotnet-tools-dropcalc` worktree, based on `origin/main` at `c34aa6e`.
The older `d2r-dotnet-tools` worktree and its unrelated edits are preserved.
The local `ProjectReference` is intentional for testing this unpublished parser
addition. Before committing/releasing the exporter, release the parser changes
upstream and replace it with a `D2RReimaginedTools.FileExtensions` package
reference to that published version, as required by `AGENTS.md`.

## Verified smoke export

The mod checkout omits unchanged vanilla rare-name tables. For verification,
`test-input-drop/data/global/excel` overlays the mod's current `.txt` files on
the installed loader compiler's `3.3.0` base tables. Its `data/hd` junction points
to the mod checkout's asset directory. This staging does not alter the mod.

```powershell
dotnet build .\D2RMultiExport.sln
dotnet run --no-build --project .\D2RMultiExport.Console -- export `
  --excel .\test-input-drop\data\global\excel `
  --mod-strings ..\d2r-reimagined-mod\data\local\lng\strings `
  --out .\test-output
```

The full export completed with 0 errors and the existing 66 skill-description
warnings. No CASC base-string directory was supplied in this environment; the
missing-translation audit therefore includes existing base-only skill keys and
some directly assigned nonrandom item names. On the website, only newly
referenced drop-name translations were added to the existing language bundles,
preserving all previously exported translations.

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

The website regression suite covers known exact probabilities, ordered cap
behavior, conditional gates, nonrandom named items, quality and rarity,
suggestions, and graph resolution against this export in all difficulties.
