using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The shape of the <c>arguments</c> a skill's scripts take. A skill declares it in its SKILL.md frontmatter, as <c>parameters_schema</c>
/// under <c>metadata</c>: one JSON Schema object for every script, or a map from script path (scripts/verify.py) to a schema. It may be
/// a YAML mapping or JSON text. A script with no declared schema gets a default for the skill's execution mode, never an array.
/// </summary>
public static class SkillSchemas
{
    public const string Key = "parameters_schema";
    public const int MaxBytes = 16 * 1024;
    private static readonly HashSet<string> NumberKeys = new(StringComparer.Ordinal)
    {
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties", "multipleOf",
    };

    private static readonly HashSet<string> BooleanKeys = new(StringComparer.Ordinal)
    {
        "additionalProperties", "uniqueItems", "nullable", "readOnly", "writeOnly", "deprecated",
    };

    /// <summary>What a script takes when the skill declares nothing.</summary>
    public static JsonObject Default(string execution) => execution switch
    {
        SkillExecution.Node => Parse("""{"type":"object","properties":{"node":{"type":"string","description":"The server to run on."}},"required":["node"]}"""),
        SkillExecution.Sandbox => Parse("""{"type":"object","properties":{"node":{"type":"string"}}}"""),
        _ => Parse("""{"type":"object"}"""),
    };

    /// <summary>The declared schema, as a node: null when there is none; <paramref name="problem"/> says why a declared one is unusable.</summary>
    internal static JsonNode? Read(SkillMarkdown? markdown, out string? problem)
    {
        problem = null;
        var raw = markdown?.MetadataValue(Key);
        if (raw is null) return null;
        JsonNode? node;
        try
        {
            node = raw switch
            {
                string text when text.Length > MaxBytes => throw new FormatException(),
                string text => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }),
                IDictionary<object, object> map => FromYaml(map, null),
                _ => null,
            };
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            problem = $"{Key} in the SKILL.md metadata must be a JSON Schema object (a YAML mapping or JSON text up to {MaxBytes / 1024} KiB).";
            return null;
        }
        problem = Validate(node);
        return problem is null ? node : null;
    }

    /// <summary>Why a declared schema can't be used, or null when it is a schema object (or a map of them) with type object.</summary>
    private static string? Validate(JsonNode? node)
    {
        if (node is not JsonObject declared || declared.Count == 0)
            return $"{Key} in the SKILL.md metadata must be a JSON Schema object with \"type\": \"object\", or a map from script path to one.";
        if (IsSchema(declared)) return CheckSchema(declared, Key);
        foreach (var (path, schema) in declared)
        {
            if (!SkillLanguages.IsScript(path)) return $"{Key} has an entry for {path}, which isn't a script path such as scripts/verify.py. A schema needs \"type\": \"object\".";
            if (schema is not JsonObject entry) return $"{Key} for {path} must be a JSON Schema object.";
            if (CheckSchema(entry, $"{Key} for {path}") is { } problem) return problem;
        }
        return null;
    }

    private static bool IsSchema(JsonObject node) => node.ContainsKey("type") || node.ContainsKey("properties") || node.ContainsKey("required");

    private static string? CheckSchema(JsonObject schema, string where)
    {
        if (schema["type"] is not JsonValue type || !type.TryGetValue(out string? text) || text != "object")
            return $"{where} must have \"type\": \"object\"; scripts take a JSON object, never an array or text.";
        if (schema["properties"] is { } properties && properties is not JsonObject) return $"{where}: properties must be an object.";
        if (schema["required"] is { } required && (required is not JsonArray list || list.Any(item => item is not JsonValue value || !value.TryGetValue(out string? _))))
            return $"{where}: required must be a list of property names.";
        return null;
    }

    /// <summary>Checks schema text a caller sends: null when it is a usable schema, with <paramref name="compact"/> the text to store.</summary>
    internal static string? Check(string text, out string? compact)
    {
        compact = null;
        if (text.Length > MaxBytes) return $"{Key} is too large; the limit is {MaxBytes / 1024} KiB.";
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 });
            if (Validate(node) is { } problem) return problem;
            compact = node!.ToJsonString();
            return null;
        }
        catch (JsonException) { return $"{Key} must be JSON text of a schema object."; }
    }

    /// <summary>The schema a script's <c>arguments</c> follow: the declared one when there is one, else the default; a node skill's always names <c>node</c>.</summary>
    internal static JsonObject For(string execution, JsonNode? declared, string script)
    {
        JsonObject? schema = null;
        if (declared is JsonObject root)
            if (IsSchema(root)) schema = (JsonObject)root.DeepClone();
            else
            {
                var wanted = script.Replace('\\', '/').TrimStart('.', '/');
                foreach (var (path, entry) in root)
                    if (string.Equals(path.Replace('\\', '/').TrimStart('.', '/'), wanted, StringComparison.Ordinal) && entry is JsonObject found) schema = (JsonObject)found.DeepClone();
            }
        if (schema is null) return Default(execution);
        if (execution != SkillExecution.Node) return schema;
        var properties = schema["properties"] as JsonObject ?? [];
        if (!properties.ContainsKey("node")) properties["node"] = Parse("""{"type":"string","description":"The server to run on."}""");
        schema["properties"] = properties;
        var required = schema["required"] as JsonArray ?? [];
        if (!required.Any(item => item is JsonValue value && value.TryGetValue(out string? text) && text == "node")) required.Add("node");
        schema["required"] = required;
        return schema;
    }

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    // YAML hands every scalar over as text, so the schema keywords that take numbers or booleans are read back as such.
    private static JsonNode? FromYaml(object? value, string? key) => value switch
    {
        IDictionary<object, object> map => new JsonObject(map.Select(pair => KeyValuePair.Create(pair.Key.ToString() ?? "", FromYaml(pair.Value, pair.Key.ToString())))),
        IEnumerable<object> list when value is not string => new JsonArray([.. list.Select(item => FromYaml(item, null))]),
        string text when key is not null && NumberKeys.Contains(key) && double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number),
        string text when key is not null && BooleanKeys.Contains(key) && bool.TryParse(text, out var flag) => JsonValue.Create(flag),
        null => null,
        _ => JsonValue.Create(value.ToString()),
    };
}
