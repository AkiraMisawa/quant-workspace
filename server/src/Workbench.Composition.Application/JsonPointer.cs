using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace Workbench.Composition.Application;

/// <summary>RFC 6901 JSON Pointer evaluation. The only path language bindings may use.</summary>
public static class JsonPointer
{
    public static bool IsValid(string pointer) => pointer.Length == 0 || pointer[0] == '/';

    /// <summary>Finds the node at <paramref name="pointer"/>; false when any segment is absent.</summary>
    public static bool TryResolve(JsonNode? root, string pointer, [MaybeNullWhen(false)] out JsonNode? node)
    {
        if (!IsValid(pointer)) throw new ArgumentException($"'{pointer}' is not a JSON Pointer.", nameof(pointer));
        node = root;
        if (pointer.Length == 0) return true;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            switch (node)
            {
                case JsonObject obj when obj.TryGetPropertyValue(segment, out var child):
                    node = child;
                    break;
                case JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count:
                    node = array[index];
                    break;
                default:
                    node = null;
                    return false;
            }
        }
        return true;
    }
}
