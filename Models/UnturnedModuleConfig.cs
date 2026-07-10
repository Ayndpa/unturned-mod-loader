using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models;

public class UnturnedModuleConfig
{
    [JsonPropertyName("IsEnabled")]
    public bool IsEnabled { get; set; } = true;

    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Version")]
    public string Version { get; set; } = "1.0.0.0";

    [JsonPropertyName("Dependencies")]
    public List<UnturnedModuleDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("Assemblies")]
    public List<UnturnedModuleAssembly> Assemblies { get; set; } = [];
}

public class UnturnedModuleDependency
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Version")]
    public string Version { get; set; } = "1.0.0.0";
}

public class UnturnedModuleAssembly
{
    [JsonPropertyName("Path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("Role")]
    public string Role { get; set; } = "";
}

public enum LocalModKind
{
    Module,
    Dll,
}

public class ParsedLocalMod
{
    public LocalModKind Kind { get; init; }
    public string RelativePath { get; init; } = "";
    public string Title { get; init; } = "";
    public string Version { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public string ModuleName { get; init; } = "";
    public string ModuleFilePath { get; init; } = "";
    public string DirectoryPath { get; init; } = "";
    public string? LocalIconPath { get; init; }
    public IReadOnlyList<string> DependencyNames { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> Assemblies { get; init; } = [];
    public IReadOnlyList<string> ResolvedAssemblyPaths { get; init; } = [];
}