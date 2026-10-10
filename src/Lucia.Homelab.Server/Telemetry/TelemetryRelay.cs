using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Stacks;
using OpenTelemetry;

namespace Lucia.Homelab.Server.Telemetry;

/// <summary>An app on the server whose Prometheus metrics the relay scrapes. llama.cpp's router serves them per model.</summary>
/// <param name="Target">An address, or with <paramref name="TargetsFile"/> a Prometheus file_sd file the app's owner keeps current.</param>
internal sealed record RelayScrape(string Job, string Target, string[]? Models = null, bool TargetsFile = false);

/// <summary>
/// The telemetry relay: an OpenTelemetry collector on each machine Lucia manages. Apps and Lucia's agent send it OTLP on
/// loopback (gRPC 14317, HTTP 14318); it scrapes the machine's hardware through node-exporter (and NVIDIA GPUs through
/// nvidia_gpu_exporter), labels everything with the machine's name and forwards it to the Observability app.
/// </summary>
internal static class TelemetryRelay
{
    public const string NodeExporter = "prom/node-exporter:v1.10.2@sha256:3ac34ce007accad95afed72149e0d2b927b7e42fd1c866149b945b84737c62c3";
    public const string GpuExporter = "utkuozdemir/nvidia_gpu_exporter:1.15.1@sha256:7aee2d42836ad29d4adb2722b7cfdc806ec9cc8d45fd0b8971ebbad2d9f3d087";

    /// <summary>A server's relay forwards straight to the Observability app with the address and header Lucia puts in its environment.</summary>
    public const string EnvExporter = """
        otlp_http:
          endpoint: ${env:OTLP_ENDPOINT}
          headers:
            Authorization: ${env:OTLP_AUTHORIZATION}
        """;

    /// <param name="node">The machine's name as a YAML scalar, quoted.</param>
    /// <param name="dockerStats">Read each container's CPU, memory and network counters from the Docker socket, which
    /// <see cref="Compose"/> then mounts. Each series carries the container's name as <c>container.name</c>.</param>
    internal static string Config(string node, bool gpu, IEnumerable<RelayScrape> apps, string exporter, bool dockerStats = false)
    {
        var docker = dockerStats ? """
              docker_stats:
                endpoint: unix:///var/run/docker.sock
                collection_interval: 30s
                metrics:
                  container.cpu.utilization:
                    enabled: true
                  container.memory.percent:
                    enabled: true
                  container.network.io.usage.rx_bytes:
                    enabled: true
                  container.network.io.usage.tx_bytes:
                    enabled: true

            """ : "";
        const string Auth = "  authorization:\n    credentials: ${env:LOCAL_AI_KEY}\n";
        var instance = $"    - target_label: instance\n      replacement: {node}\n";
        string Job(string name, string targets, string extra = "", string relabel = "", string discovery = "static_configs") =>
            $"- job_name: {name}\n{extra}  {discovery}:\n{targets}  relabel_configs:\n{relabel}{instance}";
        var jobs = Job("node", "    - targets: [127.0.0.1:19100]\n");
        if (gpu) jobs += Job("gpu", "    - targets: [127.0.0.1:19835]\n");
        foreach (var app in apps)
            jobs += app.TargetsFile ? Job(app.Job, $"    - files: [{app.Target}]\n", discovery: "file_sd_configs")
                : app.Models is null ? Job(app.Job, $"    - targets: [{app.Target}]\n", Auth)
                : Job(app.Job, string.Concat(app.Models.Select(model => $"    - targets: [{app.Target}]\n      labels:\n        model: \"{model}\"\n")),
                    Auth + "  params:\n    autoload: [\"false\"]\n", "    - source_labels: [model]\n      target_label: __param_model\n");
        return $"""
            receivers:
              otlp:
                protocols:
                  grpc:
                    endpoint: 127.0.0.1:14317
                  http:
                    endpoint: 127.0.0.1:14318
              prometheus:
                config:
                  global:
                    scrape_interval: 30s
                  scrape_configs:
            {Indent(jobs.TrimEnd(), 6)}
            {docker}processors:
              memory_limiter:
                check_interval: 1s
                limit_mib: 200
                spike_limit_mib: 50
              resource:
                attributes:
                  - key: host.name
                    value: {node}
                    action: upsert
                  - key: lucia.node
                    value: {node}
                    action: upsert
              batch:
                timeout: 5s
            exporters:
            {Indent(exporter, 2)}
            service:
              # The relay shares the machine's network, so it keeps its own metrics off port 8888.
              telemetry:
                metrics:
                  level: none
              pipelines:
                traces:
                  receivers: [otlp]
                  processors: [memory_limiter, resource, batch]
                  exporters: [otlp_http]
                metrics:
                  receivers: [otlp, prometheus{(dockerStats ? ", docker_stats" : "")}]
                  processors: [memory_limiter, resource, batch]
                  exporters: [otlp_http]
                logs:
                  receivers: [otlp]
                  processors: [memory_limiter, resource, batch]
                  exporters: [otlp_http]

            """.ReplaceLineEndings("\n");
    }

