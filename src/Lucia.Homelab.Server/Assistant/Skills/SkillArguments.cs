using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// Reads the <c>arguments</c> of a run_skill_script call once, before any runner sees them. Smaller models send the object as JSON
/// text, or name the server beside the arguments instead of inside them; both are accepted. Every message that starts with
/// <see cref="Prefix"/> was written by Lucia, so a model can tell it from a script's own output.
/// </summary>
public static class SkillArguments
{
    public const string Prefix = "HOST: ";
    public const int MaxStringBytes = 32 * 1024;
    public const string CoercedCounter = "lucia.assistant.skill_arguments_coerced";

    private static readonly Meter Meter = new(AssistantBroker.GovernanceMeter);
    private static readonly Counter<long> Coerced = Meter.CreateCounter<long>(CoercedCounter, "{call}",
        "run_skill_script calls whose arguments were repaired: a JSON string read as an object (kind=string) or a top-level node moved into the arguments (kind=node).");

    public const string NodeMustBeText = Prefix + "arguments.node must be the server's name as a string, such as \"lucialab01\".";

    /// <summary>The arguments as an object with the server named inside; or an error for the model, which starts with <see cref="Prefix"/>.</summary>
    /// <param name="topLevelNode">A <c>node</c> given beside the arguments, or null.</param>
    /// <param name="execution">A <see cref="SkillExecution"/>: only a node skill must name a server.</param>
    public static (JsonObject? Args, string? Error, bool Coerced) Normalize(JsonElement? arguments, string? topLevelNode, string execution) =>
        Normalize(arguments, topLevelNode, execution, out _);

    /// <summary>Like the three-value form; <paramref name="repairs"/> also says whether a top-level node was moved into the arguments.</summary>
    public static (JsonObject? Args, string? Error, bool Coerced) Normalize(JsonElement? arguments, string? topLevelNode, string execution,
        out (bool Coerced, bool NodeInjected) repairs)
    {
        repairs = (false, false);
        var needsNode = execution == SkillExecution.Node;
        var objectHint = needsNode ? "send an object with a \"node\" key." : "send a JSON object.";
        JsonObject args;
        var coerced = false;
        var value = arguments is { ValueKind: JsonValueKind.Undefined } ? null : arguments;
        switch (value?.ValueKind)
        {
            case null or JsonValueKind.Null: args = []; break;
            case JsonValueKind.Object: args = JsonNode.Parse(value.Value.GetRawText()) as JsonObject ?? []; break;
            case JsonValueKind.Array: return (null, Prefix + "arguments was an array; " + objectHint, false);
            case JsonValueKind.String:
                var text = value.Value.GetString() ?? "";
                if (Encoding.UTF8.GetByteCount(text) > MaxStringBytes)
                    return (null, Prefix + $"arguments was a string over {MaxStringBytes / 1024} KiB; send a JSON object, not a quoted string.", false);
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 }); }
                catch (JsonException) { return (null, Prefix + "arguments was a string that isn't valid JSON; send a JSON object, not a quoted string.", false); }
                using (parsed)
                {
                    switch (parsed.RootElement.ValueKind)
                    {
                        case JsonValueKind.Object: args = JsonNode.Parse(parsed.RootElement.GetRawText()) as JsonObject ?? []; coerced = true; break;
                        case JsonValueKind.Array: return (null, Prefix + "arguments was an array; " + objectHint, false);
                        default: return (null, Prefix + $"arguments was {Describe(parsed.RootElement)} inside a string; send a JSON object.", false);
                    }
                }
                break;
            default: return (null, Prefix + $"arguments was {Describe(value!.Value)}; send a JSON object.", false);
        }

        string? named = null;
        if (args["node"] is { } entry && entry.GetValueKind() != JsonValueKind.Null)
        {
            if (entry.GetValueKind() != JsonValueKind.String) return (null, NodeMustBeText, false);
            named = entry.GetValue<string>().Trim();
        }
        var beside = string.IsNullOrWhiteSpace(topLevelNode) ? null : topLevelNode.Trim();
        var injected = false;
        if (beside is not null)
            if (string.IsNullOrEmpty(named)) { args["node"] = beside; named = beside; injected = true; }
            else if (!string.Equals(named, beside, StringComparison.OrdinalIgnoreCase))
                return (null, Prefix + $"arguments.node (\"{Shown(named)}\") and node (\"{Shown(beside)}\") disagree; name the server once, inside arguments.", false);
        if (needsNode && string.IsNullOrEmpty(named))
            return (null, Prefix + "arguments has no \"node\" key; add the server to run on, such as {\"node\": \"lucialab01\", ...}.", false);
        repairs = (coerced, injected);
        return (args, null, coerced);
    }

    /// <summary>The server a normalized argument object names, or null.</summary>
    public static string? NodeOf(JsonObject args) =>
        args["node"] is JsonValue value && value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    /// <summary>Counts a repaired call, one tick per kind.</summary>
    public static void Count(bool coerced, bool nodeInjected, string skill)
    {
        if (coerced) Coerced.Add(1, new KeyValuePair<string, object?>("skill", skill), new KeyValuePair<string, object?>("kind", "string"));
        if (nodeInjected) Coerced.Add(1, new KeyValuePair<string, object?>("skill", skill), new KeyValuePair<string, object?>("kind", "node"));
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.String => "a string",
        _ => "not an object",
    };

    private static string Shown(string text) => text.Length <= 64 ? text : text[..64] + "…";
}
