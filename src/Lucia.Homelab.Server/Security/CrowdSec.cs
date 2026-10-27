using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;

namespace Lucia.Homelab.Server.Security;

public sealed record CrowdSecEnrollRequest(string? Key);
/// <param name="Id">One ban's decision id, from the list of bans, instead of an address.</param>
public sealed record CrowdSecUnbanRequest(string? Ip, long? Id = null);

public sealed record CrowdSecConsoleView(string State, string? Message, DateTimeOffset? At, string? Plan, string Url);
public sealed record CrowdSecCapiView(bool? Registered, bool? Authenticated, bool? Sharing, bool? Blocklist);
public sealed record CrowdSecMetricsView(long LinesRead, long LinesParsed, long LinesUnparsed, long AppSecProcessed, long AppSecBlocked);
public sealed record CrowdSecBouncerView(string Name, DateTimeOffset? LastPull, bool Revoked);
/// <param name="State">
/// <c>absent</c>, <c>stopped</c>, <c>moving</c>, <c>waiting-domain</c> (no active domain, so the engine has no name),
/// <c>waiting-certificate</c> (Lucia's authority hasn't signed the engine's certificate yet), <c>unreachable</c> (the local API
/// doesn't answer over TLS at its name), <c>needs-observability</c> (protecting, but Observability is missing or too old to forward
/// the gateway's access log), <c>no-logs</c> (protecting, but no access-log line arrived for <see cref="CrowdSec.QuietAfter"/>)
/// or <c>healthy</c>.
/// </param>
/// <param name="Name">The engine's name under the domain, which its certificate carries and the gateway connects to.</param>
/// <param name="Protecting">Whether the gateway's middlewares point at the engine now, rather than being empty chains.</param>
/// <param name="ReportedAt">When the engine last logged its status for this page.</param>
public sealed record CrowdSecStatusView(string State, string? Message, bool Protecting, bool ObservabilityInstalled, string? Stack, string? Node,
    string? Address, string? Name, string? Version, string? Image, DateTimeOffset? CertificateExpiresAt, CrowdSecCapiView? Capi, CrowdSecConsoleView Console,
    CrowdSecBouncerView? Bouncer, CrowdSecMetricsView? Metrics, int? HarborEntries, DateTimeOffset? ReportedAt, int? CustomRules = null, CrowdSecAlertsView? Alerts = null, string? CustomRulesProblem = null);
/// <param name="State">
/// Where the engine's alerts go: <c>on</c> (Observability's TLS alert intake takes them), <c>off</c> (no Observability),
/// <c>update-observability</c>, <c>waiting-domain</c>, <c>waiting-certificate</c>, <c>stopped</c> or <c>unreachable</c>. Anything but
/// <c>on</c> keeps them in CrowdSec only; the engine and the gateway carry on.
/// </param>
/// <param name="Intake">The intake's name under the domain, which its certificate carries.</param>
public sealed record CrowdSecAlertsView(string State, string? Message, string? Intake);
/// <param name="Kind">waf for a request AppSec blocked (Scenario is then the rule), crowdsec for a scenario, or another kind CrowdSec reports.</param>
/// <param name="Path">The blocked request's path, for AppSec blocks.</param>
public sealed record CrowdSecAlertView(long Id, string Scenario, string? Scope, string? Value, string? Country, string? AsName, DateTimeOffset? At,
    int Events, int Decisions, bool Simulated, string Kind, string? Path);
public sealed record CrowdSecDecisionView(long Id, string Value, string Scope, string Type, string Origin, string? Scenario, DateTimeOffset? Until);

/// <summary>The engine's own report, a <c>lucia-status</c> line its start script logs every minute.</summary>
internal sealed record CrowdSecReport(DateTimeOffset At, string? Version, CrowdSecBouncerView? Bouncer, CrowdSecMetricsView? Metrics,
    CrowdSecCapiView? Capi, bool? Enrolled, string? Plan, string EnrollState, string? EnrollMessage, DateTimeOffset? EnrollAt, int? Harbor, int? Rules = null, string? RulesProblem = null);

