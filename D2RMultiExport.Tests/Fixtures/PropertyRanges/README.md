# Property range source snapshots

These small TSV snapshots were copied from d2r-reimagined-mod commit
3ad48c83bee64a61e9d504ed989f38f0bbd70326 on 2026-10-09. The source
working tree was clean. Original game-data files were read only; headers and
selected cells are retained, including duplicate names and empty endpoints.

| Table | Selected rows | SHA-256 of complete source table |
|---|---:|---|
| automagic.txt | 1 | 87b9fa64690fc0658a5a55d2699616828d209fd338987f73d35da5a7bb222608 |
| cubemain.txt | 2 | b6ddc927e9568a3b94d051b38a6851ac6feb87cb49ad7263092e4606c8b35190 |
| magicprefix.txt | 3 | 12db607302abce022af636ed42a895aa22c9b66eff72cfc295304740c1de0a01 |
| magicsuffix.txt | 5 | 4c006ab6c28a6c4e7ad486db2ff824f6baba1d0a64603bc1514a6ea86cf20a07 |
| runes.txt | 4 | c74372b94cf189ffcff77e7ee0c3b0d5c478c6eb67e44bc9dd5b9eb29e36ba39 |
| setitems.txt | 3 | d08549f0af44d80d2bc35b42b124ced6b781572b05c32f9cac9ade29bdae534c |
| sets.txt | 2 | 07f458b43cd37b5a8efe1a35058c1218617b2e3cbd5f7c31614be485dae05cba |
| uniqueitems.txt | 6 | bb3cc1a7fddc9569d5c73f9fabad2082aacb1758478e531b7cf74faacb362562 |

Selections: Hone Sundan, Griswold’s Edge, Rakescar, Brainhew, Steelgoad,
Headstriker; Civerb’s Ward/Cudgel and Cathan’s Rule/Traps; Last Wish, Leaf,
Fortitude; Mechanist’s, Snowflake, of the Glacier, of the Jackal; and armor,
shield and weapon socket recipes.

PropertyRangeTests.RealSourceSnapshotsAreParsedImportedAndSerializedWithTheirRanges
uses the published typed parsers, actual importers and keyed JSON exporter.
It seeds focused property/stat/base-item lookups for isolation. The fixture
automagic host is deliberately supplied as a weapon; this tests the supported
bucket path, not native placement of that affix in the full mod export. Full
source loading, real equipment and rune contributions are checked separately
by the smoke export and independent source audit in docs/PROPERTY-RANGES.md.

`data/local/lng/strings/` holds only a marker README so the fixture is a
supported mod layout; the test reads only the excel tables, not translations.
