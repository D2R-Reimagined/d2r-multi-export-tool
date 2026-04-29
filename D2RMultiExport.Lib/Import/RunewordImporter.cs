// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Import;

public sealed class RunewordImporter
{
    private readonly GameData _data;
    private readonly string _excelPath;

    public RunewordImporter(string excelPath, GameData data)
    {
        _excelPath = excelPath;
        _data = data;
    }

    public async Task<ImportResult<RunewordExport>> ImportAsync()
    {
        var result = new ImportResult<RunewordExport>();
        
        IList<D2RReimaginedTools.Models.RuneWord> rawEntries;
        // Vanilla detection: a runeword is "vanilla" when its `*Rune Name`
        // (or `*RunesUsed`) token matches a row in the canonical CASC
        // `runes.txt` whose `complete` column is `1`. This mirrors the
        // doc-generator's behaviour exactly and replaces a previous
        // best-effort row-count heuristic. The canonical file is bundled
        // under `Config/CASC_DATA/runes.txt` and parsed with the same
        // schema as the live mod's `Runes.txt`.
        var canonicalRuneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            rawEntries = await RunesParser.GetEntries(Path.Combine(_excelPath, "Runes.txt"));

            var canonicalPath = ResolveCanonicalRunesPath();
            if (File.Exists(canonicalPath))
            {
                var canonicalRows = await RunesParser.GetEntries(canonicalPath);
                foreach (var crow in canonicalRows)
                {
                    if (crow.Complete != 1) continue;
                    var token = !string.IsNullOrWhiteSpace(crow.RuneName)
                        ? crow.RuneName
                        : crow.RunesUsed;
                    if (!string.IsNullOrWhiteSpace(token))
                        canonicalRuneNames.Add(token.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            result.AddError("Runeword", "Runes.txt", $"Failed to load file: {ex.Message}", ex);
            return result;
        }

        foreach (var entry in rawEntries)
        {
            var name = entry.Name ?? "";
            if (string.IsNullOrEmpty(entry.Rune1)) continue;

            try
            {
                // Add the runes and calculate base requirements
                var runes = new List<MiscEntry>();
                for (int i = 1; i <= 6; i++)
                {
                    var runeCode = ImportReflection.GetString(entry, $"Rune{i}");
                    if (!string.IsNullOrEmpty(runeCode) && !runeCode.StartsWith("*"))
                    {
                        if (_data.MiscItems.TryGetValue(runeCode, out var misc)) runes.Add(misc);
                    }
                }

                if (runes.Count == 0) continue;

                var itemLevel = runes.Max(x => x.Level);
                var baseReqLevel = runes.Max(x => x.LevelReq);

                // Add the types
                var types = new List<ItemTypeEntry>();
                for (int i = 1; i <= 6; i++)
                {
                    var typeCode = ImportReflection.GetString(entry, $"ItemType{i}");
                    if (!string.IsNullOrEmpty(typeCode) && !typeCode.StartsWith("*"))
                    {
                        if (_data.ItemTypes.TryGetValue(typeCode, out var it)) types.Add(it);
                    }
                }

                var typeCount = CountMajorTypes(types);

                // Runes.txt columns 0/1: "Name" (col 0, used as translation key —
                // e.g. "Hysteria", "Runeword180") and "*Rune Name" (col 1, a
                // free-form comment, e.g. "Hustle (armor)", "Coven"; note the
                // leading "*" marks the column as not exported by the game).
                // Only the Name column should be probed against the translation
                // table; the comment column is for human reference and must never
                // be passed to GetValue (otherwise it pollutes missing-translations
                // with phantom entries like "Coven" / "Hustle (armor)").
                var runewordKey = name; // entry.Name (col 0) — the real translation key
                var runewordComment = entry.RuneName ?? ""; // col 1 — display fallback only
                var runewordName = !string.IsNullOrEmpty(runewordKey)
                    ? _data.Translations.GetValue(runewordKey)
                    : runewordComment;

                // Compare on the same `*Rune Name` / `*RunesUsed` token the canonical
                // file is keyed by, NOT on the column-0 translation key (which never
                // appears in canonical since each canonical row is also `Runeword<n>`).
                var vanillaToken = !string.IsNullOrWhiteSpace(entry.RuneName)
                    ? entry.RuneName
                    : entry.RunesUsed;
                bool isVanilla = !string.IsNullOrWhiteSpace(vanillaToken)
                                 && canonicalRuneNames.Contains(vanillaToken.Trim());

                var export = new RunewordExport
                {
                    Name = runewordName,
                    Index = name,
                    RuneName = runewordComment,
                    Vanilla = isVanilla ? "Y" : "N",
                    ItemLevel = itemLevel,
                    RequiredLevel = baseReqLevel,
                    Runes = runes.Select(r => new RuneExport
                    {
                        // RuneExport.Name carries the *translation key* (Misc.txt
                        // namestr — e.g. "r15"), not the resolved english value.
                        // The keyed exporter ships this verbatim as
                        // `RuneRef.NameKey`, and the website resolves it via
                        // `t(rune.NameKey)` against `strings/<lang>.json`.
                        // Resolving here (the previous behavior) emitted english
                        // strings like "Hel Rune (#15)" as the "key", which then
                        // failed every lookup and degraded to that english text
                        // on every language. Fall back to the rune Code (e.g.
                        // "r15") if NameStr is unexpectedly empty.
                        Name = !string.IsNullOrEmpty(r.NameStr)
                            ? r.NameStr
                            : r.Code,
                        ItemLevel = r.Level,
                        RequiredLevel = r.LevelReq,
                        // Index uses the canonical ${code}itype suffix so the website
                        // resolves it the same way as every other itemtype label.
                        Type = new ItemTypeExport { Name = "Rune", Index = "runeitype" }
                    }).ToList(),
                    Types = types.Select(t => new ItemTypeExport
                    {
                        Name = t.Name,
                        Index = t.Index,
                        Class = t.Class ?? ""
                    }).ToList()
                };

                // 1. Add direct properties from Runes.txt
                if (entry.Mods != null)
                {
                    for (int i = 0; i < entry.Mods.Count; i++)
                    {
                        var mod = entry.Mods[i];
                        if (string.IsNullOrEmpty(mod.Code)) continue;
                        if (PropertyMapper.IsIgnored(mod.Code, _data.ExportConfig)) continue;
                        var resolved = PropertyMapper.Map(mod.Code, mod.Param, mod.Min, mod.Max, _data, itemLevel);
                        export.Properties.Add(new CubePropertyExport
                        {
                            Index = i,
                            PropertyCode = mod.Code,
                            Priority = resolved.Priority,
                            Min = mod.Min,
                            Max = mod.Max,
                            Parameter = mod.Param,
                            IsEase = mod.Code == "ease",
                            Lines = resolved.Lines
                        });
                    }
                }

                // 2. Add properties from constituent runes (Gem.txt).
                //
                // Determine which scopes (weapon / shield / armor) the runeword's
                // host items cover by walking each ItemType's full Equiv1/Equiv2
                // chain. Concrete itemtypes such as "swor", "axe", "mace", "h2h"
                // do not carry BodyLoc1 == "rarm" themselves — their parent in
                // the equiv chain ("mele" → "weap") does. The previous code only
                // checked the type's own fields and therefore misclassified those
                // weapon subtypes as "armor", leaking gem.HelmProperties into
                // weapon-only runewords like Steel.
                bool hasShieldType = types.Any(IsShieldType);
                bool hasWeaponType = types.Any(IsWeaponType);
                bool hasArmorType  = types.Any(t => !IsShieldType(t) && !IsWeaponType(t));

                foreach (var rune in runes)
                {
                    if (!_data.Gems.TryGetValue(rune.Code, out var gem)) continue;

                    if (hasShieldType)
                        AddGemProperties(export.Properties, gem.ShieldProperties, " (Shield)", typeCount > 1, itemLevel);
                    if (hasWeaponType)
                        AddGemProperties(export.Properties, gem.WeaponProperties, " (Weapon)", typeCount > 1, itemLevel);
                    if (hasArmorType)
                        AddGemProperties(export.Properties, gem.HelmProperties, " (Armor)", typeCount > 1, itemLevel);
                }

                // Cleanup duplicates and sort to match old tool:
                // base props by Priority desc, then Weapon/Armor/Shield/Other groups each sorted by PropertyString
                export.Properties = PropertyCleanup.CleanupDuplicates(
                    export.Properties,
                    _data,
                    itemLevel,
                    PropertyCleanup.RunewordSuffixOrder);

                export.RequiredLevel = RequirementHelper.ComputeAdjustedRequiredLevel(_data, "Runeword", name, export.RequiredLevel, export.Properties);

                result.AddItem(export);
            }
            catch (Exception ex)
            {
                result.AddError("Runeword", name, $"Failed to process: {ex.Message}", ex);
            }
        }

        // Sort items by RequiredLevel to match old tool output
        result.Items = result.Items.OrderBy(x => x.RequiredLevel).ToList();
        
        return result;
    }

    private void AddGemProperties(List<CubePropertyExport> target, List<ItemPropertyValue> source, string suffix, bool addSuffix, int itemLevel)
    {
        // Map the legacy English suffix " (Weapon)/(Shield)/(Armor)" to the
        // canonical lowercase scope code consumed by KeyedLine.Qualifier.
        string? qualifier = null;
        if (addSuffix)
        {
            qualifier = suffix.Trim() switch
            {
                "(Weapon)" => "weapon",
                "(Shield)" => "shield",
                "(Armor)"  => "armor",
                _          => null
            };
        }

        foreach (var gp in source)
        {
            if (string.IsNullOrEmpty(gp.Code)) continue;
            if (PropertyMapper.IsIgnored(gp.Code, _data.ExportConfig)) continue;
            var prop = PropertyMapper.Map(gp.Code, gp.Parameter, gp.Min, gp.Max, _data, itemLevel);
            var actualSuffix = addSuffix ? suffix.Trim() : "";

            // Stamp the scope qualifier on every keyed line emitted by this
            // gem-property contribution so the website can render the
            // "(Weapon)/(Shield)/(Armor)" tail in the active language.
            // Lines are cloned to avoid mutating shared instances cached
            // upstream by PropertyMapper.
            var taggedLines = prop.Lines;
            if (qualifier != null && taggedLines.Count > 0)
            {
                taggedLines = taggedLines.Select(l => new KeyedLine
                {
                    Key = l.Key,
                    Args = l.Args,
                    PerLevel = l.PerLevel,
                    Priority = l.Priority,
                    Qualifier = qualifier
                }).ToList();
            }

            target.Add(new CubePropertyExport
            {
                Index = target.Count,
                PropertyCode = gp.Code,
                Priority = prop.Priority,
                Min = gp.Min,
                Max = gp.Max,
                Parameter = gp.Parameter,
                IsEase = gp.Code == "ease",
                Suffix = actualSuffix,
                Lines = taggedLines
            });
        }
    }

    private int CountMajorTypes(List<ItemTypeEntry> types)
    {
        bool shield = false, weapon = false, armor = false;
        int count = 0;
        foreach (var type in types)
        {
            if (IsShieldType(type)) { if (!shield) count++; shield = true; }
            else if (IsWeaponType(type)) { if (!weapon) count++; weapon = true; }
            else { if (!armor) count++; armor = true; }
        }
        return count;
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied ItemType is (directly or via its
    /// Equiv1/Equiv2 chain) the shield class. Walking the chain is required
    /// because most concrete shield subtypes carry <c>Equiv1 = "shld"</c>
    /// rather than <c>Code = "shld"</c>.
    /// </summary>
    private bool IsShieldType(ItemTypeEntry type) => HasEquivAncestor(type, "shld");

    /// <summary>
    /// Returns <c>true</c> when the supplied ItemType is (directly or via its
    /// Equiv1/Equiv2 chain) a weapon. Concrete subtypes like <c>swor</c>,
    /// <c>axe</c>, <c>mace</c>, <c>h2h</c>, etc. inherit from <c>weap</c>
    /// through one or two levels of equiv parents (e.g. <c>swor → mele → weap</c>),
    /// which is why a flat field check on the leaf type is not sufficient.
    /// </summary>
    private bool IsWeaponType(ItemTypeEntry type) => HasEquivAncestor(type, "weap");

    private bool HasEquivAncestor(ItemTypeEntry type, string targetCode)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ItemTypeEntry>();
        queue.Enqueue(type);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Code == null || !visited.Add(current.Code)) continue;
            if (string.Equals(current.Code, targetCode, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(current.Equiv1) &&
                _data.ItemTypes.TryGetValue(current.Equiv1, out var p1))
            {
                queue.Enqueue(p1);
            }
            if (!string.IsNullOrEmpty(current.Equiv2) &&
                _data.ItemTypes.TryGetValue(current.Equiv2, out var p2))
            {
                queue.Enqueue(p2);
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the bundled canonical <c>runes.txt</c> shipped under
    /// <c>D2RMultiExport.Lib/Config/CASC_DATA/</c>. Resolution prefers the
    /// copy next to the running assembly (covers the standard
    /// <c>copyToOutputDirectory</c> deployment) and falls back to walking up
    /// from the current directory for development scenarios where the
    /// assembly path is not the same as the project root.
    /// </summary>
    private static string ResolveCanonicalRunesPath()
    {
        const string relative = "Config/CASC_DATA/runes.txt";
        var asmDir = AppContext.BaseDirectory;
        var fromAssembly = Path.Combine(asmDir, relative);
        if (File.Exists(fromAssembly)) return fromAssembly;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "D2RMultiExport.Lib", "Config", "CASC_DATA", "runes.txt");
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
}
