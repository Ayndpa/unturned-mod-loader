using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

public class RemoteCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name_zh")]
    public string NameZh { get; set; } = "";

    [JsonPropertyName("name_en")]
    public string NameEn { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; set; }
}