    /// <summary>The relay as a compose project for a managed server. Everything shares the host's network and binds to loopback.</summary>
    /// <param name="dockerStats">Mount the Docker socket for <see cref="Config"/>'s <c>docker_stats</c> receiver.</param>
    internal static string Compose(string config, bool gpu, bool dockerStats = false) => $$"""
        # Lucia's telemetry relay. Lucia runs it on every server while an Observability app is installed, and rewrites it
        # when the app, the server's GPUs or Local AI change.
        services:
          collector:
            image: {{ObservabilityApp.Collector}}
            restart: unless-stopped
            network_mode: host
            mem_limit: 256m
            command: ["--config=/etc/otelcol-contrib/config.yaml"]
            environment:
              OTLP_ENDPOINT: ${OTLP_ENDPOINT}
              OTLP_AUTHORIZATION: ${OTLP_AUTHORIZATION}
              LOCAL_AI_KEY: ${LOCAL_AI_KEY}
            configs:
              - source: collector
                target: /etc/otelcol-contrib/config.yaml
        {{(dockerStats ? """
                # docker_stats reads the Docker socket (root:docker, 0660). The docker group's id differs per machine and the
                # image has no such group to add by name, so the collector runs as root with every capability dropped instead;
                # anything that can read this socket controls Docker anyway.
                user: "0:0"
                cap_drop: [ALL]
                security_opt: [no-new-privileges:true]
                volumes:
                  - /var/run/docker.sock:/var/run/docker.sock:ro

            """ : "")}}  node-exporter:
            image: {{NodeExporter}}
            restart: unless-stopped
            network_mode: host
            pid: host
            command: ["--path.rootfs=/host", "--web.listen-address=127.0.0.1:19100"]
            volumes:
              - /:/host:ro,rslave
        {{(gpu ? $$"""
              gpu-exporter:
                image: {{GpuExporter}}
                restart: unless-stopped
                network_mode: host
                command: ["--web.listen-address=127.0.0.1:19835"]
                deploy:
                  resources:
                    reservations:
                      devices:
                        - driver: nvidia
                          count: all
                          capabilities: [gpu, utility]

            """ : "")}}configs:
          collector:
            content: |
        {{Indent(config.Replace("$", "$$").TrimEnd(), 6)}}

        """.ReplaceLineEndings("\n");

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Length == 0 ? line : new string(' ', spaces) + line));
}

/// <summary>
/// The controller's own machine gets a relay too, run beside it by the installer. It can't reach the Observability app's
/// credentials, so it sends through the controller: <c>POST /api/host/telemetry/relay/v1/{signal}</c> with a token the
/// controller generates, and the controller forwards to wherever the app is now.
/// </summary>
public sealed class ControllerRelay
{
    public const string Header = "X-Lucia-Relay";
    private readonly byte[] _token;

