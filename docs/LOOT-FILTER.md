# UnHoarder loot-filter catalog

The export produces `keyed/loot-filter.json` (SchemaVersion 1, FilterVersion 3)
using the existing typed parsers. No TXT files are needed by the website builder.
It requires `weapons`, `armor`, `misc`, `itemtypes`, `uniqueitems` and `setitems`
`.txt` (mod copy, else CASC copy); if any is missing the step is skipped and
reported like other optional outputs.

- `BaseItems`: Kind, Code, NameKey, BaseNameSelector, TypeCode, TypeCode2.
- `ItemTypes`: Code, TypeNameSelector, ParentCode, ParentCode2.
- `UniqueItems` and `SetItems`: NameKey and base Code; editor search enrichment only.

NameKey is localized through the regular strings bundles. BaseNameSelector is the
exact weapons/armor `name` field (empty for misc). TypeNameSelector is the exact
`ItemType` field. These are protocol identifiers, not translated display text.
They preserve whitespace and case because UnHoarder resolves literal names.
Type codes preserve the original graph, including h2h2; catalog-only normalization
and exclusions must not change the plugin's lookup semantics. Codes strip only
trailing space padding; base, unique and set rows whose code is not 1–4
printable ASCII characters are omitted. Both type parents and each base's type2
are included.

Legacy rows without translations receive source-derived English name fallbacks
through the existing synthetic merge. Mod/CASC text is never replaced by them.

The JSON is exported with the normal language bundles and listed in the run
manifest ([WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#manifest-schema-2-and-output-integrity)).
Type inheritance is not pre-expanded; consumers walk `ParentCode`/`ParentCode2`.
Unique/set entries carry only `NameKey` and the base `Code`, so they can select a
base and rarity but cannot identify a specific item.

Validate with the standard export in [TESTING.md](TESTING.md); supply
`--casc-data` so tables the mod does not ship fall back to the CASC copy.
