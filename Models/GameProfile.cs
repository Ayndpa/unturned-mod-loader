namespace UnturnedModLoader.Models;

/// <summary>
/// A mod profile: persisted overlay changes layered on top of the game install (virtual game tree).
/// </summary>
public class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long CreatedAt { get; set; }

    public static GameProfile CreateUser(string name)
    {
        var id = Guid.NewGuid().ToString("N");
        return new GameProfile
        {
            Id = id,
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}
