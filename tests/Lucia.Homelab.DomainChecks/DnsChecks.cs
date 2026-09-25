using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;

internal static class DnsChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        const string name = "lucia.lab.example.com";
        const ushort transaction = 1234;
        var packet = new List<byte>(new byte[12]);
        foreach (var label in name.Split('.'))
        {
            packet.Add((byte)label.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(label));
        }
        packet.AddRange([0, 0, 1, 0, 1, 0xc0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 168, 0, 222]);
        var bytes = packet.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes, transaction);
        bytes[2] = 0x81;
        bytes[3] = 0x80;
        bytes[5] = 1;
        bytes[7] = 1;
        check(DomainConnectivity.ContainsAnswer(bytes, transaction, name, "192.168.0.222"), "A valid compressed DNS A answer was rejected.");
        check(!DomainConnectivity.ContainsAnswer(bytes, transaction + 1, name, "192.168.0.222"), "Wrong DNS transaction was accepted.");
        check(!DomainConnectivity.ContainsAnswer(bytes, transaction, "other.lab.example.com", "192.168.0.222"), "Wrong DNS question was accepted.");
        check(!DomainConnectivity.ContainsAnswer(bytes, transaction, name, "192.168.0.223"), "Wrong DNS address was accepted.");
        check(!DomainConnectivity.ContainsAnswer(bytes.AsSpan(0, bytes.Length - 1), transaction, name, "192.168.0.222"), "Truncated DNS RDATA was accepted.");
        var truncated = (byte[])bytes.Clone();
        truncated[2] |= 2;
        check(!DomainConnectivity.ContainsAnswer(truncated, transaction, name, "192.168.0.222"), "TC-marked DNS response was accepted.");
        var loop = (byte[])bytes.Clone();
        loop[12] = 0xc0; loop[13] = 12;
        check(!DomainConnectivity.ContainsAnswer(loop, transaction, name, "192.168.0.222"), "DNS compression cycle was accepted.");
        var failure = (byte[])bytes.Clone();
        failure[3] |= 2;
        check(!DomainConnectivity.ContainsAnswer(failure, transaction, name, "192.168.0.222"), "DNS SERVFAIL was accepted.");

        var handler = new FakeDnsHttp();
        using var client = new HttpClient(handler);
        var service = new DomainDnsPreflight(client);
        var naming = DomainNames.Plan(new("example.com", "lab", "atlas"), "example.com");
        var zone = new CloudflareZone(new string('a', 32), "example.com", "active", ["a.ns.cloudflare.com", "b.ns.cloudflare.com"]);
        var clean = await service.ReviewAsync(naming, zone, "192.168.0.222", [], default);
        check(clean.Blockers.Length == 0 && clean.Rewrites.Length == 3, "Valid Cloudflare authority did not produce the three exact local rewrites.");
        handler.Delegated = true;
        var delegated = await service.ReviewAsync(naming, zone, "192.168.0.222", [], default);
        check(delegated.Blockers.Any(message => message.Contains("delegation", StringComparison.Ordinal)), "Challenge NS delegation did not block the stock Certbot flow.");
        handler.Delegated = false;
        handler.Alias = true;
        var alias = await service.ReviewAsync(naming, zone, "192.168.0.222", [], default);
        check(alias.Blockers.Any(message => message.Contains("CNAME", StringComparison.Ordinal)), "Aliased ACME challenge was accepted.");
        handler.Alias = false;
        var collision = await service.ReviewAsync(naming, zone, "192.168.0.222",
            [new(name, "192.168.0.99"), new("*.lab.example.com", "192.168.0.98")], default);
        check(collision.Blockers.Length >= 2, "Conflicting exact/wildcard rewrites did not block review.");
        var matching = await service.ReviewAsync(naming, zone, "192.168.0.222", [new(name, "192.168.0.222")], default);
        check(matching.Rewrites.Single(item => item.Domain == name).AlreadyPresent, "Existing exact rewrite was not preserved.");
    }

    private sealed class FakeDnsHttp : HttpMessageHandler
    {
        public bool Delegated { get; set; }
        public bool Alias { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "cloudflare-dns.com")
                throw new InvalidOperationException("Public preflight left its read-only fixed DNS endpoint.");
            var query = request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
            object[] answers = query["name"] == "example.com" && query["type"] == "NS"
                ? [new { type = 2, data = "a.ns.cloudflare.com." }, new { type = 2, data = "b.ns.cloudflare.com." }]
                : query["name"] == "_acme-challenge.lab.example.com" && query["type"] == "NS" && Delegated
                    ? [new { type = 2, data = "external.example.net." }]
                    : query["name"] == "_acme-challenge.lab.example.com" && query["type"] == "CNAME" && Alias
                        ? [new { type = 5, data = "challenge.example.net." }] : [];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { Status = 0, TC = false, Answer = answers }), Encoding.UTF8, "application/dns-json") });
        }
    }
}
