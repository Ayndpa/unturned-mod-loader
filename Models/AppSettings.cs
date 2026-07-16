namespace UnturnedModLoader.Models;

public class AppSettings
{
    /// <summary>Schema version. 1 = pre-profile single Modules install; 2 = multi-profile VFS.</summary>
    public int SettingsVersion { get; set; } = 2;

    public bool OnboardingCompleted { get; set; }

    /// <summary>Single Unturned install root (folder containing Unturned.exe).</summary>
    public string GamePath { get; set; } = "";

    /// <summary>Active profile id (see <see cref="GameProfile.DefaultBuiltInId"/>).</summary>
    public string ActiveProfileId { get; set; } = GameProfile.DefaultBuiltInId;

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
