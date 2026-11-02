using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hubwright.Config;

/// <summary>
/// Deep-merges configuration JSON: objects merge key by key (case-insensitively, as the binder
/// reads them), anything else in the overlay replaces the base value.
/// </summary>
public static class JsonMerge
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Merge(string baseJson, string overlayJson)
    {
        var target = JsonNode.Parse(baseJson, documentOptions: Lenient) as JsonObject ?? [];
        var overlay = JsonNode.Parse(overlayJson, documentOptions: Lenient) as JsonObject ?? [];
        MergeInto(target, overlay);
        return target.ToJsonString();
    }

    public static void MergeInto(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

            if (value is JsonObject overlayObject
                && existingKey is not null
                && target[existingKey] is JsonObject targetObject)
            {
                MergeInto(targetObject, overlayObject);
                continue;
            }

            if (existingKey is not null)
            {
                target.Remove(existingKey);
            }

            target[key] = value?.DeepClone();
        }
    }
}
