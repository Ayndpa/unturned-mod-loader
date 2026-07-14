namespace UnturnedModLoader.Models;

public class GameProfile
{
    public const string VanillaId = "vanilla";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsBuiltIn { get; set; }
    public bool IsVanilla { get; set; }
    public long CreatedAt { get; set; }

    public static GameProfile CreateVanilla(string displayName) => new()
    {
        Id = VanillaId,
        Name = displayName,
        IsBuiltIn = true,
        IsVanilla = true,
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
            IsVanilla = false,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
    }
}
