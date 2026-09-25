using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lucia.NodeAgent;

using var cancellation = new CancellationTokenSource(args.FirstOrDefault() switch
{
    "managed-run" => Timeout.InfiniteTimeSpan,
    "wait-install" => TimeSpan.FromMinutes(30),
    "stage-managed" => TimeSpan.FromMinutes(10),
    _ => TimeSpan.FromMinutes(2)
});
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var termination = OperatingSystem.IsLinux()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); })
    : null;
try
{
    if (args is ["inspect"])
    {
        Console.WriteLine(AgentJson.SerializeReport(new HardwareInspector().Inspect()));
        return 0;
    }
    if (args.Length == 0 || args is ["--help"] or ["help"])
    {
        Console.WriteLine("""
            lucia-node-agent inspect
            lucia-node-agent discover --server https://host --ca-file /path/public-ca.crt [--connect-address IP] [--state-directory /run/lucia]
            lucia-node-agent status --server https://host --ca-file /path/public-ca.crt [--connect-address IP] [--state-directory /run/lucia]
            lucia-node-agent wait-install --server https://host --ca-file /path/public-ca.crt [--connect-address IP] [--state-directory /run/lucia]
            lucia-node-agent installation-guard --server https://host --ca-file /path/public-ca.crt [--connect-address IP] [--state-directory /run/lucia]
            lucia-node-agent stage-managed --server https://host --ca-file /path/public-ca.crt [--connect-address IP] [--state-directory /run/lucia]
            lucia-node-agent managed-run

            inspect/discover/status remain read-only with respect to disks and machine configuration.
            Installation handoff requires explicit owner approval and a fresh one-time server grant.
            managed-run accepts no options and only runs from the installed boot root.
            The agent never opens disks for writes or reboots. Progress is on stderr; secrets are never printed.
            """);
        return 0;
    }
    if (args is ["managed-run"])
    {
        await ManagedRunner.RunAsync(cancellation.Token);
        return 0;
    }
    if (args[0] is not ("discover" or "status" or "wait-install" or "installation-guard" or "stage-managed"))
        throw new NodeAgentException("Unknown command or unsupported options. Use --help.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 1; i < args.Length; i += 2)
    {
        if (args[i] is not ("--server" or "--ca-file" or "--connect-address" or "--state-directory")
            || i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1]))
            throw new NodeAgentException("Options must be known, non-duplicated name/value pairs. Use --help.");
    }
    if (!options.TryGetValue("--server", out var server) || !options.TryGetValue("--ca-file", out var caFile))
        throw new NodeAgentException("--server and --ca-file are required.");
    DiscoveryClient.ValidateServer(server);
    IPAddress? connectAddress = null;
    if (options.TryGetValue("--connect-address", out var connectValue)
        && (!IPAddress.TryParse(connectValue, out connectAddress)
            || connectAddress.Equals(IPAddress.Any) || connectAddress.Equals(IPAddress.IPv6Any)
            || connectAddress.Equals(IPAddress.Broadcast) || connectAddress.IsIPv6Multicast
            || (connectAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && connectAddress.GetAddressBytes()[0] is >= 224 and <= 239)))
        throw new NodeAgentException("--connect-address must be a unicast IP address, not a URL or hostname.");
    using var authority = DiscoveryClient.LoadAuthority(caFile);
    using var client = new DiscoveryClient(server, authority, connectAddress);
    using var state = new SecureStateDirectory(options.GetValueOrDefault("--state-directory") ?? "/run/lucia");
    using var stateLock = state.Lock();
    if (args[0] == "discover")
    {
        Console.Error.WriteLine("Reading hardware. No disks will be modified.");
        var reportJson = AgentJson.SerializeReport(new HardwareInspector().Inspect());
        using var key = state.LoadOrCreateKey();
        Console.Error.WriteLine("Sending signed inventory to the configured HTTPS origin.");
        var result = await client.RegisterAsync(reportJson, key, cancellation.Token);
        state.SaveCredentials(new(client.Server.AbsoluteUri, result.DeviceId, result.Token, result.ExpiresAt, result.VerificationCode));
        state.WritePrivate("inventory.json", System.Text.Encoding.UTF8.GetBytes(reportJson));
        Console.Error.WriteLine($"Physical verification code: {result.VerificationCode}");
        Console.Error.WriteLine("Discovery registered. Owner review is required. No installation or managed enrollment has occurred.");
        Console.WriteLine(JsonSerializer.Serialize(new DiscoverySummary(result.DeviceId, result.ExpiresAt, result.VerificationCode), AgentJson.Options));
    }
    else if (args[0] == "status")
    {
        var credentials = state.LoadCredentials();
        using var result = await client.GetStatusAsync(credentials, cancellation.Token);
        // Until the lifecycle is agreed, do not relay arbitrary server JSON (it may contain capabilities).
        Console.Error.WriteLine("Authenticated discovery status read succeeded. No installation action was taken.");
        Console.WriteLine(JsonSerializer.Serialize(new { deviceId = credentials.DeviceId, statusRead = true }, AgentJson.Options));
    }
    else
    {
        SecureStateDirectory.RequireRoot();
        var runner = new InstallationRunner(client, state, new HardwareInspector(), new DiskSafety());
        try
        {
            switch (args[0])
            {
                case "wait-install": await runner.WaitAsync(cancellation.Token); break;
                case "installation-guard": await runner.GuardAsync(cancellation.Token); break;
                case "stage-managed": await runner.StageAsync(caFile, cancellation.Token); break;
            }
        }
        catch
        {
            if (args[0] != "wait-install") await runner.ReportFailureAsync(cancellation.Token);
            throw;
        }
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("The node operation was cancelled or its deadline expired. No further action was authorized; owner inspection may be required.");
    return 130;
}
catch (NodeAgentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception)
{
    // Never print exception bodies, paths, HTTP bodies or credentials from failed external operations.
    Console.Error.WriteLine("The node agent could not complete safely. Check local hardware mounts, permissions, CA and connectivity.");
    return 1;
}
