namespace UnturnedModLoader.Models;

/// <summary>What the loader did to the game install for the active profile.</summary>
public class OverlayJournal
{
    public int Version { get; set; } = 1;
    public string? ProfileId { get; set; }
    public string? GamePath { get; set; }
    public List<OverlayOp> Ops { get; set; } = [];
}

public class OverlayOp
{
    /// <summary>CreateFile | CreateDirectory | ReplaceFile | Junction</summary>
    public string Kind { get; set; } = "";

    /// <summary>Path relative to the game install root.</summary>
    public string RelativePath { get; set; } = "";

    /// <summary>For Junction: absolute source under profile overlay.</summary>
    public string? SourcePath { get; set; }

    /// <summary>For ReplaceFile: path under profile originals\ (same relative path).</summary>
    public string? OriginalsRelativePath { get; set; }
}

public static class OverlayOpKind
{
    public const string CreateFile = "CreateFile";
    public const string CreateDirectory = "CreateDirectory";
    public const string ReplaceFile = "ReplaceFile";
    public const string Junction = "Junction";
}

/// <summary>
/// Roots that are owned entirely by a profile when present under overlay.
/// Applied as a single directory junction for isolation and speed.
/// </summary>
public static class OverlayOwnedRoots
{
    public static IReadOnlyList<string> All { get; } =
    [
        "Modules",
        "BepInEx",
        "doorstop_libs",
    ];

    public static bool IsOwnedRoot(string relativePath)
    {
        var root = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (root is null)
            return false;
        return All.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
    }

    public static string? GetOwnedRootName(string relativePath)
    {
        var root = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (root is null)
            return null;
        return All.FirstOrDefault(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
    }
}
