using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Lucia.NodeAgent;

/// <summary>
/// Traces and logs for the managed agent, sent to the node's telemetry relay on loopback. Lucia runs the relay only when
/// an Observability app is installed; until then the exports fail quietly. Calls to Lucia carry the trace, so a heartbeat
/// shows up as one trace across the node and the controller.
/// </summary>
internal sealed class AgentTelemetry : IDisposable
{
    internal static readonly ActivitySource Source = new("Lucia.NodeAgent");
    private const string Relay = "http://127.0.0.1:14318";
    private readonly TracerProvider _tracing;
    private readonly ILoggerFactory _logging;

    private AgentTelemetry()
    {
        var resource = ResourceBuilder.CreateDefault().AddService("lucia-node-agent",
            serviceVersion: typeof(AgentTelemetry).Assembly.GetName().Version?.ToString(), serviceInstanceId: Environment.MachineName);
        _tracing = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource).AddSource(Source.Name).AddHttpClientInstrumentation()
            .AddOtlpExporter(options => Export(options, "/v1/traces")).Build();
        _logging = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeFormattedMessage = true;
            options.AddOtlpExporter(exporter => Export(exporter, "/v1/logs"));
        }));
        // Everything the agent says goes to stderr (the journal); the relay gets the same lines.
        Console.SetError(new Tee(Console.Error, _logging.CreateLogger("Lucia.NodeAgent")));
    }

    internal static AgentTelemetry Start() => new();

    private static void Export(OtlpExporterOptions options, string path)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri(Relay + path);
    }

    public void Dispose()
    {
        _tracing.Dispose();
        _logging.Dispose();
    }

    private sealed class Tee(TextWriter inner, ILogger logger) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => inner.Write(value);
        public override void Write(string? value) => inner.Write(value);
        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (!string.IsNullOrEmpty(value)) logger.LogInformation("{Message}", value);
        }
        public override void Flush() => inner.Flush();
    }
}
