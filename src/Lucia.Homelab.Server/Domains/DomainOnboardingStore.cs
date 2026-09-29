using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucia.Homelab.Server.Domains;

public sealed class DomainOnboardingOptions
{
    public string StateDirectory { get; set; } = DomainActivationConfiguration.DefaultStateDirectory;
    public string? IngressAddress { get; set; }
    public string GatewayDirectory { get; set; } = "";
    public string CertificateGatewayDirectory { get; set; } = "/domain-certificates";
    public string? InstallationId { get; set; }
}

public sealed record DomainPlanRequest(
    string ZoneId, string Subdomain, string SparkName, DomainServiceUrls? ServiceUrls,
    string IngressAddress, string Email, int PropagationSeconds = 60);

public sealed record DomainSetupPlan(
    Guid Id, string ReviewHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string ZoneId, string AccountId, DomainNamingPlan Naming, string IngressAddress, string Email,
    int PropagationSeconds, string TermsUrl, string AdGuardOrigin, string AdGuardUsername,
    DomainRewritePlan[] Rewrites, string[] Blockers, string[] Warnings);

public sealed record DomainSetupEvent(DateTimeOffset At, string Phase, string Message);
public sealed record DomainSetupJob(
    Guid Id, string State, string Phase, string Message, string Actor,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DomainSetupPlan Plan,
    DomainSetupEvent[] Events, string[] CreatedRewrites, CertbotCertificateReceipt? Certificate = null,
    DateTimeOffset? NextRenewalAt = null, string? RenewalError = null, DomainRuntimeProfile? ActivationProfile = null,
    string[]? PendingRewrites = null, bool RecoveryRequired = false, string? IngressBefore = null,
    string? IngressPublished = null, bool RegistrationRequested = false, DateTimeOffset? NextActivationCheckAt = null,
    DomainDiagnosis? Diagnosis = null, DomainSupportReport? Support = null, DomainFailure? Failure = null,
    DateTimeOffset? RenewalCheckedAt = null, string? RenewalOutcome = null, bool Public = false, bool? PublicRequested = null,
    string? PublicError = null);
public sealed record DomainSetupDocument(int Version, DomainSetupPlan? Plan, DomainSetupJob? Job);

