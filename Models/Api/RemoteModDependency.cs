using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class RemoteModDependency
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public LocalizedString Title { get; set; } = LocalizedString.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }
}