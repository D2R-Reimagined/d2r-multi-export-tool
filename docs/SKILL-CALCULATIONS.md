# Skill descriptions and calculation contracts

Specialized guidance referenced by [AGENTS.md](../AGENTS.md). Read this before
changing skill import, BBE evaluation or skill wire tables.

`keyed/skills.json` carries what each skill *does*, not just its name. Every class skill
gets a `Descriptions` object holding the three boxes D2's own tooltip uses — `Stats`
(`descline1..6`), `Details` (`dsc2line1..5`), `Synergies` (`dsc3line1..7`) — plus the
`MaxLevel` its value tables reach, the `HardPointCap` (the skill's own `maxlvl`) that
splits spent points from `+skills` bonus levels, and the `CharacterLevelCap` bounding the
`ulvl` axis.

```jsonc
{
  "Id": 36, "Code": "Fire Bolt", "NameKey": "skillname36",
  "Descriptions": {
    "MaxLevel": 100, "HardPointCap": 25, "CharacterLevelCap": 100,
    "Stats": [
      { "key": "StrSkill5", "values": [[3,4,6, /* … */], [6,7,9, /* … */]] },
      { "key": "StrSkill120", "values": [[1,1,1,1,2, /* … */]] }
    ],
    "Details":   [ { "key": "StrSkill3", "values": [[2.5]] } ],
    "Synergies": [ { "key": "Firedplev", "args": ["skillname47"], "values": [[14]] } ]
  }
}
```

Each line keeps the `KeyedLine` contract (camel-cased `key`/`args`, resolved against
`strings/<lang>.json`) with these additions:

- `values` — one table per numeric argument, indexed by `level - 1`, because the same line
  reads differently at every skill level. Numbers are still finalized here; the client only
  picks an index. The exporter drops each table's constant tail, so **consumers must clamp
  the index to the last element**. Written on one line by `LevelTableConverter` — do not
  remove it, the tables are ~300k numbers across the export.
- `values2d` — the same thing indexed `[hardPoints][bonusLevels]`, emitted **only** for the
  lines whose formula actually reads `blvl`. See below.
- `valuesUlvl` — indexed `[skillLevel - 1][characterLevel - 1]`, emitted **only** for the
  lines whose formula reads `ulvl`. See below.
- `pluralKey` — the `desctextB` plural template for the description functions that ship a
  singular/plural pair. The client picks it whenever the rendered value is not exactly 1.
- `scale` / `scaleCalc` — which `*SymPerCalc` damage percentage applies. See below.

Layout lives in `Config/skill-desc-functions.json` (description function id → `layout` +
`plural`/`nameArg` flags); the arithmetic lives in `Import/SkillCalculator.cs`. Reach for
the config first — a new description function is data, not code.

### Hard points are not soft points (`values2d`)

`skillcalc.txt` is explicit: `lvl` is "the current Skill Level (with bonuses)", `blvl` is
"the base Skill Level (without bonuses)". Reimagined uses the difference as a designed
mechanic — `Fire Mastery`'s `blvl*par1 + min((lvl-blvl)*par2, par3)`,
`Titans Covenant`'s `par3 + blvl + ((lvl-blvl)/par4)`, `Valkyrie`'s `min(blvl+lvl, 85)` —
so a single table indexed by effective level overstates every geared character.

`CalcLevels` carries both numbers and records whether an evaluation read `blvl`;
`SkillDescriptionImporter` solves the flat table first and pays for the grid only when that
flag came back set (a small minority of lines, so the bundle grows modestly).

- First axis is **hard points**, `0..HardPointCap`. Row `0` is kept on purpose: an oskill
  granted purely by an item has no hard points and the game renders it as all-soft.
- Second axis is **bonus levels** from gear, `0..(MaxLevel - hardPoints)` for each row. Rows may have different lengths.
- Both axes drop their constant tail, so **clamp both indices**.
- `values` is still written for the same line, so an older consumer keeps rendering (it
  reads as "every level is a hard point"). Prefer `values2d` when present.

Don't collapse the two levels back together in `SkillCalcContext`: a single
effective-level table overstates every geared character.

### How far the tables run

A character can spend at most `maxlvl` hard points (25 in Reimagined), but `+skills` gear
pushes the *effective* level far past that, so the flat tables are solved to an absolute
ceiling — `export-config.json` → `skillDescriptionMaxLevel`, currently **100** — rather
than to "maxlvl plus some headroom". That ceiling is what `Descriptions.MaxLevel` reports
and what the `values2d` bonus axis is sized from.

### Character level is a third axis (`valuesUlvl`)

`ulvl` is "the caster unit's current Level" — neither skill-level axis can stand in for it.
`Thorns`' `aurastatcalc1 = edmn * (ulvl + 100)/100` is the canonical description line that
reaches it. `ulvl` cells in summon `calc2` columns set a minion's level and are not read by
a desccalc, so the axis is emitted only for the few lines that need it.

- `CalcLevels.ReadCharacterLevel()` flags an evaluation the same way `blvl` does;
  `SkillDescriptionImporter.BuildCharacterLevelGrid` only pays for the grid when it fires.
- Indexed `[skillLevel - 1][characterLevel - 1]`, second axis `1..skillDescriptionCharacterLevels`.
- Both axes drop their constant tail — **clamp both indices**.
- `values` is still written for the same line, solved at the **maximum** character level.
  Prefer `valuesUlvl` when present.

### Difficulty is fixed, not dropped

`cond('Difficulty', normal|nightmare|hell)` (Raise Skeleton / Raise Skeletal Mage minion
damage) resolves to `1` for the branch named by `export-config.json` →
`skillDescriptionDifficulty` and `0` for the other two — exactly what the game substitutes
on that difficulty. The default is `hell` because the site shows end-game numbers.

`Difficulty` is the only one of BBE's eight conditions a static export can answer — the
other seven interrogate a live unit. `CalcExpression.Conditions` lists all eight, and
`CalcExpression.TryResolveCondition` is shared by both scopes (the Data Guide specifies a
missile's conditions are checked against its source unit, i.e. the caster, so the two
cannot legitimately disagree), so an unmodelled condition and a misspelled one report
differently.

### A missile can carry the damage percentage (`scaleCalc`)

`scale` (`"physical"` / `"elemental"` / `"elementalLength"`) names which of the skill's own
`DmgSymPerCalc` / `EDmgSymPerCalc` / `ELenSymPerCalc` expressions the client evaluates
against live ranks before rendering the solved values — that is how synergies stay correct
without the exporter inventing an allocation.

Most of Reimagined's headline spells deal their damage through a **missile**, and the
percentage then lives on the `missiles.txt` row instead (`royalstrikemeteor` →
`skill('Fists of Fire'.blvl) * 10`, `arcticblast_*` → `skill('Summon Spirit Wolf'.blvl)`,
`meteorfire` → `skill('Inferno'.blvl)*3`). Nothing else on the wire carries those. When a
line's damage comes from a missile — through the `m1en`/`m2ex`/`me3y` family (the skilldesc
row's `descmissile1..3`) or an explicit `miss('name'.edmn)` — the exporter attaches that
missile's expression as `scaleCalc`, same grammar and same client-side evaluation.
`scaleCalc` also covers safely bound percentages from referenced skill rows, as described
in [Referenced percentage contexts](#referenced-percentage-contexts).

### The grammar is BBE, and it is documented

Calc cells are the game's own **BBE** language ("Binary Bytecode Evaluator", nobody is
sure). The authority is the Data Guide's `dataguide/docs/bbe-calc.html`, shipped in the
CASC dump — read it before inferring anything about a function or operator.
`Import/CalcExpression.cs` implements it: `+ - * / % ^`, the ternary `?:` and the six
comparisons (with `0` false and non-zero true), `min`/`max`/`rand`, and the quoted
references `skill` / `sklvl` / `sksrc` / `miss` / `stat` / `cond`. A bare identifier belongs
to the scope of the file the cell lives in, which is why skills and missiles need separate
vocabularies.

Two of those have semantics worth not re-deriving:

- **`sksrc` is a real function, not a typo for `skill`.** It reads a skill on the *source*
  unit, which for skill, missile, item and treasure-class scopes is the same unit `skill`
  itself uses — only monster-scope BBE, which this exporter never evaluates, is handed a
  different one. `CalcExpression.ReferenceAliases` therefore maps it onto `skill`, and that
  is exact rather than an approximation.
- **`sklvl('Skill'.identifierToGetLevel.identifier)`'s middle field is evaluated in the
  *calling* scope** ("must be an identifier of the base BBE scope"), not the target's. When
  it is `lvl`, as in the Reimagined data, solving the referenced skill at the describing
  skill's own levels is what the game does — see `SkillCalculator.TryResolveMinionSkill`.

`%`, `^` and `rand` are implemented so the parser matches the documented language rather
than only the current data. `rand` deliberately *fails* — it is rolled per cast, so there
is no single number to export.

### The calc symbol vocabulary is data, not code

`skillcalc.txt` and `misscalc.txt` are the game's own two-column reference for every calc
code, and `Import/CalcCodeTable.cs` reads them instead of transcribing them. A row's
`*Description` column comes in three shapes:

| Shape | Example | Handled by |
|---|---|---|
| a formula over other codes | `ln12` → `par1 + (lvl - 1) * par2` | evaluated by the same parser |
| a pointer at a source column | `clc0` → `The calc10 field value` | read off the row |
| prose naming a derived quantity | `edmn` → `Elemental Damage Min` | the scope's `TryResolveIntrinsic` |

**Do not transcribe the first two shapes into C#.** The spellings are not guessable —
`clc0` is `calc10` (not `calc0`), `ps10`–`ps14` break the `pstN` pattern, and
`ln91`/`ln21`/`ln43`/`ln65`/`ln87`/`ln92` address Param9–Param20 rather than the digits in
their names. Both files are bundled under `Config/CASC_DATA/`; the mod's own copy under
`<excel>/` wins when present, so a mod that extends the vocabulary needs no code change.

`Import/CalcExpression.cs` owns the shared grammar and the `ICalcScope` abstraction;
`SkillCalcContext` (skills.txt) and `MissileCalcContext` (missiles.txt) supply only the
prose-shaped quantities. A new *prose* symbol is code; a new formula or column alias is data.

**When the printed formula is not what the engine computes.** The diminishing-returns
family is spelled as one combined division (`dm34 = ((110 * lvl) * (par4 - par3)) / (100 *
(lvl + 6)) + par3`), which states the *result*; the game truncates `(110*lvl)/(lvl+6)` to an
integer percentage first and only then applies it. Vanilla Fanaticism's published table
(`14 18 20 23 …`) settles it — the combined form misses levels 3 and 12. The correction is
`export-config.json` → `calcFormulaOverrides`, applied by `CalcCodeTable.LoadAsync` as it
builds `Formulas`; it covers `dm12/34/56/78/91/21/43/65/87/92` plus misscalc's `sd12`,
`sd34`, `cd12`, `cd34`, `shd1`, `chd1`, `dd12`. Only codes the table already defines as
formulas are replaced, so an override cannot invent vocabulary, and there is no clamp at
`max` — the mod's hand-written `Berserk` cell adds its own explicit `min()` cap, which
would be redundant if `dm*` clamped. Do **not** instead edit `Config/CASC_DATA/skillcalc.txt`
(upstream data, and the mod ships its own copy that wins anyway) or hardcode a
"diminishing" helper.

### Documented limits and the isolated preview

`SkillCalculator` drops a line rather than guessing, and
`extras/import-report.txt` lists every drop **with its root cause**. `CalcTrace`
(in `Import/CalcExpression.cs`) records the deepest failure and each enclosing
expansion prepends itself as the stack unwinds, because the outermost expression is
usually an alias that resolved fine — `ast1` is just `skillcalc.txt`'s name for
`aurastatcalc1`, and the real blocker lies inside that column. When a
warning looks like it names a symbol the tables already define, read the tail of the
message, not the head. A calculation warning against the Reimagined mod is a regression or a
new mod data gap, not an expected drop. What the evaluator cannot resolve, should a mod
reach for it:

- `sctk` (the channeling tick) — derived from cast rate, which the export does not model.
- `rand(a,b)` — rolled at run time.
- the seven `cond` conditions that interrogate a live unit.

`ulvl`, `cond('Difficulty', …)` and minion-level `sklvl` resolve as described above;
`mps` resolves as `(usmc*25)/256` — the 256ths cost drained once per frame at 25 frames
per second.

The preview allocates points to **exactly one skill** — the one being described — and wears
no gear, which is what the game shows a character with no other points. So another skill's
level (`skill('Fire Ball'.blvl)`, i.e. synergy terms), gear-accumulated stats
(`stat('extra_skelewarriors'.accr)`) and the mastery aggregates (`mael`, `madm`, `math`,
`macr`, `manc`, `mapi`, `mair`) are all **0**. The synergy list is exported alongside
precisely so the website can show which skills feed this one without the exporter inventing
an allocation.

**"One skill allocated" is not "every cross-skill term is zero."** A reference to another
skill's *expression* column — `skill('Firestorm'.clc5)`, `petmax = skill('Demonic
Mastery'.clc1)` — is solved against that row at **level 0**, not short-circuited to zero,
by `SkillCalcContext.TryResolveUnallocatedSkill`. Reimagined's synergy columns count the
referencing skill among their own operands (`Raven.calc5` and `Firestorm.calc5` each sum ten
`skill('…'.blvl)` terms, their own included), so cells are written as
`skill('Raven'.clc5) - blvl` and are only correct once that self term survives to cancel the
subtraction. Short-circuiting such references to zero leaves the bare `- blvl` behind as a
*penalty* (negative synergy caps, bonuses that shrink as the skill levels, flat
durations). Inside the nested scope every bare
`lvl`/`blvl` is 0, a reference back to the described skill reads its real level (that is
what the `Anchor` chain is for), and any further skill recurses into the same rule; the mod's
own `(lvl>=1)?(…):0` guards on `Shape Shifting`'s passive columns are written for exactly
this substitution. An empty column or a reference cycle falls back to the flat zero of
`CalcExpression.TryResolveIsolatedSkill`, so a term is lost rather than the whole line.
`Param` columns still read straight through — they do not scale.

A missile cell naming **its own caster** is not one of those: `MissileCalcContext.Owner`
links back to the `SkillCalcContext` that spawned it, so `moltenboulderfirepath`'s
`skill('Molten Boulder'.blvl)` reads the real level. Chained `miss('name'.field)` references
share one recursion guard.

A line can also drop because the mod leaves the column it reads empty or misspells a symbol.
A calc cell is evaluated as a unit, so one bad token takes the whole line down even when the
rest of the cell is valid. Those belong back with the mod authors — the exporter is
correctly refusing to invent a number, and `CalcTrace` names the offending token so the
report can be forwarded as-is.


## Referenced percentage contexts

The numeric tables and live percentage metadata have different responsibilities.
`ApplyDamageScale` follows the shared evaluator's observed intrinsic reads through
table aliases and quoted references. Same-row damage uses `scale`; missile damage
uses the missile's actual percentage as `scaleCalc`. Referenced skill damage uses
that row's percentage only if `TryBindParameters` can safely bind its bare Param
aliases to source values. Quoted synergy references remain live. Do not substitute
the describing skill's Params or silently approximate another row's quantities.

Examples of description lines reaching a different skill's non-empty percentage:

| Description | Referenced source | Emitted percentage |
|---|---|---|
| FireGolem Stats / `StrSkillGolemFireDamageRange` | FireGolemAura elemental damage | `skill('Golem Mastery'.lvl)*skill('FireGolem'.par8)` |
| Summon Tainted Stats / `StrSkill5` | Tainted Fire Ball elemental damage | `skill('Blood Boil'.blvl)*(10)` (source Param8) |

Cleansing/Meditation's referenced healing has no damage percentage and
intentionally receives none.

A referenced percentage with bare `lvl`, `blvl`, `ulvl`, `clcN`,
derived damage or formula aliases (rather than bare Param aliases) is still
declined. The numeric description line is retained but that percentage metadata
is omitted. Binding records a `CalcTrace` reason; `ApplyDamageScale` currently
does not forward it to the import report. This is an inactive diagnostics gap,
not a claim that unsupported metadata is reported. If such data appears, report
the skill/description/source expression and add regression coverage before
changing this behavior.

A safe extension would need source-scoped expression expansion or an explicit
source-context wire contract and matching website support. Replacing symbols
with values from the describing skill is incorrect. Do not add that architecture
until the data actually reaches such a context.

Consumer requirements for these fields (axis selection, live rank maps and BBE
subset re-checks) are in
[WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#skill-data-contract).
