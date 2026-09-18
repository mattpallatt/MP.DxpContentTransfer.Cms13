using System.Text.Json;
using System.Text.Json.Nodes;

namespace DxpContentTransfer.Cms13.Services;

// Generic JSON tree visitors. One small set of walkers replaces the half-dozen near-identical
// hand-rolled "if object recurse / if array recurse" methods that used to be copy-pasted around
// the transfer service. Brought in with `using static` so callers read as before.
internal static class JsonVisitors
{
    // Invokes onObject for every JsonObject in a mutable tree (depth-first, parent first).
    internal static void WalkJsonObjects(JsonNode node, Action<JsonObject> onObject)
    {
        if (node is JsonObject obj)
        {
            onObject(obj);
            foreach (var child in obj.Select(kvp => kvp.Value).ToList())
                if (child != null) WalkJsonObjects(child, onObject);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                if (item != null) WalkJsonObjects(item, onObject);
        }
    }

    // Invokes onObject for every object-kind element in a read-only tree (depth-first, parent first).
    internal static void WalkJsonElements(JsonElement element, Action<JsonElement> onObject)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            onObject(element);
            foreach (var prop in element.EnumerateObject())
                WalkJsonElements(prop.Value, onObject);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                WalkJsonElements(item, onObject);
        }
    }

    // Applies transform to every string value in the tree, whether held as an object
    // property or an array element.
    internal static void MutateJsonStrings(JsonNode node, Func<string, string> transform)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue val && val.TryGetValue(out string str))
                    obj[key] = transform(str);
                else if (child != null)
                    MutateJsonStrings(child, transform);
            }
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue val && val.TryGetValue(out string str))
                    arr[i] = transform(str);
                else if (arr[i] != null)
                    MutateJsonStrings(arr[i], transform);
            }
        }
    }
}
