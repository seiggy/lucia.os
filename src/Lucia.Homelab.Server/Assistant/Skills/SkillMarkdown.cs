using YamlDotNet.Serialization;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>A SKILL.md read apart: its frontmatter keys, and the Markdown body that follows them.</summary>
internal sealed record SkillMarkdown(Dictionary<string, object?> Map, string Body)
{
    private static readonly IDeserializer Reader = new DeserializerBuilder().Build();
    private static readonly ISerializer Writer = new SerializerBuilder().DisableAliases().WithQuotingNecessaryStrings()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull).Build();

    /// <summary>
    /// Splits the frontmatter from the body so Lucia can rewrite it. This isn't how a skill is validated or loaded: the Agent
    /// Framework's file source does that. Null when there is no readable frontmatter.
    /// </summary>
    public static SkillMarkdown? Parse(string text)
    {
        text = text.TrimStart('\uFEFF').ReplaceLineEndings("\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        while (end >= 0 && end + 4 < text.Length && text[end + 4] is not ('\n' or ' ')) end = text.IndexOf("\n---", end + 4, StringComparison.Ordinal);
        if (end < 0) return null;
        try
        {
            var map = Reader.Deserialize<Dictionary<string, object?>>(text[4..end]) ?? [];
            var body = text[Math.Min(text.Length, end + 4)..].TrimStart(' ').TrimStart('\n');
            return new(map, body);
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidCastException or InvalidOperationException) { return null; }
    }

    public string? Text(string key) => Map.TryGetValue(key, out var value) && value is string text ? text : null;

    /// <summary>The <c>metadata</c> map's entries, keys and values as text.</summary>
    public Dictionary<string, object?> Metadata()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Map.TryGetValue("metadata", out var value) && value is IDictionary<object, object> entries)
            foreach (var (key, item) in entries) metadata[key.ToString() ?? ""] = item?.ToString();
        return metadata;
    }

    public SkillSettings Settings() => SkillSettings.From(Metadata());

    /// <summary>One <c>metadata</c> entry as parsed, so a YAML mapping stays a mapping; null when there is none.</summary>
    public object? MetadataValue(string key) =>
        Map.TryGetValue("metadata", out var value) && value is IDictionary<object, object> entries && entries.TryGetValue(key, out var item) ? item : null;

    /// <summary>
    /// Writes a SKILL.md from a copy of <paramref name="previous"/>'s frontmatter: only name, description and the lucia.* metadata keys
    /// change, and every other value (license, version, list-valued allowed-tools, other metadata) passes through as it was parsed.
    /// </summary>
    public static string Write(string name, string description, SkillSettings settings, string body, SkillMarkdown? previous,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var map = new Dictionary<string, object?>(previous?.Map ?? [], StringComparer.Ordinal) { ["name"] = name, ["description"] = description };
        var metadata = new Dictionary<object, object?>();
        if (map.TryGetValue("metadata", out var existing) && existing is IDictionary<object, object> entries)
            foreach (var (key, item) in entries) metadata[key] = item;
        metadata[SkillSettings.ExecutionKey] = settings.Execution;
        metadata[SkillSettings.TierKey] = settings.Tier;
        if (settings.AllowedDomains.Length > 0) metadata[SkillSettings.DomainsKey] = string.Join(",", settings.AllowedDomains);
        else metadata.Remove(SkillSettings.DomainsKey);
        if (settings.Network == SkillNetwork.Bridge) metadata[SkillSettings.NetworkKey] = SkillNetwork.Bridge;
        else metadata.Remove(SkillSettings.NetworkKey);
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
            if (value is null) metadata.Remove(key);
            else metadata[key] = value;
        map["metadata"] = metadata;
        return "---\n" + Writer.Serialize(map).ReplaceLineEndings("\n").TrimEnd('\n') + "\n---\n\n" + body.ReplaceLineEndings("\n").TrimStart('\n');
    }
}
