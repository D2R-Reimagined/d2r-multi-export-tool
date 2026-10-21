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
trailing space padding. Both type parents and each base's type2 are included.

Legacy rows without translations receive source-derived English name fallbacks
through the existing synthetic merge. Mod/CASC text is never replaced by them.

Publish the JSON with the normal language bundles through the staging command
([WEBSITE-INTEGRATION.md](WEBSITE-INTEGRATION.md#manifest-schema-2-and-publishing)).
The website builder expands type inheritance and emits supported v3 conditions.
Named unique/set helpers select base + rarity and cannot identify a specific item.

Validate with the standard export in [TESTING.md](TESTING.md); supply
`--casc-data` so tables the mod does not ship fall back to the CASC copy.
