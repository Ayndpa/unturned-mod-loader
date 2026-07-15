namespace UnturnedModLoader.Models;

public class InstalledMod
{
    public int? RemoteId { get; set; }
    public LocalModKind Kind { get; set; } = LocalModKind.Module;
    public string RelativePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? CoverUrl { get; set; }
    public string? LocalIconPath { get; set; }
    public string ModuleFilePath { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public List<string> DependencyNames { get; set; } = [];
    public List<string> Dependencies { get; set; } = [];
    public List<string> Assemblies { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    public long InstalledAt { get; set; }

    /// <summary>
    /// For <see cref="LocalModKind.Scripted"/> mods: every overlay-relative path the install
    /// script created or modified. Recorded via overlay snapshot diff so uninstall can clean up
    /// even if the developer's uninstall script is incomplete.
    /// </summary>
    public List<string> InstalledFiles { get; set; } = [];

    /// <summary>
    /// For <see cref="LocalModKind.Scripted"/> mods: profile-relative staging directory holding
    /// the extracted archive (incl. scripts). Retained for uninstall scripts to reference.
    /// </summary>
    public string? StagingDir { get; set; }

    /// <summary>Backward-compatible alias for <see cref="RelativePath"/>.</summary>
    public string FileName
    {
        get => RelativePath;
        set => RelativePath = value;
    }
}

public class InstalledModsManifest
{
    public List<InstalledMod> Mods { get; set; } = [];
}