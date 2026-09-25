using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Boot;

public sealed class BootOptions
{
    public bool Enabled { get; set; }
    public string ControlDirectory { get; set; } = "";
    public string[] AllowedNetworks { get; set; } = [];

    public void Validate()
    {
        if (!Enabled)
            return;
        ValidateControlDirectory(ControlDirectory);
        if (AllowedNetworks.Length == 0 || AllowedNetworks.Any(value =>
                !IPNetwork.TryParse(value, out var network)
                || network.BaseAddress.AddressFamily != AddressFamily.InterNetwork
                || !new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }
                    .Select(IPNetwork.Parse).Any(range =>
                        network.PrefixLength >= range.PrefixLength && range.Contains(network.BaseAddress))))
            throw Invalid("AllowedNetworks must contain explicit private IPv4 provisioning subnets.");
    }

    internal static void ValidateControlDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)
            || Path.TrimEndingDirectorySeparator(directory) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(directory)!))
            throw Invalid("ControlDirectory must be an absolute, dedicated directory.");
    }

    public bool Allows(IPAddress? address)
    {
        if (!Enabled || address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return AllowedNetworks.Any(value => IPNetwork.Parse(value).Contains(address));
    }

    private static OptionsValidationException Invalid(string message) =>
        new(nameof(BootOptions), typeof(BootOptions), [$"Boot: {message}"]);
}
