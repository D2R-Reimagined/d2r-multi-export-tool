// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib;

/// <summary>Which root a resolved source file came from.</summary>
public enum SourceOrigin
{
    /// <summary>No root supplied the file.</summary>
    None,
    /// <summary>The mod's own <c>data</c> tree (always wins).</summary>
    Mod,
    /// <summary>The extracted vanilla CASC <c>data</c> tree (fallback beneath the mod).</summary>
    Casc,
    /// <summary>A reference copy bundled with the tool under <c>Config/CASC_DATA/</c>.</summary>
    Bundled
}

/// <summary>
/// One source lookup: the root-relative path that was asked for, the file that answered it
/// (or <c>null</c>), the root it came from, and every absolute location that was checked —
/// enough for a diagnostic to say exactly where it looked and why it failed.
/// </summary>
public sealed record ResolvedSource(string RelativePath, string? Path, SourceOrigin Origin, IReadOnlyList<string> Searched)
{
    public bool Exists => Path is not null;

    /// <summary>The first location searched, used as the expected path in messages.</summary>
    public string Expected => Searched.Count > 0 ? Searched[0] : RelativePath;

    public string DescribeSearch() => string.Join(" ; ", Searched);
}

/// <summary>Outcome of validating a user-selected source directory.</summary>
/// <param name="IsValid">Whether one of the supported layouts was recognised.</param>
/// <param name="Selected">The directory the user selected (full path).</param>
/// <param name="Root">The normalized data root, when valid.</param>
/// <param name="Message">A human-readable, actionable diagnostic.</param>
/// <param name="Notes">Non-fatal observations (e.g. a component the layout lacks).</param>
public sealed record SourceRootValidation(bool IsValid, string Selected, string? Root, string Message, IReadOnlyList<string> Notes);

/// <summary>Thrown when a selected source directory matches none of the supported layouts.</summary>
public sealed class SourceLayoutException(string message) : DirectoryNotFoundException(message);

/// <summary>
/// The single authority for where the exporter reads its inputs. Configured from exactly two
/// user-selected directories — the mod's <c>data</c> root and the extracted vanilla CASC
/// <c>data</c> root — whose supported layouts are recognised once, here. Every importer and
/// exporter asks this class for a root-relative path (<c>global/excel/skills.txt</c>,
/// <c>hd/items/items.json</c>, …) and gets the mod's copy when it ships one, otherwise the
/// vanilla copy, which is the precedence the game itself applies to a mod.
/// <para>
/// No parent-directory walking and no recursive searching: a candidate layout is a fixed
/// relative suffix of the selected directory. Lookups are case-insensitive per path
/// segment (D2R data is lower-case on disk but referenced in mixed case, e.g.
/// <c>Runes.txt</c>), so behaviour is identical on case-sensitive file systems.
/// </para>
/// </summary>
public sealed class SourceRoots
{
    /// <summary>Relative locations, under the selected mod directory, that may hold the data root.</summary>
    public static readonly IReadOnlyList<string> ModLayouts = ["", "data"];

    /// <summary>
    /// Relative locations, under the selected CASC directory, that may hold the data root:
    /// the data root itself, a <c>data/</c> folder (selecting the extraction root) and
    /// CascView's nested <c>data/data/</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> CascLayouts = ["", "data", "data/data"];

    /// <summary>The mod data root must carry its excel tables and primary translations.</summary>
    private static readonly string[] ModMarkers = ["global/excel", "local/lng/strings"];

    /// <summary>A CASC data root is recognised by any of the components the exporter reads from it.</summary>
    private static readonly string[] CascMarkers = ["global/excel", "hd/global", "local/lng/strings"];

    public const string ExcelDirectory = "global/excel";
    public const string StringsDirectory = "local/lng/strings";

    private readonly Dictionary<string, Dictionary<string, string>?> _listings = new(StringComparer.Ordinal);
    private readonly Lock _listingsLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ResolvedSource> _lookups =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every tracked lookup of this run (data tables, manifests, string folders), keyed by
    /// relative path. Written to <c>extras/source-report.txt</c> so a run states exactly which
    /// root supplied each input. Per-sprite lookups are not tracked; their exporters report
    /// their own grouped diagnostics.
    /// </summary>
    public IReadOnlyCollection<ResolvedSource> Lookups => [.. _lookups.Values.OrderBy(static l => l.RelativePath, StringComparer.OrdinalIgnoreCase)];

    private SourceRoots(string modDataRoot, string? cascDataRoot, string? bundledDataRoot)
    {
        ModDataRoot = modDataRoot;
        CascDataRoot = cascDataRoot;
        BundledDataRoot = bundledDataRoot;
        ModExcelDirectory = ResolveDirectory(modDataRoot, ExcelDirectory)!;
        ModStringsDirectory = ResolveDirectory(modDataRoot, StringsDirectory)!;
    }

    /// <summary>The normalized mod <c>data</c> root (contains <c>global/excel</c>).</summary>
    public string ModDataRoot { get; }

