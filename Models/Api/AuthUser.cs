using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class AuthUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    [JsonPropertyName("banned")]
    public int? Banned { get; set; }

    [JsonPropertyName("ban_reason")]
    public string? BanReason { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}