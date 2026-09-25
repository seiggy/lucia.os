using System.Net;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

namespace Lucia.Homelab.Server.Domains;

public sealed record DomainRewritePlan(string Domain, string Answer, bool AlreadyPresent);
public sealed record DomainDnsReview(DomainRewritePlan[] Rewrites, string[] Blockers);

public sealed class DomainDnsPreflight(HttpClient http)
{
    public async Task<DomainDnsReview> ReviewAsync(DomainNamingPlan naming, CloudflareZone zone,
        string address, IReadOnlyList<AdGuardRewrite> existing, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        ct = deadline.Token;
        var blockers = new List<string>();
        var publishedNs = await Query(zone.Name, "NS", ct);
        var expected = zone.NameServers.Select(value => value.TrimEnd('.').ToLowerInvariant()).ToHashSet();
        var actual = publishedNs.Where(answer => answer.Type == 2)
            .Select(answer => answer.Data.TrimEnd('.').ToLowerInvariant()).ToHashSet();
        if (!actual.SetEquals(expected) || actual.Count == 0)
            blockers.Add("Public DNS delegation does not match this Cloudflare zone. Update the domain's nameservers before continuing.");
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in naming.CertificateNames.Select(value => value.TrimStart('*', '.')))
        {
            var part = name;
            while (part != zone.Name)
            {
                ancestors.Add(part);
                var dot = part.IndexOf('.');
                if (dot < 0) throw new InvalidDataException("Certificate name is outside its verified zone.");
                part = part[(dot + 1)..];
            }
            var challenge = "_acme-challenge." + name;
            ancestors.Add(challenge);
            if (ancestors.Count > 32) throw new ArgumentException("The selected namespace has too many nested DNS labels for v1.");
            if ((await Query(challenge, "CNAME", ct)).Any(answer => answer.Type == 5))
                blockers.Add($"The challenge name {challenge} is a CNAME. Delegated challenge layouts are not supported in v1.");
        }
        foreach (var ancestor in ancestors)
            if ((await Query(ancestor, "NS", ct)).Any(answer => answer.Type == 2))
                blockers.Add($"The namespace {ancestor} has an intervening DNS delegation. Use names served directly by the selected zone.");

        var rewrites = new List<DomainRewritePlan>();
        foreach (var name in naming.LocalHostnames.Distinct(StringComparer.Ordinal))
        {
            var same = existing.Where(entry => entry.Domain.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (same.Length > 1 || same.Any(entry => !entry.Enabled || entry.Answer != address))
                blockers.Add($"AdGuard already has a conflicting or duplicate rewrite for {name}. Review it in AdGuard; Lucia will not overwrite it.");
            var wildcard = existing.Where(entry => entry.Domain.StartsWith("*.", StringComparison.Ordinal)
                && name.EndsWith(entry.Domain[1..].TrimEnd('.'), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (wildcard.Any(entry => !entry.Enabled || !IPAddress.TryParse(entry.Answer, out _) || entry.Answer != address))
                blockers.Add($"An AdGuard wildcard or CNAME rule overlaps {name}. Review that rule before continuing.");
            rewrites.Add(new(name, address, same.Length == 1 && same[0].Enabled && same[0].Answer == address));
        }
        return new(rewrites.ToArray(), blockers.Distinct().ToArray());
    }

    private sealed record Answer(int Type, string Data);

    private async Task<Answer[]> Query(string name, string type, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Public DNS delegation could not be checked. Try again later.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[65537];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > 65536) throw new InvalidDataException("The DNS answer exceeded its size limit.");
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (!root.TryGetProperty("Status", out var status) || status.GetInt32() is not (0 or 3))
            throw new InvalidOperationException("The public DNS resolver could not validate this namespace.");
        if (root.TryGetProperty("TC", out var truncated) && truncated.GetBoolean())
            throw new InvalidOperationException("The public DNS response was incomplete.");
        if (!root.TryGetProperty("Answer", out var answers)) return [];
        if (answers.ValueKind != JsonValueKind.Array || answers.GetArrayLength() > 64)
            throw new InvalidDataException("The public DNS response is invalid.");
        return answers.EnumerateArray().Select(answer =>
            new Answer(answer.GetProperty("type").GetInt32(), answer.GetProperty("data").GetString()
                ?? throw new InvalidDataException("A DNS answer was empty."))).ToArray();
    }
}
