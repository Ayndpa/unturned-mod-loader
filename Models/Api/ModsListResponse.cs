using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class ModsListResponse
{
    [JsonPropertyName("mods")]
    public List<RemoteMod> Mods { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pages")]
    public int Pages { get; set; }
}