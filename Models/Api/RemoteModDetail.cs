using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class RemoteModDetail : RemoteMod
{
    [JsonPropertyName("tags")]
    public List<LocalizedString> Tags { get; set; } = [];

    [JsonPropertyName("dependencies")]
    public List<RemoteModDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("comment_count")]
    public int CommentCount { get; set; }

    [JsonPropertyName("file_size")]
    public long FileSize { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public class ModDetailResponse
{
    [JsonPropertyName("mod")]
    public RemoteModDetail? Mod { get; set; }
}

public record ModDetailResult(bool Success, RemoteModDetail? Mod, string? Error);