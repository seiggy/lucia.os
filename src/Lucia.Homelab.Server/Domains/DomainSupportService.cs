using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Domains;

public sealed class DomainSupportService(DomainOnboardingStore store, ILogger<DomainSupportService> logger)
{
    private readonly SemaphoreSlim _analysis = new(1, 1);

    public async Task Queue(Guid id, CancellationToken ct)
    {
        await store.Update(current =>
        {
            if (current.Job is not { State: "Failed" } job || job.Id != id)
                throw new InvalidOperationException("Only the current failed DNS task can be explained.");
            if (job.Support?.State is "Queued" or "Running") return current;
            if (job.Support is { } previous && previous.UpdatedAt > DateTimeOffset.UtcNow.AddSeconds(-30))
                throw new InvalidOperationException("Wait thirty seconds before requesting another local explanation.");
            return current with { Job = job with { Support = new("Queued", null, null, null, DateTimeOffset.UtcNow) } };
        }, ct);
    }

    internal async Task RecoverInterrupted(CancellationToken ct)
    {
        var current = await store.Read(ct);
        if (current.Job is not { Support.State: "Running" } job) return;
        await Save(job.Id, job.Support, new("Unavailable", job.Support.Model, null,
            "The host restarted during the local explanation. The confirmed diagnosis remains available; request the explanation again.", DateTimeOffset.UtcNow), ct);
    }

    internal async Task AnalyzePending(Func<DomainDiagnosis, CancellationToken, Task<(string Model, string Explanation)>> explain,
        CancellationToken ct)
    {
        if (!await _analysis.WaitAsync(0, ct)) return;
        try
        {
            var job = (await store.Read(ct)).Job;
            if (job is not { State: "Failed" } || job.Support?.State is "Running" or "Complete" or "Unavailable") return;
            var diagnosis = job.Diagnosis is { } saved ? DomainSupportSkill.Diagnose(saved.Code, job.RecoveryRequired, job.Failure) : null;
            if (diagnosis is null)
            {
                string? code = null;
                try
                {
                    if (job.Events.Any(e => e.Phase is "Staging" or "Issuing"))
                        code = CertbotFailureDiagnostics.Read(Path.Combine(store.Root, "certbot", "logs"),
                            "lucia-" + job.Id.ToString("N"), job.CreatedAt, job.UpdatedAt);
                }
                catch (Exception error) when (error is CertbotException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("DNS support could not safely inspect diagnostic evidence ({ErrorType}).", error.GetType().Name);
                }
                var failure = job.Failure ?? new DomainFailure(code ?? "setup_failure",
                    job.Events.LastOrDefault(e => e.Phase is not ("Recovering" or "Recovery"))?.Phase ?? job.Phase,
                    null, null, "not retained by the previous host version");
                diagnosis = DomainSupportSkill.Diagnose(failure.Code, job.RecoveryRequired, failure);
            }
            var running = new DomainSupportReport("Running", null, null, null, DateTimeOffset.UtcNow);
            var claimed = false;
            await store.Update(current =>
            {
                if (current.Job is not { State: "Failed" } latest || latest.Id != job.Id
                    || latest.Support != job.Support) return current;
                claimed = true;
                return current with { Job = latest with { Diagnosis = diagnosis, Support = running } };
            }, ct);
            if (!claimed) return;
            DomainSupportReport result;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            try
            {
                var response = await explain(diagnosis, deadline.Token);
                if (string.IsNullOrWhiteSpace(response.Explanation) || response.Explanation.Length > 4096
                    || string.IsNullOrWhiteSpace(response.Model) || response.Model.Length > 1024)
                    throw new InvalidDataException("Invalid local explanation.");
                result = new("Complete", response.Model, response.Explanation.Trim(), null, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (DomainSupportUnavailableException error)
            {
                logger.LogWarning("Local DNS support unavailable ({Reason}).", error.Reason);
                result = new("Unavailable", null, null, error.Message, DateTimeOffset.UtcNow);
            }
            catch (Exception error)
            {
                logger.LogWarning("Local DNS explanation failed ({ErrorType}).", error.GetType().Name);
                result = new("Unavailable", null, null,
                    error is OperationCanceledException ? "The local explanation timed out. The confirmed diagnosis is still available; try again when the model is free."
                    : "The local model could not produce a complete explanation. The confirmed diagnosis is still available; check the loaded model and try again.",
                    DateTimeOffset.UtcNow);
            }
            await Save(job.Id, running, result, ct);
        }
        finally { _analysis.Release(); }
    }

    private Task<DomainSetupDocument> Save(Guid id, DomainSupportReport expected, DomainSupportReport value, CancellationToken ct) =>
        store.Update(current => current.Job is { State: "Failed" } job && job.Id == id && job.Support == expected
            ? current with { Job = job with { Support = value } } : current, ct);
}

internal sealed class DomainSupportUnavailableException(string reason, string message) : Exception(message)
{
    internal string Reason { get; } = reason;
}

public sealed class DomainSupportWorker(DomainSupportService support, IChatClient chat, InferenceRuntime runtime,
    IOptions<HostPlatformOptions> options, ILoggerFactory loggerFactory, ILogger<DomainSupportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await support.RecoverInterrupted(stoppingToken);
        var agent = chat.AsAIAgent(name: "lucia-dns-support", instructions: DomainSupportSkill.Instructions, loggerFactory: loggerFactory);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await support.AnalyzePending((diagnosis, ct) => Explain(agent, runtime.ChatModelName,
                    options.Value.MaxOutputTokens, diagnosis, ct), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                logger.LogError("DNS support could not persist its report ({ErrorType}).", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static async Task<(string Model, string Explanation)> Explain(AIAgent agent, string? model,
        int maxTokens, DomainDiagnosis diagnosis, CancellationToken ct)
    {
        if (model is null)
            throw new DomainSupportUnavailableException("no_model",
                "No chat model is loaded. Load one under Local AI → Models, then request the explanation again. The confirmed diagnosis does not require a model.");
        var response = await agent.RunAsync("Explain this confirmed diagnosis without adding unobserved facts:\n"
            + JsonSerializer.Serialize(diagnosis),
            options: new ChatClientAgentRunOptions(new ChatOptions
            {
                ModelId = model, MaxOutputTokens = Math.Min(1536, maxTokens),
                Temperature = 0.1f, ToolMode = ChatToolMode.None
            }), cancellationToken: ct);
        if (response.FinishReason == ChatFinishReason.Length)
            throw new InvalidDataException("The local explanation was truncated.");
        return (model, response.Text);
    }
}