    public ControllerRelay(string directory, string publicOrigin)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tokenPath = Path.Combine(directory, "relay-token");
        if (!File.Exists(tokenPath))
            Write(tokenPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        var token = File.ReadAllText(tokenPath).Trim();
        _token = Encoding.UTF8.GetBytes(token);
        // The relay runs on the host's network, where the public origin's name reaches the gateway on this machine.
        var exporter = $"""
            otlp_http:
              endpoint: {publicOrigin.TrimEnd('/')}/api/host/telemetry/relay
              headers:
                {Header}: "{token}"
              tls:
                ca_file: /trust/lucia-root-ca.crt
                # Once a domain is active the origin has a public certificate instead.
                include_system_ca_certs_pool: true
            """;
        // spark_model_worker.py lists TensorFold's metrics address in spark-model.json while Qwen3.8-Flash-Next runs.
        Write(Path.Combine(directory, "relay.yaml"), TelemetryRelay.Config("\"${env:LUCIA_NODE}\"", gpu: true,
            [new("spark-model", "/etc/lucia-relay/spark-model.json", TargetsFile: true)], exporter));
    }

    internal bool Accepts(string? token) =>
        token is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token);

    private static void Write(string path, string text)
    {
        var pending = path + ".pending";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(pending, options)) stream.Write(Encoding.UTF8.GetBytes(text));
        File.Move(pending, path, overwrite: true);
    }
}

public static class TelemetryRelayExtensions
{
    private static readonly HttpClient Forwarding = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>Set <c>Telemetry:RelayDirectory</c> to have the controller write its machine's relay configuration there.</summary>
    public static void AddControllerRelay(this WebApplicationBuilder builder)
    {
        if (builder.Configuration["Telemetry:RelayDirectory"] is not { Length: > 0 } directory) return;
        var origin = builder.Configuration["HostAuthentication:PublicOrigin"]
            ?? throw new InvalidOperationException("The controller relay needs HostAuthentication:PublicOrigin.");
        builder.Services.AddSingleton(new ControllerRelay(directory, origin));
    }

    public static void MapControllerRelay(this WebApplication app)
    {
        if (app.Services.GetService<ControllerRelay>() is null) return;
        app.MapPost("/api/host/telemetry/relay/v1/{signal}", async (string signal, HttpContext context, ControllerRelay relay, StackStore stacks,
            CancellationToken ct) =>
        {
            if (signal is not ("traces" or "metrics" or "logs")) return Results.NotFound();
            if (!relay.Accepts(context.Request.Headers[ControllerRelay.Header])) return Results.Unauthorized();
            if (await stacks.TelemetryEndpoint(ct) is not { } destination) return Results.NoContent();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(destination.Endpoint, "v1/" + signal))
            {
                Content = new StreamContent(context.Request.Body),
            };
            if (context.Request.ContentType is { } type) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
            foreach (var encoding in context.Request.Headers.ContentEncoding) request.Content.Headers.ContentEncoding.Add(encoding!);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(destination.Authorization);
            HttpResponseMessage response;
            // Tracing the forward would make telemetry about telemetry on every batch.
            using (SuppressInstrumentationScope.Begin())
            {
                try { response = await Forwarding.SendAsync(request, ct); }
                catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }
            using (response)
            {
                context.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } answer) context.Response.ContentType = answer.ToString();
                await response.Content.CopyToAsync(context.Response.Body, ct);
                return Results.Empty;
            }
        }).AllowAnonymous().ExcludeFromDescription();
    }
}

/// <summary>
/// Sends the controller's own traces, metrics and logs to the Observability app, wherever it is now. The OTLP exporters
/// post to a placeholder address; this points each request at the app, or answers for it when there's no app.
/// </summary>
public sealed class TelemetryForwarder() : DelegatingHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
{
    public static readonly Uri Placeholder = new("http://observability.lucia.invalid/");
    public static Func<CancellationToken, Task<TelemetryDestination?>>? Destination { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        await Point(request, ct) ? await base.SendAsync(request, ct) : Nothing(request);

    // The exporters send synchronously from their own threads.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
        Point(request, ct).GetAwaiter().GetResult() ? base.Send(request, ct) : Nothing(request);

    private static async Task<bool> Point(HttpRequestMessage request, CancellationToken ct)
    {
        if (Destination is null || await Destination(ct) is not { } destination) return false;
        request.RequestUri = new Uri(destination.Endpoint, request.RequestUri!.AbsolutePath.TrimStart('/'));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(destination.Authorization);
        return true;
    }

    private static HttpResponseMessage Nothing(HttpRequestMessage request) => new(HttpStatusCode.NoContent) { RequestMessage = request };
}