    /// <summary>The normalized vanilla CASC <c>data</c> root, or <c>null</c> when none was configured.</summary>
    public string? CascDataRoot { get; }

    /// <summary>
    /// The tool's bundled reference tables (<c>Config/CASC_DATA/</c>), used only for files the
    /// tool ships itself (<c>skillcalc.txt</c>, <c>misscalc.txt</c>, canonical <c>runes.txt</c>).
    /// </summary>
    public string? BundledDataRoot { get; }

    public string ModExcelDirectory { get; }

    public string ModStringsDirectory { get; }

    /// <summary>The CASC strings directory when the CASC root has one; otherwise <c>null</c>.</summary>
    public string? CascStringsDirectory =>
        CascDataRoot is not null && ResolveDirectory(CascDataRoot, StringsDirectory) is { } dir ? dir : null;

    /// <summary>Data roots in ascending precedence (vanilla first, mod last).</summary>
    public IReadOnlyList<string> AssetRoots => CascDataRoot is null ? [ModDataRoot] : [CascDataRoot, ModDataRoot];

    /// <summary>
    /// Validates both selections and builds the resolver. Throws <see cref="SourceLayoutException"/>
    /// naming the selected directory, the expected relative location and what was found.
    /// </summary>
    public static SourceRoots Create(string modDataPath, string? cascDataPath, string? bundledDataRoot = null)
    {
        var mod = ValidateModRoot(modDataPath);
        if (!mod.IsValid) throw new SourceLayoutException(mod.Message);
        string? casc = null;
        if (!string.IsNullOrWhiteSpace(cascDataPath))
        {
            var validation = ValidateCascRoot(cascDataPath);
            if (!validation.IsValid) throw new SourceLayoutException(validation.Message);
            casc = validation.Root;
        }
        var bundled = string.IsNullOrWhiteSpace(bundledDataRoot) ? null : Path.GetFullPath(bundledDataRoot);
        return new SourceRoots(mod.Root!, casc, bundled);
    }

    public static SourceRootValidation ValidateModRoot(string? selected) =>
        Validate(selected, "Mod Data Directory", ModLayouts, ModMarkers, requireAll: true);

    public static SourceRootValidation ValidateCascRoot(string? selected) =>
        Validate(selected, "CASC Data Directory", CascLayouts, CascMarkers, requireAll: false);

    private static SourceRootValidation Validate(
        string? selected,
        string label,
        IReadOnlyList<string> layouts,
        IReadOnlyList<string> markers,
        bool requireAll)
    {
        if (string.IsNullOrWhiteSpace(selected))
            return new SourceRootValidation(false, "", null, $"{label}: no directory selected.", []);

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new SourceRootValidation(false, selected, null, $"{label}: '{selected}' is not a valid path ({ex.Message}).", []);
        }
        if (!Directory.Exists(full))
            return new SourceRootValidation(false, full, null, $"{label}: '{full}' does not exist.", []);

        foreach (var layout in layouts)
        {
            var candidate = layout.Length == 0 ? full : ResolveDirectory(full, layout);
            if (candidate is null) continue;
            var present = markers.Where(marker => ResolveDirectory(candidate, marker) is not null).ToList();
            var matches = requireAll ? present.Count == markers.Count : present.Count > 0;
            if (!matches) continue;

            var notes = markers.Except(present)
                .Select(marker => $"{label}: '{candidate}' has no '{marker}' folder; that component falls back or is unavailable.")
                .ToList();
            var layoutText = layout.Length == 0 ? "the selected directory" : $"'{layout}/' under the selected directory";
            return new SourceRootValidation(true, full, candidate,
                $"{label}: using '{candidate}' ({layoutText}).", notes);
        }

