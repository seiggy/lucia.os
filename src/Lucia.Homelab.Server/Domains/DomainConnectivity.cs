using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lucia.Homelab.Server.Host;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lucia.Homelab.DomainChecks")]

namespace Lucia.Homelab.Server.Domains;

public sealed class DomainProbeException(string code, string target, string message, int? statusCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public string Target { get; } = target;
    public int? StatusCode { get; } = statusCode;
}

public static class DomainConnectivity
{
    public static async Task VerifyGateway(string origin, string address, string path,
        HostAuthenticationOptions legacyTrust, bool publicTrust, Func<JsonElement, bool> validate, CancellationToken ct,
        string? expectedCertificateSha256 = null)
    {
        using var root = publicTrust ? null : legacyTrust.ReadCertificateAuthority();
        using var handler = root is null ? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }
            : HostAuthenticationOptions.CreateBackchannelHandler(root);
        handler.UseProxy = false;
        var target = new Uri(origin).GetLeftPart(UriPartial.Authority);
        var certificateMismatch = false;
        SslPolicyErrors? certificateErrors = null;
        if (expectedCertificateSha256 is not null)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                certificateErrors = errors;
                certificateMismatch = certificate is not null
                    && !certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(expectedCertificateSha256, StringComparison.OrdinalIgnoreCase);
                return errors == SslPolicyErrors.None && certificate is not null && !certificateMismatch;
            };
        handler.ConnectTimeout = TimeSpan.FromSeconds(5);
        handler.ConnectCallback = async (connection, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Parse(address), connection.DnsEndPoint.Port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 65536 };
        try
        {
            using var response = await client.GetAsync(origin + path, ct);
            if (!response.IsSuccessStatusCode)
                throw new DomainProbeException("gateway_http_error", target,
                    $"The HTTPS check at {target} returned HTTP {(int)response.StatusCode}, not a successful service response.", (int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            if (!validate(json.RootElement))
                throw new DomainProbeException("gateway_response_invalid", target,
                    $"HTTPS connected to {target}, but the response did not identify the expected Lucia or Authentik service.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { throw new DomainProbeException("gateway_timeout", target, $"The HTTPS check at {target} timed out."); }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            var code = certificateMismatch ? "gateway_certificate_not_served"
                : certificateErrors?.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) == true ? "gateway_certificate_name"
                : "gateway_certificate_trust";
            throw new DomainProbeException(code, target, certificateMismatch
                ? $"The gateway at {target} did not serve the newly issued certificate."
                : $"The certificate served at {target} did not pass hostname and trust validation.");
        }
        catch (HttpRequestException)
        { throw new DomainProbeException("gateway_unreachable", target, $"Lucia could not connect to the HTTPS gateway at {target}."); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new DomainProbeException("gateway_response_invalid", target, $"The HTTPS service at {target} returned an unexpected response."); }
    }

    public static async Task VerifyLocalAddress(string name, string expectedAddress, string dnsOrigin, int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("AdGuard did not report a valid DNS port.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var serverHost = new Uri(dnsOrigin).Host;
        var addresses = await Dns.GetHostAddressesAsync(serverHost, deadline.Token);
        if (addresses.Length == 0 || addresses.Any(address => !IsPrivateDnsServer(address)))
            throw new InvalidOperationException("AdGuard's DNS endpoint no longer resolves exclusively to the private network.");
        var server = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var id = (ushort)RandomNumberGenerator.GetInt32(65536);
        var question = new List<byte>();
        foreach (var label in DomainNames.Hostname(name).Split('.'))
        {
            question.Add((byte)label.Length);
            question.AddRange(Encoding.ASCII.GetBytes(label));
        }
        question.AddRange([0, 0, 1, 0, 1]);
        var packet = new byte[12 + question.Count];
        BinaryPrimitives.WriteUInt16BigEndian(packet, id);
        packet[2] = 1;
        packet[5] = 1;
        question.CopyTo(packet, 12);
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(server, port, deadline.Token);
        await socket.SendAsync(packet, SocketFlags.None, deadline.Token);
        var response = new byte[4096];
        var length = await socket.ReceiveAsync(response, SocketFlags.None, deadline.Token);
        if (!ContainsAnswer(response.AsSpan(0, length), id, name, expectedAddress))
            throw new InvalidOperationException("AdGuard did not resolve the selected hostname to the reviewed Spark address.");
        var normal = await Dns.GetHostAddressesAsync(name, deadline.Token);
        if (normal.Length == 0 || normal.Any(address => address.ToString() != expectedAddress))
            throw new InvalidOperationException("The Spark's normal DNS resolver does not exclusively return the reviewed address. Check DHCP, DNS forwarding, and conflicting AAAA records.");
    }

    internal static bool ContainsAnswer(ReadOnlySpan<byte> packet, ushort id, string name, string address)
    {
        if (packet.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(packet) != id
            || (packet[2] & 0x82) != 0x80 || (packet[3] & 15) != 0 || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1)
            return false;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        if (answers > 64) return false;
        try
        {
            var offset = 12;
            if (!ReadName(packet, ref offset).Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
            if (offset + 4 > packet.Length || BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]) != 1
                || BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]) != 1) return false;
            offset += 4;
            for (var i = 0; i < answers; i++)
            {
                var answerName = ReadName(packet, ref offset);
                if (offset + 10 > packet.Length) return false;
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
                var klass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
                var size = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
                offset += 10;
                if (offset + size > packet.Length) return false;
                if (type == 1 && klass == 1 && size == 4 && answerName.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && new IPAddress(packet.Slice(offset, 4)).ToString() == address)
                    return true;
                offset += size;
            }
        }
        catch (InvalidDataException) { }
        return false;
    }

    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var current = offset;
        var next = -1;
        var labels = new List<string>();
        for (var hops = 0; hops < 128; hops++)
        {
            if (current >= packet.Length) throw new InvalidDataException("Invalid DNS name.");
            var length = packet[current++];
            if (length == 0)
            {
                offset = next < 0 ? current : next;
                var result = string.Join('.', labels);
                if (result.Length > 253) throw new InvalidDataException("DNS name is oversized.");
                return result;
            }
            if ((length & 0xc0) == 0xc0)
            {
                if (current >= packet.Length) throw new InvalidDataException("Invalid DNS pointer.");
                next = next < 0 ? current + 1 : next;
                current = ((length & 0x3f) << 8) | packet[current];
                continue;
            }
            if (length > 63 || current + length > packet.Length) throw new InvalidDataException("Invalid DNS label.");
            labels.Add(Encoding.ASCII.GetString(packet.Slice(current, length)));
            current += length;
        }
        throw new InvalidDataException("DNS compression exceeded its limit.");
    }

    public static bool IsPrivate(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork
        && new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Select(IPNetwork.Parse).Any(range => range.Contains(address));

    private static bool IsPrivateDnsServer(IPAddress address) => IsPrivate(address)
        || (address.AddressFamily == AddressFamily.InterNetworkV6 && IPNetwork.Parse("fc00::/7").Contains(address));
}
