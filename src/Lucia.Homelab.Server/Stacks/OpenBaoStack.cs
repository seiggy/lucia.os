using System.Text.Json;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Onboarding;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>The isolated Spark vault: owners choose start or stop, never its image, mounts or unseal shares.</summary>
public sealed class OpenBaoStack(IOptions<HardwareOnboardingOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal string Folder => Path.Combine(Path.GetDirectoryName(options.Value.StateDirectory)!, "openbao-stack");
    internal sealed record Request(int SchemaVersion, long Generation, string Desired);
    internal sealed record Backup(string? State, DateTimeOffset? LastSuccessAt);
    internal sealed record Status(int SchemaVersion, bool Configured, long Generation, string State, string? Message,
        bool? Initialized, bool? Sealed, DateTimeOffset CheckedAt, Backup? Backup, string Image, string? Url);

    internal static bool Fresh(Status status, DateTimeOffset now) =>
        status.CheckedAt <= now.AddSeconds(10) && status.CheckedAt > now.AddMinutes(-1);

    private T? Read<T>(string name)
    {
        var path = Path.Combine(Folder, name);
        DomainOnboardingStore.RejectLinks(path);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<T>(CertbotFiles.ReadBounded(path, 16 * 1024), DomainOnboardingStore.Json)
                ?? throw new InvalidDataException("OpenBao stack state is empty.")
            : default;
    }

    private Status? ReadStatus()
    {
        var status = Read<Status>("status.json");
        if (status is not null && (status.SchemaVersion != 1 || status.Generation < -1
            || status.State is not ("starting" or "running" or "sealed" or "uninitialized" or "missing" or "stopped" or "failed")
            || (status.Url is not null && (!Uri.TryCreate(status.Url, UriKind.Absolute, out var url)
                || url.Scheme != "https" || url.UserInfo.Length != 0))))
            throw new InvalidDataException("OpenBao stack status is invalid.");
        return status;
    }

    public async Task<object> Get(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var status = ReadStatus();
            if (status is null) return new { configured = false, workerReady = false };
            var request = Read<Request>("request.json");
            var ready = Fresh(status, time.GetUtcNow());
            var state = !ready ? "unknown" : status.State == "failed" ? "failed" : request is not null && request.Generation != status.Generation
                ? request.Desired == "running" ? "starting" : "stopping" : status.State;
            return new
            {
                status.Configured, workerReady = ready, state, status.Message, status.Initialized, status.Sealed,
                status.CheckedAt, status.Backup, status.Image, status.Url,
                desired = request?.Desired,
            };
        }
        finally { _gate.Release(); }
    }

    public async Task<object> Set(string desired, CancellationToken ct)
    {
        if (desired is not ("running" or "stopped"))
            throw new HardwareOnboardingException(400, "invalid_action", "OpenBao supports only start and stop. It cannot be deleted or upgraded here.");
        await _gate.WaitAsync(ct);
        try
        {
            var status = ReadStatus();
            if (status is not { Configured: true })
                throw new HardwareOnboardingException(404, "openbao_not_configured", "Recover and install the OpenBao stack on the Spark first.");
            if (!Fresh(status, time.GetUtcNow()))
                throw new HardwareOnboardingException(409, "openbao_worker_offline", "The Spark's OpenBao management service is offline. Check lucia-openbao-stack.service.");
            var previous = Read<Request>("request.json");
            var generation = checked(Math.Max(previous?.Generation ?? 0, status.Generation) + 1);
            await DomainOnboardingStore.WriteJson(Path.Combine(Folder, "request.json"), new Request(1, generation, desired), ct);
        }
        finally { _gate.Release(); }
        return await Get(ct);
    }
}
