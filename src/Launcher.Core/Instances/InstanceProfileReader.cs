using System.Text.Json.Nodes;

namespace Launcher.Core.Instances;

public static class InstanceProfileReader
{
    public static string GetJsonPath(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var expected = Path.Combine(directory, Path.GetFileName(directory) + ".json");
        if (File.Exists(expected)) return expected;
        foreach (var candidate in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(candidate)) is JsonObject json
                    && json.ContainsKey("mainClass") && json.ContainsKey("type") && json.ContainsKey("id")) return candidate;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        throw new FileNotFoundException("未找到版本 JSON 文件：" + expected, expected);
    }

    public static JsonObject Read(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var jsonPath = GetJsonPath(directory);
        JsonObject json;
        try { json = ReadJson(jsonPath); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // PCL retries a transient read failure off the UI thread.
            Thread.Sleep(2000);
            json = ReadJson(jsonPath);
        }
        var name = Path.GetFileName(directory);
        if (json["patches"] is JsonArray patches && !json.ContainsKey("time"))
        {
            var merged = new JsonObject();
            foreach (var patch in patches.OfType<JsonObject>().OrderBy(p => int.TryParse(p["priority"]?.ToString(), out var priority) ? priority : 0))
                if (patch["id"] is not null) merged = Merge(merged, patch);
            var gameVersion = patches.OfType<JsonObject>().FirstOrDefault(p => p["id"]?.ToString() == "game")?["version"]?.ToString();
            json = merged;
            json["id"] = name;
            if (gameVersion is not null) json["clientVersion"] = gameVersion;
            json.Remove("inheritsFrom");
        }
        if (string.Equals(json["inheritsFrom"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)) json.Remove("inheritsFrom");
        if (json["inheritsFrom"] is null && !json.ContainsKey("logging")
            && (json["libraries"]?.ToJsonString().Contains("liteloader", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            var candidate = json["jar"]?.ToString();
            if (string.IsNullOrEmpty(candidate))
            {
                var match = System.Text.RegularExpressions.Regex.Match(name, @"(?:1|[2-9][0-9])\.[0-9]+(?:\.[0-9]+)?");
                if (match.Success) candidate = match.Value;
            }
            if (!string.IsNullOrEmpty(candidate) && !candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) json["inheritsFrom"] = candidate;
        }
        return json;
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new InvalidDataException("版本 JSON 必须是对象。");

    public static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var (key, value) in child)
        {
            if (value is JsonObject obj && result[key] is JsonObject existing) result[key] = Merge(existing, obj);
            else if (value is JsonArray array && result[key] is JsonArray prior)
            {
                var combined = new JsonArray();
                foreach (var item in key == "libraries" ? array.Concat(prior) : prior.Concat(array)) combined.Add(item?.DeepClone());
                result[key] = combined;
            }
            else if (value is not null) result[key] = value.DeepClone();
        }
        return result;
    }

    public static void ValidateParentName(string name)
    {
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("版本 JSON 中的前置版本名称无效。");
    }
}
