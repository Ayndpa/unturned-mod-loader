using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class AuthActionResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("user")]
    public AuthUserSummary? User { get; set; }
}

public class AuthUserSummary
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
}

public class MeResponse
{
    [JsonPropertyName("user")]
    public AuthUser? User { get; set; }

    [JsonPropertyName("banned")]
    public bool? Banned { get; set; }

    [JsonPropertyName("ban_reason")]
    public string? BanReason { get; set; }
}

public record AuthResult(bool Success, AuthUser? User, string? Error);

public record AuthActionResult(bool Success, string? Error);