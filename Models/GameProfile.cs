namespace UnturnedModLoader.Models;

/// <summary>
/// A mod profile: persisted overlay changes layered on top of the game install (virtual game tree).
/// </summary>
public class GameProfile
{
    public const string DefaultBuiltInId = "default";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public long CreatedAt { get; set; }

    public static GameProfile CreateBuiltInDefault(string displayName) => new()
    {
        Id = DefaultBuiltInId,
        Name = displayName,
        IsBuiltIn = true,
        CreatedAt = 0,
    };

    public static GameProfile CreateUser(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        return new GameProfile
        {
            Id = id,
            Name = name,
            IsBuiltIn = false,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}