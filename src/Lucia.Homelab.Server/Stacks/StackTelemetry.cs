using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Telemetry;
using Microsoft.AspNetCore.DataProtection;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>Where Lucia's servers and the controller send telemetry: the installed Observability app's OTLP/HTTP address.</summary>
/// <param name="Authorization">The whole <c>Authorization</c> header value.</param>
public sealed record TelemetryDestination(Uri Endpoint, string Authorization);

public sealed partial class StackStore
{
    /// <summary>
    /// The system app every server runs while an Observability app is installed: an OpenTelemetry collector that takes
    /// OTLP on loopback and scrapes the server's hardware (and Local AI's engine) for metrics, then forwards all of it.
    /// </summary>
    public const string RelayName = "telemetry-relay";
    private static readonly TimeSpan DestinationLifetime = TimeSpan.FromSeconds(30), ModelsLifetime = TimeSpan.FromMinutes(5);
    private (DateTimeOffset At, TelemetryDestination? Value) _destination;
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, string[] Models)> _llamaModels = new();

    /// <summary>The running Observability app's OTLP/HTTP address and credentials, or null when none is installed.</summary>
    public async Task<TelemetryDestination?> TelemetryEndpoint(CancellationToken ct)
    {
        var cached = _destination;
        if (cached.At > time.GetUtcNow() - DestinationLifetime) return cached.Value;
        var value = Destination(await Read(ct), (await ManagedNodeDns.ActiveNaming(domains, ct))?.Namespace, await nodes.Addresses(ct));
        _destination = (time.GetUtcNow(), value);
        return value;
    }

    /// <summary>Its web address once the domain is active, since that one follows the app between servers; the server's port until then.</summary>
    private TelemetryDestination? Destination(IEnumerable<StoredStack> stacks, string? ns, IEnumerable<ManagedNodeAddress> addresses)
    {
        var stack = stacks.FirstOrDefault(item => item.Manifest.Template?.Id == "observability" && item is { Desired: "Running", Move: null });
        if (stack?.Manifest.Routes?.FirstOrDefault(route => route.GrpcPort is not null) is not { } route) return null;
        var address = stack.Manifest.Address ?? addresses.FirstOrDefault(item => item.Hostname == stack.Assigned)?.Address;
        var env = StackCatalog.ReadEnv(_protector.Unprotect(stack.ProtectedEnv));
        if (env.GetValueOrDefault("OTLP_PASSWORD") is not { Length: > 0 } password) return null;
        var endpoint = ns is not null ? new Uri($"https://{route.Host}.{ns}")
            : address is not null ? new UriBuilder("http", address, route.Port).Uri : null;
        var user = env.GetValueOrDefault("OTLP_USERNAME") ?? ObservabilityApp.User;
        return endpoint is null ? null : new(endpoint, "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
    }

    /// <summary>The relay for a server, or null without an Observability app. Its revision is its content's hash.</summary>
    private async Task<NodeDesiredStack?> Relay(Guid nodeId, string hostname, StoredStack[] stacks, CancellationToken ct)
    {
        if (await TelemetryEndpoint(ct) is not { } destination) return null;
        var node = (await nodes.Facts(ct)).FirstOrDefault(item => item.NodeId == nodeId);
        var gpu = node?.Status?.Runtime is { GpuContainers: true } runtime
            && runtime.Gpus.Any(item => item.Vendor.Equals("nvidia", StringComparison.OrdinalIgnoreCase));
        var scrapes = new List<RelayScrape>();
        var key = "";
        if (stacks.FirstOrDefault(item => item.Manifest.Template?.Id == "local-ai" && item.Assigned == hostname && item is { Desired: "Running", Move: null })
            is { Manifest.Template.Settings: { } settings } ai && int.TryParse(settings.GetValueOrDefault("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            key = StackCatalog.ReadEnv(_protector.Unprotect(ai.ProtectedEnv)).GetValueOrDefault("LUCIA_INFERENCE_KEY") ?? "";
            var target = $"127.0.0.1:{port}";
            if (settings.GetValueOrDefault("engine") is null or "lucia")
                scrapes.Add(new("lucia-inference", target));
            else if (settings.GetValueOrDefault("engine") == "vllm" && settings.GetValueOrDefault("vllm-model") is { Length: > 0 })
                scrapes.Add(new("vllm", target));
            else if (settings.GetValueOrDefault("engine") == "llamacpp" && node is not null
                && await LlamaModels(nodeId, node, port, key, ct) is { Length: > 0 } models)
                scrapes.Add(new("llama-cpp", target, models));
        }
        var config = TelemetryRelay.Config(JsonSerializer.Serialize(hostname), gpu, scrapes, TelemetryRelay.EnvExporter);
        var compose = TelemetryRelay.Compose(config, gpu);
        var env = $"OTLP_ENDPOINT={destination.Endpoint.AbsoluteUri.TrimEnd('/')}\nOTLP_AUTHORIZATION={destination.Authorization}\nLOCAL_AI_KEY={key}\n";
        var revision = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(compose + "\0" + env))) & long.MaxValue;
        return new(RelayName, revision, "Running", 0, 0, compose, env);
    }

    /// <summary>llama.cpp's router serves metrics per model, so the relay needs their names; they're reread every few minutes.</summary>
    private async Task<string[]> LlamaModels(Guid nodeId, ManagedNodeFacts node, int port, string key, CancellationToken ct)
    {
        if (_llamaModels.TryGetValue(nodeId, out var cached) && cached.At > time.GetUtcNow() - ModelsLifetime) return cached.Models;
        var address = (await nodes.Addresses(ct)).FirstOrDefault(item => item.NodeId == node.NodeId)?.Address;
        var models = cached.Models ?? [];
        if (address is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("http", IPAddress.Parse(address).ToString(), port, "/models").Uri);
            request.Headers.Authorization = new("Bearer", key);
            try
            {
                using var response = await ServingClient.SendAsync(request, ct);
                if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).TryGetProperty("data", out var data)
                    && data.ValueKind == JsonValueKind.Array)
                    models = [.. data.EnumerateArray().Take(64)
                        .Select(item => item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                        .OfType<string>().Where(name => ModelName().IsMatch(name)).Order(StringComparer.Ordinal)];
            }
            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { }
        }
        _llamaModels[nodeId] = (time.GetUtcNow(), models);
        return models;
    }

    // Model names go into the relay's YAML; these characters need no escaping there, in compose or in the collector.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/@+-]{0,199}\z")]
    private static partial Regex ModelName();
}
