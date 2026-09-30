# UnHoarder loot-filter catalog

Every export now produces `keyed/loot-filter.json` (SchemaVersion 1, FilterVersion 3)
using the existing typed parsers. No TXT files are needed by the website builder.

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

Copy the generated JSON and normal language bundles to the website's static/data
tree. The builder expands type inheritance and emits supported v3 conditions.
Named unique/set helpers select base + rarity and cannot identify a specific item.

Run the normal end-to-end smoke export in TESTING.md. Supply an Excel directory
containing extracted base tables overlaid by the mod: a mod-only directory may omit
vanilla tables required by other exporters (for example rareprefix/raresuffix).
Keep the corresponding mod HD asset directories available for sprite exporters.
