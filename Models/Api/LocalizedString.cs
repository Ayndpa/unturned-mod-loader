using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnturnedModLoader.Models.Api;

/// <summary>
/// Multilingual text from API: <c>{ "zh": "...", "en": "..." }</c> or legacy plain string.
/// </summary>
[JsonConverter(typeof(LocalizedStringJsonConverter))]
public sealed class LocalizedString
{
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Values => _values;

    public static LocalizedString Empty { get; } = new();

    public string Pick(string locale, string fallbackLocale = "zh")
    {
        if (_values.Count == 0)
            return "";

        if (_values.TryGetValue(locale, out var exact) && !string.IsNullOrWhiteSpace(exact))
            return exact.Trim();

        if (_values.TryGetValue(fallbackLocale, out var fb) && !string.IsNullOrWhiteSpace(fb))
            return fb.Trim();

        foreach (var lang in new[] { "zh", "en" })
        {
            if (_values.TryGetValue(lang, out var v) && !string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        foreach (var v in _values.Values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }

        return "";
    }

    internal void SetValues(Dictionary<string, string> values) => _values = values;
}

public sealed class LocalizedStringJsonConverter : JsonConverter<LocalizedString>
{
    private static readonly string[] Supported = ["zh", "en"];

    public override LocalizedString Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new LocalizedString();

        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return result;
            case JsonTokenType.String:
                var plain = reader.GetString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(plain))
                    return result;
                if (TryParseJsonMap(plain, out var fromEncoded))
                {
                    result.SetValues(fromEncoded);
                    return result;
                }

                result.SetValues(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["zh"] = plain,
                });
                return result;
            case JsonTokenType.StartObject:
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        continue;
                    var key = reader.GetString() ?? "";
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var val = reader.GetString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(val))
                            map[key] = val;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                result.SetValues(Normalize(map));
                return result;
            default:
                reader.Skip();
                return result;
        }
    }

    public override void Write(Utf8JsonWriter writer, LocalizedString value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value.Values)
            writer.WriteString(k, v);
        writer.WriteEndObject();
    }

    private static Dictionary<string, string> Normalize(Dictionary<string, string> input)
    {
        var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var lang in Supported)
        {
            if (input.TryGetValue(lang, out var val) && !string.IsNullOrWhiteSpace(val))
                outMap[lang] = val.Trim();
        }

        if (outMap.Count > 0)
            return outMap;

        foreach (var (k, v) in input)
        {
            if (!string.IsNullOrWhiteSpace(v))
                outMap[k] = v.Trim();
        }

        return outMap;
    }

    private static bool TryParseJsonMap(string raw, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = prop.Value.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(s))
                        map[prop.Name] = s;
                }
            }

            return map.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}

public static class LocalizedContent
{
    public static string Pick(LocalizedString? field, string locale) =>
        field?.Pick(locale) ?? "";
}