public sealed class DomainOnboardingStore : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 16
    };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FileStream _lease;
    public string Root { get; }

    public DomainOnboardingStore(DomainOnboardingOptions options)
    {
        if (!Path.IsPathFullyQualified(options.StateDirectory) || options.StateDirectory == Path.GetPathRoot(options.StateDirectory))
            throw new ArgumentException("Domain onboarding requires a dedicated absolute state directory.");
        Root = Path.GetFullPath(options.StateDirectory);
        EnsureDirectory(Root);
        _path = Path.Combine(Root, "workflow.json");
        var lockPath = Path.Combine(Root, ".workflow.lock");
        RejectLinks(lockPath);
        _lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var exclusive = false;
        try
        {
            using var competing = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { exclusive = true; }
        if (!exclusive)
        {
            _lease.Dispose();
            throw new IOException("Domain workflow storage must support exclusive locks.");
        }
    }

    public async Task<DomainSetupDocument> Read(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadFile(ct); }
        finally { _gate.Release(); }
    }

    public async Task<DomainSetupDocument> Update(Func<DomainSetupDocument, DomainSetupDocument> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await ReadFile(ct);
            var next = change(current);
            await WriteJson(_path, next, ct);
            return next;
        }
        finally { _gate.Release(); }
    }

    public async Task<DomainSetupJob> AcceptReview(StartDomainSetupRequest request, string actor, string currentTerms, CancellationToken ct = default)
    {
        if (!request.AcceptTerms || !request.AcceptDnsChanges || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Owner consent is required before DNS changes.");
        var document = await Update(current =>
        {
            if (current.Job is { } accepted && accepted.Plan.Id == request.PlanId && accepted.Plan.ReviewHash == request.ReviewHash)
                return current;
            if (current.Job?.State is "Queued" or "Running" or "Activating" or "Active")
                throw new InvalidOperationException("An existing DNS setup cannot be replaced.");
            if (current.Job?.RecoveryRequired == true) throw new InvalidOperationException("The previous DNS task needs recovery.");
            if (DomainActivationConfiguration.Read(Root) is not null) throw new InvalidOperationException("A domain profile is already active.");
            var plan = current.Plan ?? throw new InvalidOperationException("Prepare and review the DNS setup first.");
            if (plan.Id != request.PlanId || plan.ExpiresAt <= DateTimeOffset.UtcNow || request.ReviewHash.Length != 64
                || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(plan.ReviewHash), System.Text.Encoding.ASCII.GetBytes(request.ReviewHash)))
                throw new ArgumentException("The DNS review changed or expired. Prepare a fresh review.");
            if (plan.TermsUrl != currentTerms) throw new ArgumentException("The subscriber agreement changed. Review it again.");
            if (plan.Blockers.Length > 0) throw new InvalidOperationException("Resolve the review blockers before starting.");
            var now = DateTimeOffset.UtcNow;
            return current with { Plan = null, Job = new(Guid.NewGuid(), "Queued", "Queued", "Waiting to begin the approved DNS setup.",
                actor, now, now, plan, [new(now, "Queued", "Owner approved the listed DNS changes and subscriber agreement.")], []) };
        }, ct);
        return document.Job!;
    }

    private async Task<DomainSetupDocument> ReadFile(CancellationToken ct)
    {
        RejectLinks(_path);
        if (_lease.Length != 0)
        {
            if (_lease.Length != "Lucia.Domains.v1\n"u8.Length) throw new InvalidDataException("The domain workflow ownership marker is invalid.");
            var marker = new byte["Lucia.Domains.v1\n"u8.Length];
            _lease.Position = 0;
            _lease.ReadExactly(marker);
            if (!marker.AsSpan().SequenceEqual("Lucia.Domains.v1\n"u8)) throw new InvalidDataException("The domain workflow ownership marker is invalid.");
        }
        if (!File.Exists(_path))
        {
            if (_lease.Length != 0) throw new InvalidDataException("Domain workflow state is missing; restore it rather than resetting an in-flight change.");
            var empty = new DomainSetupDocument(1, null, null);
            _lease.Write("Lucia.Domains.v1\n"u8);
            _lease.Flush(flushToDisk: true);
            await WriteJson(_path, empty, ct);
            return empty;
        }
        if (_lease.Length == 0) throw new InvalidDataException("Existing domain workflow data has no ownership marker.");
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("Domain workflow state exceeds its size limit.");
        var result = JsonSerializer.Deserialize<DomainSetupDocument>(await File.ReadAllBytesAsync(_path, ct), Json)
            ?? throw new InvalidDataException("Domain workflow state is empty.");
        if (result.Version != 1 || result.Job?.Events.Length > 100
            || (result.Job is { } job && (job.Id == Guid.Empty
                || job.State is not ("Queued" or "Running" or "Failed" or "Activating" or "Active"))))
            throw new InvalidDataException("Domain workflow state has an unsupported schema.");
        foreach (var plan in new[] { result.Plan, result.Job?.Plan }.OfType<DomainSetupPlan>())
            if (plan.Id == Guid.Empty || plan.ReviewHash != Hash(plan) || plan.ExpiresAt <= plan.CreatedAt)
                throw new InvalidDataException("The stored DNS review has changed or is invalid.");
        return result;
    }

    public static string Hash(DomainSetupPlan plan) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan with { ReviewHash = "" }, Json)));

    internal static async Task WriteJson<T>(string path, T value, CancellationToken ct = default)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await JsonSerializer.SerializeAsync(stream, value, Json, ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void EnsureDirectory(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new IOException("Domain state must not be writable by other accounts.");
        }
    }

    internal static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("Domain state paths must not contain symbolic links or junctions.");
    }

    public void Dispose() { _lease.Dispose(); _gate.Dispose(); }
}
