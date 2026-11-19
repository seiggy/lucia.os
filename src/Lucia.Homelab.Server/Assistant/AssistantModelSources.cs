using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Assistant;

public sealed record AssistantConnection(string Id, string Name, string BaseUrl, string? ApiKey = null);
public sealed record AssistantSourceSettings(AssistantConnection[] Connections, string[] DisabledModels);

public sealed partial class AssistantProviders
{
    public async Task<object> SourceSettingsAsync(string owner, CancellationToken ct)
    {
        var saved = ReadSources(owner);
        return new
        {
            connections = saved.Connections.Select(connection => new
            {
                connection.Id, connection.Name, connection.BaseUrl, hasKey = !string.IsNullOrEmpty(connection.ApiKey)
            }),
            saved.DisabledModels,
            sources = await ListAsync(owner, ct, includeDisabled: true)
        };
    }

    public async Task SaveSourcesAsync(string owner, AssistantSourceSettings settings, CancellationToken ct)
    {
        if (settings.Connections is null || settings.DisabledModels is null || settings.Connections.Length > 20
            || settings.DisabledModels.Length > 4000)
            throw InvalidSource("Keep at most 20 connections and 4,000 disabled models.");
        var gate = _gates.GetOrAdd("sources:" + Check(owner), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var before = ReadSources(owner);
            var connections = new List<AssistantConnection>();
            foreach (var connection in settings.Connections)
            {
                if (connection is null || connection.Id is null || !ConnectionPattern().IsMatch(connection.Id)
                    || connections.Any(item => item.Id == connection.Id) || string.IsNullOrWhiteSpace(connection.Name)
                    || connection.Name.Length > 80 || connection.Name.Any(char.IsControl))
                    throw InvalidSource("Each connection needs a unique ID and a name of 1 to 80 characters.");
                var url = SourceUri(connection.BaseUrl).AbsoluteUri;
                var old = before.Connections.FirstOrDefault(item => item.Id == connection.Id);
                // A retained key must never be sent to a different endpoint after a URL edit.
                if (old is { ApiKey.Length: > 0 } && old.BaseUrl != url && connection.ApiKey is null)
                    throw InvalidSource("Enter the API key again when changing a connection's URL, or clear it.");
                var key = connection.ApiKey?.Trim() ?? old?.ApiKey;
                if (key is { Length: > 4096 } || key?.Any(character => character < 33 || character > 126) == true)
                    throw InvalidSource("An API key must be at most 4,096 printable characters without spaces.");
                connections.Add(new(connection.Id, connection.Name.Trim(), url, key));
            }
            if (settings.DisabledModels.Any(id => id is null || !AssistantRuns.ModelPattern().IsMatch(id) || IsGitHub(id)))
                throw InvalidSource("Only non-GitHub models can be disabled here.");
            var saved = new AssistantSourceSettings([.. connections], [.. settings.DisabledModels.Distinct()]);
            var encrypted = SourceProtector(owner).Protect(JsonSerializer.Serialize(saved, Json));
            await DomainOnboardingStore.WriteJson(SourcesPath(owner), new Envelope(1, encrypted), ct, Json);
        }
        finally { gate.Release(); }
    }

    private AssistantSourceSettings ReadSources(string owner)
    {
        var path = SourcesPath(owner);
        try
        {
            if (!File.Exists(path)) return new([], []);
            DomainOnboardingStore.RejectLinks(path);
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException();
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllBytes(path), Json);
            if (envelope is not { Version: 1, Data.Length: > 0 }) throw new InvalidDataException();
            return JsonSerializer.Deserialize<AssistantSourceSettings>(SourceProtector(owner).Unprotect(envelope.Data), Json)
                is { Connections: not null, DisabledModels: not null } saved ? saved : throw new InvalidDataException();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            logger.LogWarning("Assistant model source settings could not be read ({ErrorType}).", e.GetType().Name);
            throw Unavailable("Your model source settings could not be read. Restore them with the host's Data Protection keys.");
        }
    }

    private string SourcesPath(string owner) => Path.Combine(Path.GetFullPath(options.Value.Directory), "sources", Check(owner) + ".json");

    private IDataProtector SourceProtector(string owner) => protection.CreateProtector("Lucia.Homelab.AssistantSources.v1", owner);

    private static Uri SourceUri(string? value) =>
        value is { Length: > 0 and <= 2048 } && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && uri.Host.Length > 0 && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/")
            : throw InvalidSource("Use an absolute HTTP or HTTPS API base URL, including /v1, without credentials, query or fragment.");

    private async Task<ServedModel[]> ConnectionModelsAsync(AssistantConnection connection, CancellationToken ct)
    {
        var list = await SendAsync(HttpMethod.Get, SourceUri(connection.BaseUrl), "models", connection.ApiKey, null, ct);
        if (list.ValueKind != JsonValueKind.Object || !list.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The model list is not an OpenAI model list.");
        return Models(list, $"byok:{connection.Id}/");
    }

    private async Task<AssistantSource> ConnectionSourceAsync(AssistantConnection connection, CancellationToken ct)
    {
        try
        {
            var models = (await ConnectionModelsAsync(connection, ct))
                .Select(model => new AssistantModel($"byok:{connection.Id}/{model.Id}", model.Id, model.Context)).ToArray();
            return new("byok:" + connection.Id, connection.Name, models,
                models.Length == 0 ? "No compatible chat models were returned. Check the client's model access in your gateway." : null);
        }
        catch (Exception e) when (Unreachable(e, ct))
        {
            return new("byok:" + connection.Id, connection.Name, [], ConnectionFailure(e));
        }
    }

    private string ConnectionFailure(Exception error)
    {
        logger.LogWarning("An assistant model source could not list models ({ErrorType}).", error.GetType().Name);
        return error is HttpRequestException { StatusCode: { } status }
            ? $"The model source answered with HTTP {(int)status}. Check its API URL, client key and model access."
            : "The model source could not return a model list. Check its API URL, connectivity and TLS certificate.";
    }

    private static AssistantException InvalidSource(string message) => new(400, "invalid_model_sources", message);

    [GeneratedRegex("^[a-f0-9]{12}$")]
    private static partial Regex ConnectionPattern();
}
