namespace UnturnedModLoader.Services;

public static class GamePathValidator
{
    public const string ExecutableName = "Unturned.exe";

    public static bool IsValid(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, ExecutableName));
}