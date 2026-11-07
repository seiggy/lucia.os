using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

internal sealed record OpenBaoDnsIntent(int SchemaVersion, Guid ProfileId, string Origin);

internal static class OpenBaoDnsRegistration
{
    internal const string FileName = "openbao-access.json";

    internal static AdGuardRewrite[] Read(string root, DomainSetupJob job)
    {
        var path = Path.Combine(root, FileName);
        DomainOnboardingStore.RejectLinks(path);
        if (!File.Exists(path)) return [];
        var intent = JsonSerializer.Deserialize<OpenBaoDnsIntent>(CertbotFiles.ReadBounded(path, 4096), DomainOnboardingStore.Json);
        if (intent is not { SchemaVersion: 1 } || intent.ProfileId != job.Id)
            throw new InvalidDataException("OpenBao DNS registration does not match the active domain profile.");
        var naming = job.Plan.Naming;
        var host = DomainNames.ServiceHost(intent.Origin, naming.Domain);
        if (intent.Origin != "https://" + host || !host.EndsWith("." + naming.Namespace, StringComparison.Ordinal)
            || host[..^(naming.Namespace.Length + 1)].Contains('.')
            || naming.LocalHostnames.Contains(host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("OpenBao DNS registration requires its own HTTPS hostname in the active namespace.");
        return [new(host, job.Plan.IngressAddress)];
    }
}
