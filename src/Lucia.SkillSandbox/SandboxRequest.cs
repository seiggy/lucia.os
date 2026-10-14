using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lucia.SkillSandbox;

/// <summary>What the controller pipes to the sandbox on stdin. <c>Script</c> is a path inside the skill that starts with scripts/.</summary>
internal sealed partial record SandboxRequest(string Script, string Language, string ArgumentsJson, string[] AllowedDomains, int TimeoutSeconds, string? HeapSize)
{
    public const int MaxRequestBytes = 256 * 1024, MaxScriptBytes = 512 * 1024, MaxDomains = 64, MaxTimeout = 240;
    public const string Python = "python", JavaScript = "javascript";

    /// <summary>Parses and validates a request. Returns null and a message that is safe to show the model when it is not acceptable.</summary>
    public static SandboxRequest? Parse(string json, out string error)
    {
        error = "";
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { root = null; }
        if (root is null) { error = "The sandbox request is not a JSON object."; return null; }

        var script = Text(root, "script");
        var language = Text(root, "language");
        if (script is null || !ValidScript(script, language, out error)) { error = error.Length > 0 ? error : "The request names no script."; return null; }

        var arguments = root["arguments"];
        if (arguments is not (null or JsonObject)) { error = "The script arguments must be a JSON object."; return null; }
        var argumentsJson = (arguments ?? new JsonObject()).ToJsonString(Ascii);

        var domains = new List<string>();
        if (root["allowedDomains"] is { } list)
        {
            if (list is not JsonArray array || array.Count > MaxDomains) { error = "allowedDomains must be a short list of host names."; return null; }
            foreach (var item in array)
            {
                if (item is not JsonValue value || !value.TryGetValue(out string? domain) || !ValidDomain(domain))
                {
                    error = "allowedDomains holds an entry that is not a host name.";
                    return null;
                }
                domains.Add(domain);
            }
        }

        var timeout = 60;
        if (root["timeoutSeconds"] is { } seconds)
        {
            if (seconds is not JsonValue number || !number.TryGetValue(out int parsed) || parsed < 1 || parsed > MaxTimeout)
            {
                error = $"timeoutSeconds must be from 1 to {MaxTimeout}.";
                return null;
            }
            timeout = parsed;
        }

        string? heap = null;
        if (root["heapSize"] is { } size)
        {
            if (size is not JsonValue text || !text.TryGetValue(out heap) || !ValidHeap(heap)) { error = "heapSize must look like 64Mi, at most 512Mi."; return null; }
        }
        return new(script, language!, argumentsJson, [.. domains], timeout, heap);
    }

    internal static readonly JsonSerializerOptions Ascii = new();

    private static string? Text(JsonObject root, string key) => root[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>A plain path under scripts/ whose extension matches the language: no "..", no absolute or backslash path.</summary>
    internal static bool ValidScript(string script, string? language, out string error)
    {
        error = "";
        var parts = script.Split('/');
        if (script.Length is 0 or > 200 || script.Contains('\\') || script.Any(char.IsControl) || parts.Length < 2 || parts[0] != "scripts"
            || parts.Any(part => part is "" or "." or ".."))
        {
            error = "The script must be a plain path under scripts/.";
            return false;
        }
        var extension = Path.GetExtension(script).ToLowerInvariant();
        var expected = extension switch { ".py" => Python, ".js" or ".mjs" => JavaScript, _ => null };
        if (expected is null) { error = "The sandbox runs Python (.py) and JavaScript (.js, .mjs) scripts only."; return false; }
        if (language != expected) { error = $"A {extension} script runs as {expected}."; return false; }
        return true;
    }

    internal static bool ValidDomain(string? domain) =>
        domain is { Length: > 0 and <= 253 } && DomainPattern().IsMatch(domain);

    internal static bool ValidHeap(string? heap) =>
        heap is not null && HeapPattern().Match(heap) is { Success: true } match
        && long.Parse(match.Groups[1].Value) * (match.Groups[2].Value == "Gi" ? 1024 : 1) is > 0 and <= 512;

    [GeneratedRegex(@"\A(?:\*\.)?[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"\A([1-9][0-9]{0,3})(Mi|Gi)\z")]
    private static partial Regex HeapPattern();
}
