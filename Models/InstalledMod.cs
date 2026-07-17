namespace UnturnedModLoader.Models;

/// <summary>
/// One installed mod = one manifest file under the profile overlay's
/// <c>.unmod-manifests\{RemoteId}.json</c>. Remote metadata comes from the download;
/// <see cref="Files"/> records every file extracted into the overlay root so uninstall
/// can remove exactly what this package wrote.
/// </summary>
public class InstalledMod
{
    public int? RemoteId { get; set; }
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }
    public long InstalledAt { get; set; }

    /// <summary>
    /// Paths extracted into the overlay root (relative, forward-slash separated), e.g.
    /// <c>winhttp.dll</c>, <c>BepInEx/core/0Harmony.dll</c>. Recorded at install time so
    /// uninstall removes exactly what this package wrote, even across version upgrades.
    /// </summary>
    public List<string> Files { get; set; } = [];
}
