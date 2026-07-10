namespace UnturnedModLoader.Models;

public record ModItem(
    string Name,
    string Author,
    string Version,
    string Category,
    string Description,
    bool IsEnabled = false);