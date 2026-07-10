namespace UnturnedModLoader.Models;

public class InstalledMod
{
    public int? RemoteId { get; set; }
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }
    public string FileName { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public long InstalledAt { get; set; }
}

public class InstalledModsManifest
{
    public List<InstalledMod> Mods { get; set; } = [];
}