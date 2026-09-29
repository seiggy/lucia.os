using System.Net;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;

namespace Lucia.Homelab.Server.Stacks;

/// <param name="State"><c>Held</c>, <c>InUse</c> (another device answers for it) or <c>NoSubnet</c>.</param>
public sealed record NodeAddressStatus(string Address, string State, string? Message = null);
/// <param name="Address">Empty or missing removes the app's address.</param>
public sealed record StackAddressRequest(string? Address);

/// <summary>
/// A stack's own IPv4 address, such as a DNS server's. Only the node the stack has settled on takes it, and only while
/// the stack runs, so a move always gives it up on the old node before the new one claims it.
/// </summary>
public sealed partial class StackStore
{
    internal const string AddressVariable = "LUCIA_ADDRESS";

    /// <summary>Gives an app its own address, changes it or removes it. The stack reapplies so its ports bind to the new one.</summary>
    public async Task<object> SaveStackAddress(string name, StackAddressRequest request, string actor, CancellationToken ct)
    {
        var names = await NodeNames(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var stacks = ReadUnlocked().ToList();
            var stack = Find(stacks, name);
            RequireSettled(stack);
            var address = await ValidAddress(request.Address ?? "", name, stacks, ct);
            if (address is null && StackCatalog.Apps.FirstOrDefault(app => app.Id == stack.Manifest.Template?.Id) is { UsesAddress: true } app)
                throw new HardwareOnboardingException(400, "address_required", $"{app.Name} needs an address of its own on your network.");
            if (address == stack.Manifest.Address) return Summary(stack, names);
            var changed = stack with
            {
                Manifest = stack.Manifest with { Address = address }, Revision = stack.Revision + 1, UpdatedAt = time.GetUtcNow(), UpdatedBy = actor,
            };
            stacks[stacks.IndexOf(stack)] = changed;
            await Write(stacks, ct);
            return Summary(changed, names);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The address, checked: a private dotted-quad IPv4 that no other stack or managed server uses.</summary>
    private async Task<string?> ValidAddress(string? value, string name, IEnumerable<StoredStack> stacks, CancellationToken ct)
    {
        if (ValidAddress(value) is not { } address) return null;
        if (stacks.FirstOrDefault(stack => stack.Name != name && stack.Manifest.Address == address) is { } other)
            throw new HardwareOnboardingException(409, "address_in_use", $"{other.Name} already uses {address}.");
        if ((await nodes.Addresses(ct)).FirstOrDefault(item => item.Address == address) is { } server)
            throw new HardwareOnboardingException(409, "address_in_use", $"{address} is {server.Hostname}'s own address.");
        return address;
    }

    internal static string? ValidAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var address = value.Trim();
        if (!QuadPattern().IsMatch(address) || !IPAddress.TryParse(address, out var ip) || !DomainConnectivity.IsPrivate(ip)
            || address.EndsWith(".0", StringComparison.Ordinal) || address.EndsWith(".255", StringComparison.Ordinal))
            throw new HardwareOnboardingException(400, "invalid_address",
                "The address must be a private IPv4 address on your network, such as 192.168.1.53.");
        return address;
    }

    /// <summary>The environment the node writes, with the address for the compose's port bindings and the app's web addresses.
    /// The address is always set, so compose parses when stopped.</summary>
    internal static string NodeEnv(string env, StackManifest manifest, string? ns = null)
    {
        var extra = (manifest.Address is { } address ? $"{AddressVariable}={address}\n" : "") + RouteEnv(manifest, ns);
        return extra.Length == 0 ? env : $"{env.TrimEnd('\n')}\n{extra}".TrimStart('\n');
    }

    private static void ValidateAddresses(NodeAddressStatus[]? addresses)
    {
        if (addresses is null) return;
        HardwareInventoryValidation.Require(addresses.Length <= 64, "The address report is oversized.");
        foreach (var item in addresses)
        {
            HardwareInventoryValidation.Require(QuadPattern().IsMatch(item.Address ?? "") && item.State is "Held" or "InUse" or "NoSubnet",
                "The address report is invalid.");
            HardwareInventoryValidation.Text(item.Message, 512);
        }
    }

    [GeneratedRegex(@"\A(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\z")]
    private static partial Regex QuadPattern();
}
