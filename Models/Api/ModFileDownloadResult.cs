namespace UnturnedModLoader.Models.Api;

public record ModFileDownloadResult(bool Success, byte[]? Content, string? FileName, string? Error);