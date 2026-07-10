namespace UnturnedModLoader.Models;

public class AppSettings
{
    public bool OnboardingCompleted { get; set; }
    public string GamePath { get; set; } = "";

    /// <summary>Local dev API by default; switch to <see cref="ApiProvider.Cloud"/> for production.</summary>
    public ApiProvider ApiProvider { get; set; } = ApiProvider.Local;

    public string LocalApiBaseUrl { get; set; } = "http://localhost:3000";
    public string CloudApiBaseUrl { get; set; } = "";

    public string? AuthToken { get; set; }
    public int? UserId { get; set; }
    public string? Username { get; set; }
    public string? UserRole { get; set; }

    /// <summary>UI locale: <c>zh</c> or <c>en</c>.</summary>
    public string Locale { get; set; } = "";

    /// <summary>Theme preference: <c>light</c>, <c>dark</c>, or <c>system</c>.</summary>
    public string Theme { get; set; } = "";

    public bool IsLoggedIn =>
        !string.IsNullOrWhiteSpace(AuthToken) && UserId.HasValue;
}