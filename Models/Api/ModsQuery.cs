namespace UnturnedModLoader.Models.Api;

public class ModsQuery
{
    public string? Category { get; set; }
    public string? Search { get; set; }
    public string Sort { get; set; } = "newest";
}