using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Packages;
using Lucia.Homelab.Server.Platform;

namespace Lucia.Homelab.Server.Assistant;

/// <summary>
/// The Spark's platform images (Authentik, the gateway, the CA, PostgreSQL, OpenLDAP and the telemetry relay) and its NVIDIA
/// device list. The model names services, never images, tags or digests: Lucia picks the version it found, and the Spark's
/// worker checks it again with the registry and the image's policy before anything restarts.
/// </summary>
public sealed partial class AssistantTools
{
    private static readonly TimeSpan PlatformWait = TimeSpan.FromMinutes(15), DevicesWait = TimeSpan.FromMinutes(5);

    private IEnumerable<AssistantTool> PlatformTools()
    {
        if (platformImages is null) yield break;
        var images = platformImages;
        yield return Tool(ToolTier.Read, "list_platform_image_updates",
            "Lists the Spark's platform images (Authentik, the Traefik gateway, step-ca, PostgreSQL, OpenLDAP, the OpenTelemetry relay and "
            + "exporters, and the gateway's CrowdSec plugin): the version each runs, its policy class, the newer versions Lucia offers with "
            + "their kind (rebuild, patch, minor, release-line, major), whether each can be applied, its release notes, how many days it has "
            + "waited, what an update restarts, the update service's state and the NVIDIA device list's.",
            () => PlatformList(images));
        yield return Tool(ToolTier.Change, "apply_platform_image_updates",
            "Updates Spark platform images by service name, from list_platform_image_updates, as the Spark updates page does. Lucia picks each "
            + "service's patch first; patchOnly takes nothing else. Authentik's next release line goes on its own and its database is backed up "
            + "first. Updating Authentik, the gateway, PostgreSQL or OpenLDAP restarts every web route for about 10 to 15 seconds; telemetry "
            + "services don't touch traffic. Allowed while Qwen3.8-Flash-Next runs, but slower then; the result carries a warning. Waits for the Spark's result, which rolls back on failure.",
            async Task<object?> ([Description("Service names such as authentik, gateway or database, from list_platform_image_updates.")] string[] services,
                [Description("Only patches and rebuilds of the same tag, the low-risk set.")] bool patchOnly = false, CancellationToken ct = default) =>
                await PlatformApply(images, services, patchOnly, ct));
        yield return Tool(ToolTier.Change, "refresh_nvidia_device_list",
            "Generates the Spark's NVIDIA device list (CDI) again, as Refresh NVIDIA device list on the Spark updates page does. Use it when "
            + "list_platform_image_updates or get_spark_updates says the list is out of date: until then GPU containers (Qwen, Lucia's "
            + "GPU exporter) can't start. Nothing restarts.",
            async Task<object?> (CancellationToken ct) => await RefreshDevices(ct));
    }

    private object PlatformList(PlatformImageUpdates images)
    {
        var listed = JsonSerializer.SerializeToNode(images.Get(), AssistantStream.Json) as JsonObject ?? [];
        if (listed["status"] is JsonObject status) status.Remove("history");
        var now = DateTimeOffset.UtcNow;
        foreach (var offer in (listed["images"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(row => (row["offers"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            offer.Remove("digest");
            if (offer["firstSeenAt"] is JsonValue seen && seen.TryGetValue(out DateTimeOffset at))
                offer["waitingDays"] = Math.Max(0, (int)(now - at).TotalDays);
            offer.Remove("firstSeenAt");
        }
        foreach (var row in (listed["images"] as JsonArray ?? []).OfType<JsonObject>()) row.Remove("digest");
        listed["gpuDevices"] = GpuDevices()?.DeepClone();
        listed["qwenRunning"] = sparkModel?.Serving() is not null;
        return listed;
    }

    private JsonObject? GpuDevices() => sparkPackages?.ReadWorker().Status?["gpuDevices"] as JsonObject;

    private const string QwenWarning = "Qwen3.8-Flash-Next is running on the Spark and holds most of its memory, so pulling images and restarting "
        + "services is slower and more likely to fail. The update went ahead.";

    private async Task<object?> PlatformApply(PlatformImageUpdates images, string[] services, bool patchOnly, CancellationToken ct)
    {
        if (services is not { Length: > 0 } || services.Length > PlatformImages.BuiltIn.Length)
            throw new AssistantException(400, "invalid_services", "Name one or more services from list_platform_image_updates.");
        var warning = sparkModel?.Serving() is not null ? QwenWarning : null;
        if (GpuDevices() is { } devices && devices["current"]?.GetValue<bool>() == false)
            throw new AssistantException(409, "gpu_devices_stale", "The NVIDIA device list is out of date. Run refresh_nvidia_device_list first.");
        JsonObject requested;
        try { requested = JsonSerializer.SerializeToNode(await images.Apply(new(null, services, patchOnly), ct), AssistantStream.Json)!.AsObject(); }
        catch (PackageUpdatesException e) { throw new AssistantException(e.StatusCode, e.Code, e.Message); }
        var generation = (long?)requested["generation"];
        var deadline = DateTimeOffset.UtcNow + PlatformWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (images.ReadStatus() is { } status && (long?)status["generation"] == generation && (string?)status["state"] is { } state && state != "working")
                return new { Requested = requested, Warning = warning, Result = new { State = state, Message = (string?)status["message"], RolledBack = (bool?)status["rolledBack"],
                    Backup = (string?)status["backup"] } };
        }
        return new { Requested = requested, Warning = warning, Result = new { State = "working", Message = "The Spark is still updating. Check list_platform_image_updates in a few minutes." } };
    }

    private async Task<object?> RefreshDevices(CancellationToken ct)
    {
        if (sparkPackages is null) throw new AssistantException(503, "spark_unavailable", "Spark updates aren't available.");
        string id;
        try { id = (string)JsonSerializer.SerializeToNode(await sparkPackages.RefreshGpuDevicesAsync(ct), AssistantStream.Json)!["id"]!; }
        catch (PackageUpdatesException e) { throw new AssistantException(e.StatusCode, e.Code, e.Message); }
        var deadline = DateTimeOffset.UtcNow + DevicesWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var status = sparkPackages.ReadWorker().Status;
            if ((status?["history"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => (string?)item["id"] == id) is { } done)
                return new { State = (string?)done["state"], Message = (string?)done["message"], GpuDevices = status!["gpuDevices"]?.DeepClone() };
        }
        return "The Spark is still refreshing the NVIDIA device list. Check get_spark_updates in a minute.";
    }
}
