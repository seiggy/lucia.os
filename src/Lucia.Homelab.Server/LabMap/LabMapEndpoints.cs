using System.Security.Cryptography;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.LabMap;

public static class LabMapEndpoints
{
    public static void AddLabMap(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ClientOverrideStore>();
        builder.Services.AddSingleton<SiteDistrictStore>();
        builder.Services.AddSingleton<CategoryStore>();
        builder.Services.AddSingleton<SnmpSettingsStore>();
        builder.Services.AddSingleton<PrometheusQuery>();
        builder.Services.AddSingleton<LabTraffic>();
        builder.Services.AddSingleton<LabDnsNames>();
        builder.Services.AddSingleton<LabAsn>();
        builder.Services.AddSingleton<LabMapService>();
        builder.Services.AddHostedService<LabCollectorSync>();
    }

    /// <summary>Map behind host authentication and the global CSRF middleware.</summary>
    public static void MapLabMap(this WebApplication app)
    {
        var map = app.MapGroup("/api/host/lab-map").WithTags("Lab map").RequireAuthorization("HostOwner").AddEndpointFilter<UniFiManagementFilter>();
        map.MapGet("", (LabMapService service, CancellationToken ct) => service.Get(ct));
        map.MapGet("/live", (LabMapService service, string? window, int? sites, CancellationToken ct) => service.Live(window, sites, ct));
        map.MapPut("/clients/{mac}", (string mac, LabClientOverrideRequest request, LabMapService service, CancellationToken ct) =>
            service.SetGroup(mac, request.Group, ct));
        map.MapPut("/sites", async (LabSiteDistrictRequest request, SiteDistrictStore store, CancellationToken ct) =>
        {
            var (key, district) = await store.Set(request.Site, request.District, ct);
            return new LabSiteDistrictRequest("dest:" + key, district);
        });
        map.MapPut("/categories", (LabCategoryRequest request, LabMapService service, CancellationToken ct) =>
            service.SetCategory(request.Object, request.Category, ct));

        var snmp = app.MapGroup("/api/host/unifi/snmp").WithTags("UniFi").RequireAuthorization("HostOwner").AddEndpointFilter<UniFiManagementFilter>();
        snmp.MapGet("", (SnmpSettingsStore store, LabTraffic traffic, CancellationToken ct) => Status(store, traffic, ct));
        snmp.MapPut("", async (HttpContext context, SnmpSettingsStore store, LabTraffic traffic, CancellationToken ct) =>
        {
            const int limit = 4 * 1024;
            if (context.Request.ContentLength > limit || !context.Request.HasJsonContentType())
                throw new UniFiException(400, "invalid_snmp_request", "Send the SNMP settings as a JSON object under 4 KiB.");
            var body = await AdGuardTransport.ReadBoundedAsync(context.Request.Body, limit, ct);
            try
            {
                // Avoid model binding diagnostics and duplicate or unknown secret fields.
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 2 });
                var root = document.RootElement;
                string[] names = ["username", "authProtocol", "authPassphrase", "privProtocol", "privPassphrase"];
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != names.Length
                    || root.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))
                    || names.Any(name => AdGuardValidation.String(root, name) is null))
                    throw new UniFiException(400, "invalid_snmp_request",
                        "Supply exactly username, authProtocol, authPassphrase, privProtocol and privPassphrase.");
                await store.Save(new()
                {
                    Username = AdGuardValidation.String(root, "username")!, AuthProtocol = AdGuardValidation.String(root, "authProtocol")!,
                    AuthPassphrase = AdGuardValidation.String(root, "authPassphrase")!, PrivProtocol = AdGuardValidation.String(root, "privProtocol")!,
                    PrivPassphrase = AdGuardValidation.String(root, "privPassphrase")!,
                }, ct);
                return await Status(store, traffic, ct);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
        });
        snmp.MapDelete("", (SnmpSettingsStore store, CancellationToken ct) => store.Delete(ct));
    }

    private static async Task<SnmpStatus> Status(SnmpSettingsStore store, LabTraffic traffic, CancellationToken ct)
    {
        var status = await store.Status(ct);
        if (!status.Configured) return status;
        var reading = await traffic.Read(ct);
        return status with
        {
            LastScrapeAt = reading?.LastSnmpAt,
            Message = reading is null ? "Install and start the Observability app to collect SNMP readings."
                : reading.LastSnmpAt is null ? "No SNMP readings yet. Enable SNMP v3 with this user under UniFi's Settings → System → Advanced." : null,
        };
    }
}