/// <summary>
/// Keeps the gateway in step with the CrowdSec app, and serves the CrowdSec page. Every 15 seconds it gets (or renews) the
/// engine's certificate from Lucia's authority, hands the engine that certificate, the safe harbor and any Console enroll key,
/// gives Observability's collector the address to forward the gateway's access log to, and checks the engine's local API over
/// TLS. While that answers, the gateway's <c>crowdsec</c> and <c>crowdsec-appsec</c> middlewares point at the engine; when
/// it's absent, stopped, moving or hasn't answered twice in a row, they're empty chains, so no request waits on it. The page
/// reads alerts and decisions and lifts bans through the local API, signed in as the app's <c>lucia-host</c> machine; the
/// engine's own status comes from its <c>lucia-status</c> log line, through the server's agent.
/// </summary>
public sealed partial class CrowdSec(StackStore stacks, DomainOnboardingStore domains, SafeHarbor harbor, CrowdSecRules rules, GatewaySecurityOptions options,
    HostAuthenticationOptions authentication, AppCertificates certificates, ManagedNodeEnrollment nodes, NodeRequests requests, TimeProvider time,
    ILogger<CrowdSec> logger) : BackgroundService
{
    internal const string ConsoleUrl = "https://app.crowdsec.net";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15), ReportLifetime = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _wake = new(0, 1), _sync = new(1, 1);
    private X509Certificate2? _root;
    private HttpClient? _client;
    private CrowdSecLink? _link;
    private int _failures;
    private string? _probeProblem;
    private string? _certificateProblem;
    private string? _stack;
    private string? _name;
    private CrowdSecLapi? _lapi;
    private (DateTimeOffset At, string Container, CrowdSecReport? Report, string? Problem) _report;
    private (long Lines, DateTimeOffset Since)? _logs;
    private string? _intakeStack;
    private TelemetryDestination? _intakeDestination;
    private (string State, string? Message, string? Intake) _alerts;
    /// <summary>How long the engine may read no access-log line before the page says the log path is broken.</summary>
    internal static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(15);

    /// <summary>The engine's access-log line count and when it last changed. A restart resets the count, which counts as a change.</summary>
    internal static (long Lines, DateTimeOffset Since) Track((long Lines, DateTimeOffset Since)? seen, long lines, DateTimeOffset now) =>
        seen is { } last && last.Lines == lines ? last : (lines, now);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        harbor.Changed += Wake;
        rules.Changed += Wake;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Sync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogWarning("The gateway's CrowdSec configuration could not be updated ({ErrorType}); retrying.", error.GetType().Name);
                }
                try { await _wake.WaitAsync(Interval, stop); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally { harbor.Changed -= Wake; rules.Changed -= Wake; }
    }

    internal void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    internal async Task Sync(CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var entries = await harbor.Entries(ct);
            var install = await stacks.CrowdSec(ct);
            CrowdSecLink? link = null;
            if (install is null)
            {
                _certificateProblem = _probeProblem = null;
                _failures = 0;
                if (_stack is { } gone) await certificates.Forget(gone, ct);
                if (_intakeStack is { } intakeGone) await certificates.Forget(intakeGone, ct);
                _stack = _name = _intakeStack = null;
                _intakeDestination = null;
                _alerts = default;
                await stacks.SetCrowdSecCollector("", ct);
            }
            else
            {
                _stack = install.Name;
                var naming = await ManagedNodeDns.ActiveNaming(domains, ct);
                var name = _name = naming is null ? null : CrowdSecApp.FullName(install.Settings, naming.Namespace);
                var root = Root();
                var current = Certificate(install.Env);
                var certificate = name is not null && current is not null && AppCertificates.Valid(current, name, root, time.GetUtcNow()) ? current : null;
                _certificateProblem = null;
                if (install is { Settled: true, Address: { } address, NodeId: { } nodeId } && name is not null)
                    (certificate, _certificateProblem) = await certificates.Ensure(install.Name, nodeId, address, name, current, root, nodes.WorkerReady, ct);
                var (intake, intakeCertificate) = await Intake(naming, root, ct);
                _intakeDestination = intake;
                await stacks.SetCrowdSec(install.Name, Lines(certificate, root.ExportCertificatePem(), entries,
                    install.Env.GetValueOrDefault(StackStore.CrowdSecPrefix + "ENROLL_KEY"), install.Env.GetValueOrDefault(StackStore.CrowdSecPrefix + "ENROLL_ID"), name,
                    await rules.Rendered(ct), intake), ct);
                var running = install is { Settled: true, Desired: "Running", Address: not null } && certificate is not null && name is not null;
                var ports = CrowdSecApp.Ports(install.Settings);
                await stacks.SetCrowdSecCollector(CrowdSecApp.CollectorLines(running && install.Env.GetValueOrDefault(CrowdSecApp.IntakeToken) is { Length: >= 32 } token
                    ? ($"https://{name}:{ports.Logs}{CrowdSecApp.IntakePath}", root.ExportCertificatePem(), token) : null, intakeCertificate)
                    + CrowdSecApp.ConsoleLine(naming is null ? null : DomainActivationConfiguration.Read(domains.Root)?.CanonicalLuciaOrigin), ct);
                if (running && install.Env.GetValueOrDefault(CrowdSecApp.GatewayKey) is { Length: >= 32 } key)
                {
                    var candidate = new CrowdSecLink(name!, ports.Lapi, ports.AppSec, key, root.ExportCertificatePem() + "\n");
                    if (await Probe(candidate, ct)) { _failures = 0; _probeProblem = null; link = candidate; }
                    else if (++_failures < 2 && _link == candidate) link = _link;
                }
                else _failures = 0;
            }
            if (link is null && _link is not null) logger.LogInformation("The gateway's CrowdSec middlewares now let everything through: the engine isn't available.");
            if (link is not null && _link is null) logger.LogInformation("The gateway's CrowdSec middlewares now use the engine at {Name}.", link.Host);
            _link = link;
            Publish(entries, link);
        }
        finally { _sync.Release(); }
    }

    private void Publish(string[] entries, CrowdSecLink? link)
    {
        if (!harbor.Enabled) return;
        DomainActivationConfiguration.RejectLinks(options.GatewayDirectory);
        GatewaySecurity.Publish(options.GatewayDirectory, entries, DomainIngressConfiguration.CloudflareRanges, link);
    }

    /// <summary>
    /// Observability's TLS alert intake for the engine's notifications: its certificate from Lucia's CA (asked for and renewed
    /// like the engine's own), and its address once it answers over TLS that verifies against Lucia's root. Until then the
    /// engine gets no collector, so its alerts stay in CrowdSec and nothing else changes; <see cref="_alerts"/> says why.
    /// </summary>
    private async Task<(TelemetryDestination? Destination, AppCertificate? Certificate)> Intake(DomainNamingPlan? naming, X509Certificate2 root, CancellationToken ct)
    {
        var observability = await stacks.Observability(ct);
        if (_intakeStack is { } old && old != observability?.Name) await certificates.Forget(old, ct);
        _intakeStack = observability?.Name;
        if (observability is null)
        {
            _alerts = ("off", "Observability isn't installed, so CrowdSec keeps its alerts to itself.", null);
            return (null, null);
        }
        if (observability.Version < ObservabilityApp.ShowsCrowdSec)
        {
            _alerts = ("update-observability", $"Observability runs catalog version {observability.Version}, which has no TLS alert intake. Update it under Apps (version {ObservabilityApp.ShowsCrowdSec} or later) to see CrowdSec's alerts in Grafana.", null);
            return (null, null);
        }
        if (naming is null)
        {
            _alerts = ("waiting-domain", "The alert intake needs your lab's domain for its name and certificate.", null);
            return (null, null);
        }
        var name = ObservabilityApp.AlertsName(observability.Settings, naming.Namespace);
        var current = CrowdSecApp.IntakeCertificate(observability.Env);
        var certificate = current is not null && AppCertificates.Valid(current, name, root, time.GetUtcNow()) ? current : null;
        string? problem = null;
        if (observability is { Settled: true, Address: { } address, NodeId: { } nodeId })
            (certificate, problem) = await certificates.Ensure(observability.Name, nodeId, address, name, current, root, nodes.WorkerReady, ct);
        if (certificate is null)
        {
            _alerts = ("waiting-certificate", problem ?? "Lucia is getting the alert intake's certificate from its authority.", name);
            return (null, null);
        }
        if (observability is not { Desired: "Running", Settled: true, Authorization: { } authorization })
        {
            _alerts = ("stopped", "Observability is stopped or moving, so CrowdSec keeps its alerts to itself until it's back.", name);
            return (null, certificate);
        }
        var endpoint = new Uri($"https://{name}:{int.Parse(observability.Settings["alerts-port"], CultureInfo.InvariantCulture)}");
        if (await ProbeIntake(endpoint, ct) is { } failure)
        {
            _alerts = ("unreachable", failure, name);
            return (null, certificate);
        }
        _alerts = ("on", null, name);
        return (new TelemetryDestination(endpoint, authorization), certificate);
    }

    /// <summary>Null when the intake answers over TLS that verifies against Lucia's root for its name (any HTTP status), else why not.</summary>
    private async Task<string?> ProbeIntake(Uri endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await Client().GetAsync(new Uri(endpoint, "/v1/logs"), timeout.Token);
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return error switch
            {
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                    $"{endpoint.Host} doesn't resolve here yet. Lucia publishes it in AdGuard once Observability runs under the domain.",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                    $"The alert intake at {endpoint.Host} didn't present a certificate from Lucia's authority for its name yet.",
                _ => $"The alert intake at {endpoint.Authority} isn't answering yet. Observability's collector picks it up when Lucia redeploys it.",
            };
        }
    }

    /// <summary>
    /// The engine's Lucia variables: its name and certificate with Lucia's root (base64, so each fits one line), the harbor,
    /// the Console enroll key, the owner's custom AppSec rules (base64 YAML) and Observability's collector on the LAN, which
    /// receives every alert. The name only comes with a certificate for it, so the engine never waits under a name it can't serve.
    /// </summary>
    internal static string Lines(AppCertificate? certificate, string caPem, IEnumerable<string> entries, string? enrollKey, string? enrollId, string? name,
        string appSecRules = "", TelemetryDestination? collector = null)
    {
        var prefix = StackStore.CrowdSecPrefix;
        var text = new StringBuilder();
        if (certificate is not null && name is not null)
            text.Append($"{CrowdSecApp.NameVariable}={name}\n").Append($"{prefix}TLS_CERT={Convert.ToBase64String(Encoding.ASCII.GetBytes(certificate.ChainPem))}\n")
                .Append($"{prefix}TLS_KEY={Convert.ToBase64String(Encoding.ASCII.GetBytes(certificate.KeyPem))}\n")
                .Append($"{prefix}CA={Convert.ToBase64String(Encoding.ASCII.GetBytes(caPem.ReplaceLineEndings("\n")))}\n");
        text.Append($"{prefix}HARBOR={string.Join(',', entries)}\n");
        if (enrollKey is { Length: > 0 } && ValidEnrollKey(enrollKey) && enrollId is { Length: > 0 } && Hex().IsMatch(enrollId))
            text.Append($"{prefix}ENROLL_KEY={enrollKey}\n{prefix}ENROLL_ID={enrollId}\n");
        text.Append($"{prefix}APPSEC_RULES={Convert.ToBase64String(Encoding.UTF8.GetBytes(appSecRules.ReplaceLineEndings("\n")))}\n");
        if (collector is not null && StackCatalog.Unquoted(collector.Authorization.Replace(' ', '_')))
            text.Append($"{prefix}OTLP_ENDPOINT={collector.Endpoint.AbsoluteUri.TrimEnd('/')}\n{prefix}OTLP_AUTHORIZATION={collector.Authorization}\n");
        return text.ToString();
    }

    internal static AppCertificate? Certificate(IReadOnlyDictionary<string, string> env)
    {
        try
        {
            if (env.GetValueOrDefault(StackStore.CrowdSecPrefix + "TLS_CERT") is not { Length: > 0 } chain
                || env.GetValueOrDefault(StackStore.CrowdSecPrefix + "TLS_KEY") is not { Length: > 0 } key) return null;
            var (chainPem, keyPem) = (Encoding.ASCII.GetString(Convert.FromBase64String(chain)), Encoding.ASCII.GetString(Convert.FromBase64String(key)));
            using var leaf = X509Certificate2.CreateFromPem(chainPem);
            return new(chainPem, keyPem, leaf.NotAfter.ToUniversalTime());
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException) { return null; }
    }

    private X509Certificate2 Root() => _root ??= authentication.ReadCertificateAuthority();

    private HttpClient Client() => _client ??= new HttpClient(HostAuthenticationOptions.CreateBackchannelHandler(Root()))
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private async Task<bool> Probe(CrowdSecLink link, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await Client().GetAsync($"https://{link.Authority(link.LapiPort)}/health", timeout.Token);
            _probeProblem = response.IsSuccessStatusCode ? null : $"CrowdSec's local API answered {(int)response.StatusCode}.";
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            _probeProblem = error switch
            {
                HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
                    $"{link.Host} doesn't resolve here. Lucia publishes it in AdGuard once the domain is active; check that the Spark uses AdGuard for DNS.",
                HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
                    $"CrowdSec's local API didn't present a certificate from Lucia's authority for {link.Host}.",
                _ => "CrowdSec's local API didn't answer.",
            };
            return false;
        }
    }

    public async Task<CrowdSecStatusView> Status(CancellationToken ct)
    {
        var observability = await stacks.Installed(ObservabilityApp.AppId, ct);
        var install = await stacks.CrowdSec(ct);
        if (install is null)
            return new("absent", null, false, observability, null, null, null, null, null, null, null, null, new("not-enrolled", null, null, null, ConsoleUrl),
                null, null, null, null);
        var naming = await ManagedNodeDns.ActiveNaming(domains, ct);
        var name = naming is null ? null : CrowdSecApp.FullName(install.Settings, naming.Namespace);
        var certificate = Certificate(install.Env);
        var report = install.NodeId is { } node && install.Engine is { } engine && install is { Settled: true, Desired: "Running" }
            ? await Report(node, engine.Name, ct) : (Report: (CrowdSecReport?)null, Problem: (string?)null);
        var (state, message) = install switch
        {
            { Settled: false } => ("moving", "CrowdSec is moving or being restored. The gateway lets everything through until it's back."),
            { Desired: not "Running" } => ("stopped", "CrowdSec is stopped. The gateway lets everything through until you start it."),
            _ when name is null => ("waiting-domain", "CrowdSec needs your lab's domain: the gateway reaches it by name. The gateway lets everything through until a domain is active."),
            _ when certificate is null => ("waiting-certificate", _certificateProblem ?? "Lucia is getting this engine's certificate from its authority."),
            _ when _link is null => ("unreachable", _probeProblem ?? "Lucia is checking CrowdSec's local API."),
            _ => ("healthy", _certificateProblem),
        };
        if (state == "healthy")
        {
            var observabilityVersion = await stacks.TemplateVersion(ObservabilityApp.AppId, ct);
            if (report.Report?.Metrics is { } metrics) _logs = Track(_logs, metrics.LinesRead, time.GetUtcNow());
            if (observabilityVersion is not { } version || version < ObservabilityApp.ForwardsToCrowdSec)
                (state, message) = ("needs-observability", observabilityVersion is null
                    ? "Observability isn't installed, so CrowdSec gets none of the gateway's access log. Only AppSec and the community blocklist protect the gateway until you install it."
                    : $"Observability runs catalog version {observabilityVersion}, which doesn't forward the gateway's access log to CrowdSec. Update Observability under Apps (version {ObservabilityApp.ForwardsToCrowdSec} or later); until then only AppSec and the community blocklist protect the gateway.");
            else if (_logs is { } seen && time.GetUtcNow() - seen.Since >= QuietAfter)
                (state, message) = ("no-logs", $"CrowdSec hasn't received any of the gateway's access log for {(int)(time.GetUtcNow() - seen.Since).TotalMinutes} minutes. Check that Observability is running and its collector can reach {name}; bans from the access log wait until it does.");
        }
        var key = install.Env.GetValueOrDefault(StackStore.CrowdSecPrefix + "ENROLL_KEY");
        var view = report.Report;
        var console = view switch
        {
            { Enrolled: true } => new CrowdSecConsoleView("accepted", null, view.EnrollAt, view.Plan, ConsoleUrl),
            _ when key is null => new CrowdSecConsoleView("not-enrolled", null, null, null, ConsoleUrl),
            { EnrollState: "sent" } => new CrowdSecConsoleView("pending", null, view.EnrollAt, view.Plan, ConsoleUrl),
            { EnrollState: "failed" } => new CrowdSecConsoleView("failed", view.EnrollMessage, view.EnrollAt, null, ConsoleUrl),
            _ => new CrowdSecConsoleView("sending", null, null, null, ConsoleUrl),
        };
        return new(state, message ?? report.Problem, state is "healthy" or "needs-observability" or "no-logs" && _link is not null, observability, install.Name, install.Node, install.Address, name,
            view?.Version, install.Image, certificate?.NotAfter, view?.Capi, console, view?.Bouncer, view?.Metrics, view?.Harbor, view?.At, view?.Rules,
            _alerts.State is { } alertState ? new CrowdSecAlertsView(alertState, _alerts.Message, _alerts.Intake) : null, view?.RulesProblem);
    }

    /// <summary>The engine's latest <c>lucia-status</c> line, read through the server's agent and kept for half a minute.</summary>
    private async Task<(CrowdSecReport? Report, string? Problem)> Report(Guid node, string container, CancellationToken ct)
    {
        var cached = _report;
        if (cached.Container == container && cached.At > time.GetUtcNow() - ReportLifetime) return (cached.Report, cached.Problem);
        CrowdSecReport? report = null;
        string? problem = null;
        try
        {
            var result = await requests.Logs(node, container, 400, ct);
            report = result.Success ? ParseReport(result.Output ?? "") : null;
            if (report is null) problem = result.Success ? "The engine hasn't reported its status yet." : result.Message;
        }
        catch (HardwareOnboardingException error) { problem = error.Message; }
        _report = (time.GetUtcNow(), container, report, problem);
        return (report, problem);
    }

    internal static CrowdSecReport? ParseReport(string logs)
    {
        const string Marker = "lucia-status ";
        foreach (var line in logs.Split('\n').Reverse())
        {
            var start = line.IndexOf(Marker, StringComparison.Ordinal);
            if (start < 0 || line.Length - start > 8192) continue;
            try
            {
                using var document = JsonDocument.Parse(line[(start + Marker.Length)..].Trim());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                    || Time(root, "at") is not { } at) continue;
                var bouncer = root.TryGetProperty("bouncers", out var bouncers) && bouncers.ValueKind == JsonValueKind.Array
                    && bouncers.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first && Text(first, "name") is { } name
                    ? new CrowdSecBouncerView(name, Time(first, "lastPull"), Bool(first, "revoked") ?? false) : null;
                var metrics = root.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object
                    ? new CrowdSecMetricsView(Number(m, "linesRead"), Number(m, "linesParsed"), Number(m, "linesUnparsed"), Number(m, "appsecProcessed"),
                        Number(m, "appsecBlocked")) : null;
                var capi = root.TryGetProperty("capi", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
                var console = root.TryGetProperty("console", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
                var enroll = root.TryGetProperty("enroll", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                var state = Text(enroll, "state") is "none" or "sent" or "failed" ? Text(enroll, "state")! : "none";
                return new(at, Text(root, "version") is { Length: > 0 and <= 64 } version ? version : null, bouncer, metrics,
                    capi.ValueKind == JsonValueKind.Object ? new(Bool(capi, "registered"), Bool(console, "authenticated"), Bool(capi, "sharing"), Bool(capi, "blocklist")) : null,
                    Bool(console, "enrolled"), Text(console, "plan") is { Length: > 0 and <= 64 } plan ? plan : null, state,
                    Text(enroll, "message") is { Length: > 0 } text ? new string([.. text.Where(ch => !char.IsControl(ch)).Take(240)]) : null,
                    Time(enroll, "at"), root.TryGetProperty("harbor", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetInt32(out var count) ? count : null,
                    root.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var ruleCount) && ruleCount >= 0 ? ruleCount : null,
                                        Text(root, "rulesProblem") is { Length: > 0 } rulesProblem ? new string([.. rulesProblem.Where(ch => !char.IsControl(ch)).Take(400)]) : null);
                                }
            catch (JsonException) { }
        }
        return null;

        static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static bool? Bool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
        static long Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0 ? number : 0;
        static DateTimeOffset? Time(JsonElement element, string name) =>
            Text(element, name) is { Length: > 0 and <= 64 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at : null;
    }

    /// <summary>Bans in force, from alerts with an active decision. The community blocklist's own decisions stay out.</summary>
    public async Task<CrowdSecDecisionView[]> Decisions(CancellationToken ct)
    {
        var (origin, password) = await LapiTarget(ct);
        using var alerts = await Call(() => Lapi().Send(origin, password, HttpMethod.Get, "/v1/alerts?include_capi=false&has_active_decision=true&limit=500", ct), ct);
        return CrowdSecLapi.ParseDecisions(alerts.RootElement, time.GetUtcNow());
    }

    /// <summary>
    /// Lifts every ban on an address or network, or one ban by its decision id. The gateway drops it at its next pull, within
    /// 15 seconds.
    /// </summary>
    public async Task<object> Unban(CrowdSecUnbanRequest request, CancellationToken ct, string actor = "The owner")
    {
        string path;
        if (request.Id is { } id)
        {
            if (id <= 0 || !string.IsNullOrWhiteSpace(request.Ip))
                throw new HardwareOnboardingException(400, "invalid_decision", "Name either the address or the ban's id from the list of bans, not both.");
            path = "/v1/decisions/" + id.ToString(CultureInfo.InvariantCulture);
        }
        else
            path = "/v1/decisions?" + (CrowdSecLapi.UnbanQuery(request.Ip)
                ?? throw new HardwareOnboardingException(400, "invalid_address", "Name the address or network to unban, such as 203.0.113.7."));
        var (origin, password) = await LapiTarget(ct);
        using var result = await Call(() => Lapi().Send(origin, password, HttpMethod.Delete, path, ct), ct);
        var deleted = CrowdSecLapi.Deleted(result.RootElement);
        logger.LogInformation("{Actor} lifted {Count} CrowdSec decisions.", actor, deleted);
        return new { deleted };
    }

    /// <summary>Keeps the owner's enroll key as the app's secret; the engine enrolls once and the page shows the Console's answer.</summary>
    public async Task<CrowdSecStatusView> Enroll(CrowdSecEnrollRequest request, CancellationToken ct)
    {
        var key = (request.Key ?? "").Trim();
        if (!ValidEnrollKey(key))
            throw new HardwareOnboardingException(400, "invalid_enroll_key", "Paste the enroll key from app.crowdsec.net: letters and digits, without spaces.");
        await SetEnrollKey(key, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), ct);
        return await Status(ct);
    }

    public async Task<CrowdSecStatusView> ForgetEnrollKey(CancellationToken ct)
    {
        await SetEnrollKey(null, null, ct);
        return await Status(ct);
    }

    private async Task SetEnrollKey(string? key, string? id, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            var install = await stacks.CrowdSec(ct)
                ?? throw new HardwareOnboardingException(409, "crowdsec_not_installed", "Install CrowdSec from Apps first.");
            if (!install.Settled)
                throw new HardwareOnboardingException(409, "stack_moving", "CrowdSec is moving or being restored. Try again once it's back.");
            await stacks.SetCrowdSec(install.Name, Lines(Certificate(install.Env), Root().ExportCertificatePem(), await harbor.Entries(ct), key, id,
                install.Env.GetValueOrDefault(CrowdSecApp.NameVariable), await rules.Rendered(ct), _intakeDestination), ct);
            _report = default;
        }
        finally { _sync.Release(); }
        logger.LogInformation(key is null ? "The owner removed CrowdSec's Console enroll key." : "The owner gave CrowdSec a Console enroll key.");
    }

    /// <summary>The engine's local API while the gateway uses it, and Lucia's machine password.</summary>
    private async Task<(string Origin, string Password)> LapiTarget(CancellationToken ct)
    {
        var install = await stacks.CrowdSec(ct)
            ?? throw new HardwareOnboardingException(409, "crowdsec_not_installed", "Install CrowdSec from Apps first.");
        if (_link is not { } link || _stack != install.Name || link.Host != _name)
            throw new HardwareOnboardingException(503, "crowdsec_unavailable", "CrowdSec isn't answering right now. Its status says why.");
        return ($"https://{link.Authority(link.LapiPort)}", install.Env.GetValueOrDefault(CrowdSecApp.MachinePassword) ?? "");
    }

    private CrowdSecLapi Lapi() => _lapi ??= new CrowdSecLapi(Client(), time);

    private static async Task<JsonDocument> Call(Func<Task<JsonDocument>> call, CancellationToken ct)
    {
        try { return await call(); }
        catch (CrowdSecLapiException error) { throw new HardwareOnboardingException(error.Status, error.Code, error.Message); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            throw new HardwareOnboardingException(503, "crowdsec_unavailable", "CrowdSec's local API didn't answer.");
        }
    }

    internal static bool ValidEnrollKey(string key) => EnrollKey().IsMatch(key);

    internal static TimeSpan Duration(string text)
    {
        var total = TimeSpan.Zero;
        foreach (Match part in DurationPart().Matches(text))
            total += part.Groups[2].Value switch
            {
                "h" => TimeSpan.FromHours(double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture)),
                "m" => TimeSpan.FromMinutes(double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture)),
                "s" => TimeSpan.FromSeconds(double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture)),
                "ms" => TimeSpan.FromMilliseconds(double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture)),
                _ => TimeSpan.Zero,
            };
        return text.StartsWith('-') ? -total : total;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{8,128}\z")] private static partial Regex EnrollKey();
    [GeneratedRegex(@"\A[0-9a-f]{16}\z")] private static partial Regex Hex();
    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|h|m|s)")] private static partial Regex DurationPart();
}