        string found;
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).Take(12).ToList();
            found = entries.Count == 0 ? "an empty directory" : string.Join(", ", entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = $"an unreadable directory ({ex.Message})";
        }
        var expected = string.Join(requireAll ? " and " : " or ", markers.Select(m => $"'{m}'"));
        var where = string.Join(", ", layouts.Select(l => l.Length == 0 ? "<selected>" : $"<selected>/{l}"));
        return new SourceRootValidation(false, full, null,
            $"{label}: '{full}' is not a supported layout. Expected {expected} under one of {where}; found {found}.", []);
    }

    /// <summary>A table under <c>global/excel</c>: the mod's copy, else the vanilla copy.</summary>
    public ResolvedSource ExcelSource(string fileName) => Resolve($"{ExcelDirectory}/{fileName}");

    /// <summary>
    /// Path of a table under <c>global/excel</c> (mod first, then CASC). When neither root has
    /// it, the mod location is returned so a parser's own error names the expected file.
    /// </summary>
    public string Excel(string fileName)
    {
        var source = ExcelSource(fileName);
        return source.Path ?? source.Expected;
    }

    /// <summary>
    /// Resolves a data-root-relative path: mod first, then CASC. <paramref name="track"/>
    /// records the lookup in <see cref="Lookups"/>.
    /// </summary>
    public ResolvedSource Resolve(string relativePath, bool track = true)
    {
        var searched = new List<string>(2);
        var result = new ResolvedSource(relativePath, null, SourceOrigin.None, searched);
        var mod = ResolveUnder(ModDataRoot, relativePath, searched);
        if (mod is not null)
        {
            result = result with { Path = mod, Origin = SourceOrigin.Mod };
        }
        else if (CascDataRoot is not null && ResolveUnder(CascDataRoot, relativePath, searched) is { } casc)
        {
            result = result with { Path = casc, Origin = SourceOrigin.Casc };
        }
        return track ? Track(result) : result;
    }

    private ResolvedSource Track(ResolvedSource source)
    {
        _lookups[source.RelativePath] = source;
        return source;
    }

    /// <summary>Resolves against one specific root only (no fallback).</summary>
    public ResolvedSource ResolveIn(SourceOrigin origin, string relativePath)
    {
        var root = origin switch
        {
            SourceOrigin.Mod => ModDataRoot,
            SourceOrigin.Casc => CascDataRoot,
            SourceOrigin.Bundled => BundledDataRoot,
            _ => null
        };
        var searched = new List<string>(1);
        var path = root is null ? null : ResolveUnder(root, relativePath, searched);
        return new ResolvedSource(relativePath, path, path is null ? SourceOrigin.None : origin, searched);
    }

    /// <summary>Records a directory a consumer reads as a whole (the string folders).</summary>
    public void TrackDirectory(string relativePath, string? directory, SourceOrigin origin)
    {
        Track(new ResolvedSource(relativePath + "/", directory, directory is null ? SourceOrigin.None : origin,
            [directory ?? relativePath]));
    }

    /// <summary>
    /// Resolves a reference table the tool also bundles: mod excel, then CASC excel, then the
    /// bundled copy.
    /// </summary>
    public ResolvedSource ResolveReferenceTable(string fileName)
    {
        var excel = ExcelSource(fileName);
        if (excel.Exists) return excel;
        var searched = new List<string>(excel.Searched);
        if (BundledDataRoot is not null)
        {
            var bundled = ResolveUnder(BundledDataRoot, fileName, searched);
            if (bundled is not null) return Track(excel with { Path = bundled, Origin = SourceOrigin.Bundled, Searched = searched });
        }
        return Track(excel with { Searched = searched });
    }

    /// <summary>One line per root, for the run log.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"Mod data root:  {ModDataRoot}";
        yield return CascDataRoot is null
            ? "CASC data root: (not configured — no vanilla fallback for files the mod does not ship)"
            : $"CASC data root: {CascDataRoot}";
    }

    private string? ResolveUnder(string root, string relativePath, List<string> searched)
    {
        var segments = SplitRelative(relativePath);
        searched.Add(Combine(root, string.Join('/', segments)));
        var exact = Path.Combine([root, .. segments]);
        if (!OperatingSystem.IsWindows() && File.Exists(exact)) return exact;

        // Case-insensitive walk, one cached directory listing per segment.
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var listing = GetListing(current);
            if (listing is null || !listing.TryGetValue(segments[i], out var actual)) return null;
            current = Path.Combine(current, actual);
        }
        return File.Exists(current) ? current : null;
    }

    private Dictionary<string, string>? GetListing(string directory)
    {
        lock (_listingsLock)
        {
            if (_listings.TryGetValue(directory, out var cached)) return cached;
            Dictionary<string, string>? listing = null;
            try
            {
                if (Directory.Exists(directory))
                {
                    listing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        listing.TryAdd(Path.GetFileName(entry), Path.GetFileName(entry));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                listing = null;
            }
            _listings[directory] = listing;
            return listing;
        }
    }

    /// <summary>Case-insensitive, fixed-depth directory resolution (no searching).</summary>
    private static string? ResolveDirectory(string root, string relativePath)
    {
        var current = root;
        foreach (var segment in SplitRelative(relativePath))
        {
            string? match = null;
            try
            {
                var directories = Directory.EnumerateDirectories(current).ToArray();
                match = directories.FirstOrDefault(d => string.Equals(Path.GetFileName(d), segment, StringComparison.Ordinal))
                    ?? directories.FirstOrDefault(d => string.Equals(Path.GetFileName(d), segment, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (match is null) return null;
            current = match;
        }
        return current;
    }

    /// <summary>
    /// Splits a root-relative path on either separator. Rooted paths and <c>..</c> segments are
    /// rejected: a source lookup may never leave its root.
    /// </summary>
    internal static string[] SplitRelative(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("A source path must not be empty.", nameof(relativePath));
        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\')
            || (relativePath.Length >= 2 && char.IsAsciiLetter(relativePath[0]) && relativePath[1] == ':'))
            throw new ArgumentException($"Source path '{relativePath}' must be relative to a data root.", nameof(relativePath));
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static s => s is ".." or "."))
            throw new ArgumentException($"Source path '{relativePath}' must not contain '.' or '..' segments.", nameof(relativePath));
        return segments;
    }

    private static string Combine(string root, string relativePath) =>
        Path.Combine([root, .. SplitRelative(relativePath)]);
}
