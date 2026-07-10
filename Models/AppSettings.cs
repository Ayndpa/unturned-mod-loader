namespace UnturnedModLoader.Models;

public class AppSettings
{
    public bool OnboardingCompleted { get; set; }
    public string GamePath { get; set; } = "";

    /// <summary>Local dev API by default; switch to <see cref="ApiProvider.Cloud"/> for production.</summary>
    public ApiProvider ApiProvider { get; set; } = ApiProvider.Local;

    public string LocalApiBaseUrl { get; set; } = "http://localhost:3000";
    public string CloudApiBaseUrl { get; set; } = "";
}