internal sealed class CrowdSecLapiException(int status, string code, string message) : Exception(message)
{
    public int Status => status;
    public string Code => code;
}

/// <summary>
/// CrowdSec's local API as Lucia's host uses it: over TLS that trusts only Lucia's authority (the caller's handler), signed in
/// as the app's <c>lucia-host</c> machine. The token is kept until shortly before it expires.
/// </summary>
internal sealed class CrowdSecLapi(HttpClient client, TimeProvider time)
{
    private const string UserAgent = "lucia-host/1";
    private (string Key, string Token, DateTimeOffset Expires)? _token;

    public async Task<JsonDocument> Send(string origin, string password, HttpMethod method, string path, CancellationToken ct, string? json = null)
    {
        var key = origin + "\n" + password;
        if (_token is not { } cached || cached.Key != key || cached.Expires <= time.GetUtcNow())
        {
            using var body = new StringContent(JsonSerializer.Serialize(new { machine_id = CrowdSecApp.Machine, password, scenarios = Array.Empty<string>() }),
                Encoding.UTF8, "application/json");
            using var signIn = new HttpRequestMessage(HttpMethod.Post, origin + "/v1/watchers/login") { Content = body };
            // The local API refuses a machine that names no user agent.
            signIn.Headers.UserAgent.ParseAdd(UserAgent);
            using var login = await client.SendAsync(signIn, ct);
            if (!login.IsSuccessStatusCode)
                throw new CrowdSecLapiException(502, "crowdsec_login_failed", "CrowdSec refused Lucia's machine credential. Restart the CrowdSec app to register it again.");
            using var answer = await JsonDocument.ParseAsync(await login.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var expires = Time(answer.RootElement, "expire") ?? time.GetUtcNow().AddMinutes(30);
            _token = (key, Text(answer.RootElement, "token") ?? throw new CrowdSecLapiException(502, "crowdsec_login_failed", "CrowdSec's sign-in answer was empty."),
                expires.AddMinutes(-2));
        }
        using var request = new HttpRequestMessage(method, origin + path);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token.Value.Token);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) _token = null;
        if (!response.IsSuccessStatusCode)
            throw new CrowdSecLapiException(502, "crowdsec_error", $"CrowdSec's local API answered {(int)response.StatusCode}.");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > 8 * 1024 * 1024) throw new CrowdSecLapiException(502, "crowdsec_error", "CrowdSec's answer was too large.");
        return JsonDocument.Parse(bytes.Length == 0 || bytes.AsSpan().Trim((byte)' ').SequenceEqual("null"u8) ? "[]"u8.ToArray() : bytes);
    }

    internal static CrowdSecAlertView[] ParseAlerts(JsonElement alerts) => alerts.ValueKind != JsonValueKind.Array ? [] : [.. alerts.EnumerateArray()
        .Where(alert => alert.ValueKind == JsonValueKind.Object).Select(alert =>
        {
            var source = alert.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            return new CrowdSecAlertView(alert.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) ? number : 0,
                Text(alert, "scenario") ?? "", Text(source, "scope"), Text(source, "value") ?? Text(source, "ip"), Text(source, "cn") is { Length: > 0 } cn ? cn : null,
                Text(source, "as_name") is { Length: > 0 } asName ? asName : null, Time(alert, "created_at") ?? Time(alert, "start_at"),
                alert.TryGetProperty("events_count", out var events) && events.TryGetInt32(out var count) ? count : 0,
                alert.TryGetProperty("decisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array ? decisions.GetArrayLength() : 0,
                alert.TryGetProperty("simulated", out var simulated) && simulated.ValueKind == JsonValueKind.True,
                Text(alert, "kind") is { Length: > 0 and <= 32 } kind ? kind : "crowdsec", BlockedPath(alert));
        })];

    internal static bool IsAppSec(CrowdSecAlertView alert) => alert.Kind == "waf";

    /// <summary>
    /// The alert cscli posts for <c>cscli decisions add</c>, with Lucia's scenario and origin: one ban decision, the reason as
    /// the message, and who asked in its context. The profiles see it only to send it to Observability; they add no decision.
    /// </summary>
    internal static string BanAlert(string value, string scope, TimeSpan duration, string reason, string actor, string origin, DateTimeOffset now)
    {
        var at = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var message = $"{reason} (by {actor})";
        return JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object?>
            {
                ["scenario"] = CrowdSec.ManualScenario, ["scenario_hash"] = "", ["scenario_version"] = "", ["message"] = message,
                ["capacity"] = 0, ["leakspeed"] = "0", ["events_count"] = 1, ["simulated"] = false, ["remediation"] = true,
                ["start_at"] = at, ["stop_at"] = at, ["created_at"] = at, ["events"] = Array.Empty<object>(),
                ["meta"] = new[]
                {
                    new { key = "lucia_reason", value = JsonSerializer.Serialize(new[] { reason }) },
                    new { key = "lucia_actor", value = JsonSerializer.Serialize(new[] { actor }) },
                },
                ["source"] = new Dictionary<string, object?> { ["scope"] = scope, ["value"] = value, ["ip"] = value, ["range"] = "", ["cn"] = "", ["as_name"] = "", ["as_number"] = "" },
                ["decisions"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "ban", ["scope"] = scope, ["value"] = value, ["duration"] = CrowdSecGuard.Format(duration),
                        ["origin"] = origin, ["scenario"] = CrowdSec.ManualScenario, ["simulated"] = false,
                    },
                },
            },
        });
    }

    /// <summary>One alert from <c>GET /v1/alerts/{id}</c>, with its context lists and up to 20 events, values capped for the reader.</summary>
    internal static CrowdSecAlertDetail? ParseAlert(JsonElement alert, DateTimeOffset now)
    {
        if (alert.ValueKind != JsonValueKind.Object) return null;
        using var wrapped = JsonDocument.Parse("[" + alert.GetRawText() + "]");
        if (ParseAlerts(wrapped.RootElement) is not [var view]) return null;
        static string Cap(string? text, int limit = 512) => text is null ? "" : text.Length <= limit ? text : text[..limit] + "…";
        var context = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (alert.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Array)
            foreach (var item in meta.EnumerateArray().Take(32))
            {
                if (Text(item, "key") is not { Length: > 0 and <= 64 } key || Text(item, "value") is not { } value) continue;
                string[] values;
                try
                {
                    using var list = JsonDocument.Parse(value);
                    values = list.RootElement.ValueKind == JsonValueKind.Array
                        ? [.. list.RootElement.EnumerateArray().Take(20).Select(entry => Cap(entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetRawText()))]
                        : [Cap(value)];
                }
                catch (JsonException) { values = [Cap(value)]; }
                context[key] = values;
            }
        var events = alert.TryGetProperty("events", out var list2) && list2.ValueKind == JsonValueKind.Array
            ? list2.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Take(20).Select(item => new CrowdSecEventView(Time(item, "timestamp"),
                item.TryGetProperty("meta", out var eventMeta) && eventMeta.ValueKind == JsonValueKind.Array
                    ? eventMeta.EnumerateArray().Where(entry => Text(entry, "key") is { Length: > 0 and <= 64 }).Take(40)
                        .GroupBy(entry => Text(entry, "key")!).ToDictionary(group => group.Key, group => Cap(Text(group.First(), "value")))
                    : new Dictionary<string, string>())).ToArray()
            : [];
        var source = alert.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        return new(view, Text(alert, "message") is { } message ? Cap(message) : null, Text(alert, "machine_id"), Text(source, "as_number") is { Length: > 0 } asn ? asn : null,
            Text(source, "range") is { Length: > 0 } range ? range : null, context, events, ParseDecisions(wrapped.RootElement, now));
    }

    /// <summary>The path an AppSec block names: its event's uri, else the alert's target_uri (a JSON list as text). Capped for the page.</summary>
    internal static string? BlockedPath(JsonElement alert)
    {
        string? path = null;
        if (alert.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            path = events.EnumerateArray().Select(evt => Meta(evt, "uri")).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        if (string.IsNullOrEmpty(path) && Meta(alert, "target_uri") is { Length: > 0 } targets)
        {
            try
            {
                using var list = JsonDocument.Parse(targets);
                path = list.RootElement.ValueKind == JsonValueKind.Array
                    ? list.RootElement.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                        .FirstOrDefault(value => !string.IsNullOrEmpty(value))
                    : targets;
            }
            catch (JsonException) { path = targets; }
        }
        return string.IsNullOrEmpty(path) ? null : path.Length > 256 ? path[..256] : path;
    }

    private static string? Meta(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Array
            ? meta.EnumerateArray().Where(item => Text(item, "key") == key).Select(item => Text(item, "value")).FirstOrDefault()
            : null;

    internal static CrowdSecDecisionView[] ParseDecisions(JsonElement alerts, DateTimeOffset now) => alerts.ValueKind != JsonValueKind.Array ? [] : [.. alerts.EnumerateArray()
        .Where(alert => alert.ValueKind == JsonValueKind.Object)
        .SelectMany(alert => alert.TryGetProperty("decisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array
            ? decisions.EnumerateArray().Where(decision => decision.ValueKind == JsonValueKind.Object).Select(decision => (alert, decision)) : [])
        .Select(item => new CrowdSecDecisionView(item.decision.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) ? number : 0,
            Text(item.decision, "value") ?? "", Text(item.decision, "scope") ?? "Ip", Text(item.decision, "type") ?? "ban",
            Text(item.decision, "origin") ?? "", Text(item.decision, "scenario") ?? Text(item.alert, "scenario"),
            Time(item.decision, "until") ?? (Text(item.decision, "duration") is { } duration ? now + CrowdSec.Duration(duration) : null)))
        .Where(decision => decision.Until is null || decision.Until > now)
        .OrderByDescending(decision => decision.Until)];

    /// <summary>The local API's filter for one address or one network written with its host bits clear; null for anything else.</summary>
    internal static string? UnbanQuery(string? value)
    {
        var target = (value ?? "").Trim();
        if (target.Length is 0 or > 64 || target.Contains('%')) return null;
        if (!target.Contains('/') && IPAddress.TryParse(target, out var address)) return "ip=" + Uri.EscapeDataString(address.ToString());
        return target.Contains('/') && IPNetwork.TryParse(target, out var network) && IPAddress.TryParse(target[..target.IndexOf('/')], out var written)
            && written.Equals(network.BaseAddress) ? "range=" + Uri.EscapeDataString(network.ToString()) : null;
    }

    internal static int Deleted(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("nbDeleted", out var count)
            && int.TryParse(count.ValueKind == JsonValueKind.String ? count.GetString() : count.GetRawText(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : 0;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { Length: > 0 and <= 64 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;
}

public static class CrowdSecEndpoints
{
    public static void AddGatewaySecurity(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("GatewaySecurity").Get<GatewaySecurityOptions>() ?? new();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SafeHarbor>();
        builder.Services.AddSingleton<CrowdSecRules>();
        builder.Services.AddSingleton<AppCertificates>();
        builder.Services.AddSingleton<CrowdSec>();
        builder.Services.AddHostedService(services => services.GetRequiredService<CrowdSec>());
    }

    public static void MapGatewaySecurity(this WebApplication app)
    {
        var security = app.MapGroup("/api/host/security").WithTags("Gateway security")
            .RequireAuthorization("HostOwner").AddEndpointFilter<HardwareOnboardingErrorFilter>();
        security.MapGet("/safe-harbor", (SafeHarbor harbor, CancellationToken ct) => harbor.Read(ct));
        security.MapPut("/safe-harbor", async (HttpContext context, SafeHarbor harbor, TimeProvider time, CancellationToken ct) =>
        {
            var request = await Body<SafeHarborRequest>(context, 16384, "Send the list as {\"addresses\": [...]}.", ct);
            var actor = context.User.FindFirst("preferred_username")?.Value ?? context.User.FindFirst("sub")?.Value ?? "owner API key";
            return await harbor.Save(request, actor, time.GetUtcNow(), ct);
        });
        security.MapGet("/crowdsec", (CrowdSec crowdsec, CancellationToken ct) => crowdsec.Status(ct));
        security.MapGet("/crowdsec/alerts", async (int? limit, string? kind, string? ip, string? range, string? scenario, string? since, CrowdSec crowdsec,
            CancellationToken ct) => new { alerts = await crowdsec.Alerts(new CrowdSecAlertQuery(ip, range, scenario, kind, since, limit), ct) });
        security.MapGet("/crowdsec/alerts/{id:long}", (long id, CrowdSec crowdsec, CancellationToken ct) => crowdsec.Alert(id, ct));
        security.MapGet("/crowdsec/decisions", async (CrowdSec crowdsec, CancellationToken ct) => new { decisions = await crowdsec.Decisions(ct) });
        security.MapPost("/crowdsec/ban", async (HttpContext context, CrowdSec crowdsec, CancellationToken ct) =>
            await crowdsec.Ban(await Body<CrowdSecBanRequest>(context, 2048, "Send the ban as {\"target\": \"203.0.113.7\", \"duration\": \"4h\", \"reason\": \"...\"}.", ct),
                Actor(context), CrowdSec.OwnerOrigin, ct));
        security.MapPost("/crowdsec/unban", async (HttpContext context, CrowdSec crowdsec, CancellationToken ct) =>
            await crowdsec.Unban(await Body<CrowdSecUnbanRequest>(context, 1024, "Send the address as {\"ip\": \"203.0.113.7\"} or a ban as {\"id\": 42}.", ct), ct,
                Actor(context)));
        security.MapGet("/crowdsec/rules", (CrowdSecRules rules, CancellationToken ct) => rules.List(ct));
        security.MapPost("/crowdsec/rules", async (HttpContext context, CrowdSecRules rules, TimeProvider time, CancellationToken ct) =>
            await rules.Add(await Body<CrowdSecRuleRequest>(context, 2048,
                "Send the rule as {\"field\": \"path\", \"operator\": \"prefix\", \"value\": \"/wp-admin\", \"reason\": \"...\"}.", ct), Actor(context), time.GetUtcNow(), ct));
        security.MapDelete("/crowdsec/rules/{id}", (string id, HttpContext context, CrowdSecRules rules, CancellationToken ct) => rules.Remove(id, Actor(context), ct));
        security.MapPut("/crowdsec/enrollment", async (HttpContext context, CrowdSec crowdsec, CancellationToken ct) =>
            await crowdsec.Enroll(await Body<CrowdSecEnrollRequest>(context, 1024, "Send the enroll key as {\"key\": \"...\"}.", ct), ct));
        security.MapDelete("/crowdsec/enrollment", (CrowdSec crowdsec, CancellationToken ct) => crowdsec.ForgetEnrollKey(ct));
    }

    private static string Actor(HttpContext context) =>
        context.User.FindFirst("preferred_username")?.Value ?? context.User.FindFirst("sub")?.Value ?? "owner API key";

    private static async Task<T> Body<T>(HttpContext context, int limit, string help, CancellationToken ct)
    {
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength is null or <= 0 || context.Request.ContentLength > limit)
            throw new HardwareOnboardingException(400, "invalid_body", help);
        try
        {
            return await context.Request.ReadFromJsonAsync<T>(HardwareOnboardingJson.Options, ct)
                ?? throw new HardwareOnboardingException(400, "invalid_body", help);
        }
        catch (JsonException) { throw new HardwareOnboardingException(400, "invalid_body", help); }
    }
}
