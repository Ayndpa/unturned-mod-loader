using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class CategoriesListResponse
{
    [JsonPropertyName("categories")]
    public List<RemoteCategory> Categories { get; set; } = [];
}