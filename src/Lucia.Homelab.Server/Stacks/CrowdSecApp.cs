using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Security;

namespace Lucia.Homelab.Server.Stacks;

/// <summary>
/// CrowdSec, the engine behind the gateway's protection. Observability's collector forwards the gateway's access log to it
/// over TLS; it decides who to ban and answers the gateway's bouncer (decisions and AppSec) on its local API, also over
/// TLS. Every listener serves a certificate from Lucia's own authority, which Lucia renews and the engine reloads without a
/// restart. A small sidecar holds what Lucia sends after install (that certificate, the safe harbor, a Console enroll key,
/// the owner's custom AppSec rules and where to send alerts) in memory, so a change recreates only the sidecar. Every alert
/// also goes to Observability's collector as an OTLP log record, through the engine's own notification plugin.
/// </summary>
internal sealed partial class CrowdSecApp() : CatalogApp(AppId, 3, "CrowdSec",
    "Protects Lucia's gateway: bans internet addresses that attack your apps and blocks known exploit requests.",
    "Any server with Docker ready and 1 GB of memory. Install Observability first: CrowdSec reads the gateway's access log through its collector.",
    [],
    [
        new("crowdsec-host", "Name", "text", "crowdsec", "The gateway reaches CrowdSec at this name under your domain. Lucia's DNS points it straight at this server."),
        new("lapi-port", "Local API port", "port", "8088", "The gateway fetches bans here, over TLS with a certificate from Lucia's own authority."),
        new("appsec-port", "AppSec port", "port", "7422", "The gateway asks here whether a request is a known exploit."),
        new("logs-port", "Log intake port", "port", "7423", "Observability's collector forwards the gateway's access log here."),
        new("metrics-port", "Metrics port", "port", "19060", "Prometheus metrics, on the server's loopback only, for Lucia's telemetry relay."),
    ])
{
    public const string AppId = "crowdsec";
    internal const string Image = "crowdsecurity/crowdsec:v1.8.1@sha256:0f2523fa61ef507f15d953045cface490cc880670c62f2755ced17524107f71a";
    /// <summary>The gateway's bouncer key, in the app's environment.</summary>
    internal const string GatewayKey = "GATEWAY_KEY";
    /// <summary>The password of <see cref="Machine"/>, the machine Lucia's host signs in to the local API as, to read alerts and lift bans.</summary>
    internal const string MachinePassword = "LUCIA_MACHINE_PASSWORD";
    internal const string Machine = "lucia-host";
    /// <summary>The header value Observability's collector sends with the access log.</summary>
    internal const string IntakeToken = "INTAKE_TOKEN";
    internal const string IntakeHeader = "X-Lucia-Intake";
    internal const string IntakePath = "/v1/logs";
    /// <summary>
    /// The engine's full name, <c>crowdsec-host.namespace</c>, which Lucia writes into the app's environment once a domain is
    /// active. Its certificate names exactly this; inside the engine's container the name points at loopback, so AppSec and
    /// cscli reach the local API under the same name and certificate as the gateway.
    /// </summary>
    internal const string NameVariable = StackStore.CrowdSecPrefix + "NAME";

    /// <summary>The engine's name under the domain's namespace.</summary>
    internal static string FullName(IReadOnlyDictionary<string, string> settings, string ns) => $"{settings["crowdsec-host"].ToLowerInvariant()}.{ns}";
    /// <summary>The bouncer's name carries a digest of its key, so a replaced key registers afresh instead of meeting a stale registration.</summary>
    internal static string BouncerName(string key) =>
        "lucia_gateway_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];

    // The SQLite database is only consistent while the engine is stopped; the in-memory state volume holds nothing to keep.
    public override string BackupMode => "stop";
    public override string[] BackupExclude => ["volumes/state"];
    public override string[] RequiresApps => [ObservabilityApp.AppId];
    public override string RequiresWhy => "CrowdSec reads the gateway's access log through Observability's collector.";
    public override IReadOnlyDictionary<string, int> RequiresVersions { get; } = new Dictionary<string, int> { [ObservabilityApp.AppId] = ObservabilityApp.ShowsCrowdSec };
    public override bool Single => true;

    internal static (int Lapi, int AppSec, int Logs, int Metrics) Ports(IReadOnlyDictionary<string, string> settings)
    {
        int Port(string id) => int.Parse(settings[id], NumberStyles.None, CultureInfo.InvariantCulture);
        return (Port("lapi-port"), Port("appsec-port"), Port("logs-port"), Port("metrics-port"));
    }

    /// <summary>
    /// The Observability app's CrowdSec configs while CrowdSec is installed, each one line of YAML flow style, single-quoted
    /// so compose leaves it alone. While the engine runs, <paramref name="forward"/> (the collector's third config,
    /// <c>LUCIA_CROWDSEC_COLLECTOR</c>) sends the gateway's access-log records (service <c>lucia-gateway</c>) to CrowdSec's log
    /// intake as OTLP/JSON, over TLS that trusts only Lucia's authority, with the intake's token beside it as
    /// <c>LUCIA_CROWDSEC_TOKEN</c>; a failed send is dropped rather than retried, so an engine that's down never backs up the
    /// collector's other pipelines. Once the alert intake has its certificate, <paramref name="intake"/> (the fourth config,
    /// <c>LUCIA_CROWDSEC_INTAKE</c>) adds a TLS OTLP/HTTP receiver for CrowdSec's alerts, behind the collector's own password,
    /// that sends them to Loki. Its certificate and key sit in that line as YAML strings: the collector keeps a string field's
    /// <c>${env:...}</c> value raw, so a PEM can't come from another variable.
    /// </summary>
    internal static string CollectorLines(string endpoint, string caPem, string token) => CollectorLines((endpoint, caPem, token), null);

    internal static string CollectorLines((string Endpoint, string CaPem, string Token)? forward, AppCertificate? intake)
    {
        static string Escaped(string pem) => pem.ReplaceLineEndings("\n").Trim().Replace("\n", "\\n") + "\\n";
        var prefix = StackStore.CrowdSecPrefix;
        var lines = "";
        if (forward is var (endpoint, caPem, token))
        {
            var config = "{processors: {filter/crowdsec: {error_mode: ignore, logs: {log_record: [\"resource.attributes[\\\"service.name\\\"] != \\\"lucia-gateway\\\"\"]}}, "
                + "transform/crowdsec: {error_mode: ignore, log_statements: [{context: log, statements: ["
                + "\"set(log.attributes[\\\"time\\\"], FormatTime(log.time, \\\"%Y-%m-%dT%H:%M:%S.%LZ\\\"))\", \"set(log.body, \\\"\\\")\"]}]}}, "
                + $"exporters: {{otlp_http/crowdsec: {{logs_endpoint: \"{endpoint}\", encoding: json, timeout: 5s, tls: {{ca_pem: \"{Escaped(caPem)}\"}}, "
                + $"headers: {{{IntakeHeader}: \"${{env:{prefix}TOKEN}}\"}}, retry_on_failure: {{enabled: false}}, "
                + "sending_queue: {enabled: true, queue_size: 1000, num_consumers: 4}}}, "
                + "service: {pipelines: {logs/crowdsec: {receivers: [otlp], processors: [memory_limiter, filter/crowdsec, transform/crowdsec, batch], "
                + "exporters: [otlp_http/crowdsec]}}}}";
            lines += $"{prefix}COLLECTOR='{config}'\n{prefix}TOKEN={token}\n";
        }
        if (intake is not null)
        {
            var config = $"{{receivers: {{{AlertsReceiver}: {{protocols: {{http: {{endpoint: \"0.0.0.0:{ObservabilityApp.AlertsContainerPort}\", "
                + $"auth: {{authenticator: basicauth/server}}, tls: {{cert_pem: \"{Escaped(intake.ChainPem)}\", key_pem: \"{Escaped(intake.KeyPem)}\", min_version: \"1.2\"}}}}}}}}}}, "
                + $"service: {{pipelines: {{logs/crowdsec_alerts: {{receivers: [{AlertsReceiver}], processors: [memory_limiter, batch], exporters: [otlphttp/loki]}}}}}}}}";
            lines += $"{prefix}INTAKE='{config}'\n";
        }
        return lines;
    }

    /// <summary>The collector's receiver for CrowdSec's alerts.</summary>
    internal const string AlertsReceiver = "otlp/crowdsec_alerts";

    /// <summary>The alert intake's certificate as Lucia wrote it into the Observability app's environment, or null.</summary>
    internal static AppCertificate? IntakeCertificate(IReadOnlyDictionary<string, string> env)
    {
        if (env.GetValueOrDefault(StackStore.CrowdSecPrefix + "INTAKE") is not { Length: > 0 } config) return null;
        string? Pem(string field) => Regex.Match(config, field + ": \"([A-Za-z0-9+/=\\\\ -]+)\"") is { Success: true } match
            ? match.Groups[1].Value.Replace("\\n", "\n", StringComparison.Ordinal) : null;
        try
        {
            if (Pem("cert_pem") is not { } chain || Pem("key_pem") is not { } key) return null;
            using var leaf = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(chain);
            return new(chain, key, leaf.NotAfter.ToUniversalTime());
        }
        catch (Exception error) when (error is CryptographicException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Lucia's web address for the Observability app while CrowdSec is installed: the Lucia CrowdSec dashboard links to the
    /// Gateway protection page there. Nothing for an address that isn't a plain https origin.
    /// </summary>
    internal static string ConsoleLine(string? origin) =>
        origin is not null && ConsoleOrigin().IsMatch(origin) ? $"{StackStore.CrowdSecPrefix}CONSOLE={origin.TrimEnd('/')}\n" : "";

    [GeneratedRegex(@"\Ahttps://[a-z0-9]([a-z0-9.-]{0,251}[a-z0-9])?(:[0-9]{1,5})?/?\z")]
    private static partial Regex ConsoleOrigin();

    internal override CatalogOutput Render(IReadOnlyDictionary<string, string> settings, ManagedNodeFacts node, IReadOnlyDictionary<string, string> env)
    {
        var (lapi, appsec, logs, metrics) = Ports(settings);
        var host = settings["crowdsec-host"].Trim().ToLowerInvariant();
        if (!StackStore.RouteHostPattern().IsMatch(host))
            throw new HardwareOnboardingException(400, "invalid_setting", "CrowdSec's name uses lowercase letters, digits and hyphens, such as crowdsec.");
        if (new[] { lapi, appsec, logs, metrics }.Distinct().Count() != 4)
            throw new HardwareOnboardingException(400, "invalid_setting", "CrowdSec's four ports must all be different.");
        var engine = new (string Name, string Target, string Content)[]
        {
            ("start", "/lucia/config/start.sh", Start),
            ("settings", "/lucia/config/config.yaml.local", Settings),
            ("acquisition", "/lucia/config/acquis.yaml", Acquisition),
            ("profiles", "/lucia/config/profiles.yaml", Profiles(DomainIngressConfiguration.CloudflareRanges, notify: true)),
            ("profiles-quiet", "/lucia/config/profiles-quiet.yaml", Profiles(DomainIngressConfiguration.CloudflareRanges, notify: false)),
            ("notifications", "/lucia/config/notifications.yaml", Notifications),
            ("appsec", "/lucia/config/lucia-appsec.yaml", AppSecConfig),
            ("appsec-placeholder", "/lucia/config/lucia-placeholder.yaml", AppSecPlaceholder),
            ("telemetry", "/lucia/config/lucia-telemetry.yaml", TelemetryWhitelist),
        };
        static string Mounts(IEnumerable<(string Name, string Target, string Content)> items) =>
            string.Concat(items.Select(item => $"      - source: {item.Name}\n        target: {item.Target}\n"));
        static string Indent(string text, int depth) => string.Join("\n", text.ReplaceLineEndings("\n").TrimEnd().Split('\n')
            .Select(line => line.Length == 0 ? line : new string(' ', depth) + line)).Replace("$", "$$");
        static string Content(string text) => string.Concat(text.ReplaceLineEndings("\n").TrimEnd().Split('\n')
            .Select(line => (line.Length == 0 ? "" : "      " + line) + "\n")).Replace("$", "$$");
        // Compose before 2.30 keeps a container when only its inline configs change; the label makes it recreate.
        static string Hash(IEnumerable<(string Name, string Target, string Content)> items) => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\0', items.Select(item => item.Content.ReplaceLineEndings("\n"))))))[..16];
        var compose = $$"""
            # Installed from Lucia's catalog (crowdsec, version {{Version}}). Lucia rewrites this file when the catalog
            # updates the app. Convert the app to a custom app to edit it by hand.
            services:
              # Holds what Lucia sends after install, in memory: the engine's TLS certificate from Lucia's own authority, the
              # safe harbor, a Console enroll key, the owner's custom AppSec rules and the Observability collector that receives
              # alerts. A change recreates only this container; the engine follows its files.
              lucia:
                image: {{ObservabilityApp.Alpine}}
                restart: unless-stopped
                init: true
                network_mode: none
                read_only: true
                cap_drop: [ALL]
                security_opt: ["no-new-privileges:true"]
                pids_limit: 16
                mem_limit: 32m
                entrypoint:
                  - /bin/sh
                  - -c
                  - |
            {{Indent(Sidecar, 8)}}
                environment:
                  CA_CERT: ${LUCIA_CROWDSEC_CA:-}
                  TLS_CERT: ${LUCIA_CROWDSEC_TLS_CERT:-}
                  TLS_KEY: ${LUCIA_CROWDSEC_TLS_KEY:-}
                  HARBOR: ${LUCIA_CROWDSEC_HARBOR:-}
                  ENROLL_KEY: ${LUCIA_CROWDSEC_ENROLL_KEY:-}
                  ENROLL_ID: ${LUCIA_CROWDSEC_ENROLL_ID:-}
                  APPSEC_RULES: ${LUCIA_CROWDSEC_APPSEC_RULES:-}
                  OTLP_ENDPOINT: ${LUCIA_CROWDSEC_OTLP_ENDPOINT:-}
                  OTLP_AUTHORIZATION: ${LUCIA_CROWDSEC_OTLP_AUTHORIZATION:-}
                volumes:
                  - state:/lucia/state
              crowdsec:
                image: {{Image}}
                restart: unless-stopped
                init: true
                entrypoint: ["/bin/bash", "/lucia/config/start.sh"]
                environment:
                  # traefik brings the access-log parser and the HTTP scenarios; the AppSec collections are the in-band rules;
                  # good actors keeps search engines and CDNs out of the scenarios.
                  COLLECTIONS: crowdsecurity/traefik crowdsecurity/http-cve crowdsecurity/base-http-scenarios crowdsecurity/appsec-virtual-patching crowdsecurity/appsec-generic-rules crowdsecurity/whitelist-good-actors
                  # The stock whitelist drops every private address before a scenario sees it; Lucia records LAN alerts instead.
                  DISABLE_PARSERS: crowdsecurity/whitelists
                  # The engine, AppSec and cscli reach the local API under the engine's own name, which extra_hosts points at the
                  # container's loopback, and verify its certificate against Lucia's authority like everyone else. Until Lucia
                  # sends the name (with the certificate the engine waits for), localhost stands in so compose can start.
                  USE_TLS: "true"
                  CACERT_FILE: /lucia/state/ca.crt
                  LAPI_CERT_FILE: /lucia/state/tls.crt
                  LAPI_KEY_FILE: /lucia/state/tls.key
                  LOCAL_API_URL: https://${{{NameVariable}}:-localhost}:8080
                  AGENT_USERNAME: {{Machine}}
                  AGENT_PASSWORD: ${{{MachinePassword}}}
                  LUCIA_GATEWAY_KEY: ${{{GatewayKey}}}
                  LUCIA_INTAKE_TOKEN: ${{{IntakeToken}}}
                  LUCIA_ENGINE_NAME: lucia-{{node.Hostname}}
                  # The notification plugin runs as nobody and serves the engine on a unix socket; this makes the socket writable
                  # for the nobody group, which group_add gives the engine, since it has no CAP_DAC_OVERRIDE to open it otherwise.
                  PLUGIN_UNIX_SOCKET_GROUP: nobody
                extra_hosts:
                  - "${{{NameVariable}}:-localhost}:127.0.0.1"
                labels:
                  lucia.configs: "{{Hash(engine)}}"
                configs:
            {{Mounts(engine)}}    # Not read-only: compose writes inline configs into the container, which a read-only root refuses.
                tmpfs:
                  - /tmp:mode=1777
                cap_drop: [ALL]
                # Only to start the alert notification plugin as nobody; no-new-privileges still forbids gaining anything back.
                cap_add: [SETUID, SETGID]
                group_add: ["65534"]
                security_opt: ["no-new-privileges:true"]
                pids_limit: 512
                mem_limit: 1g
                volumes:
                  - data:/var/lib/crowdsec/data
                  - config:/etc/crowdsec
                  - state:/lucia/state:ro
                ports:
                  - "{{lapi}}:8080"
                  - "{{appsec}}:7422"
                  - "{{logs}}:7423"
                  # Metrics stay on the server's loopback, where Lucia's telemetry relay scrapes them.
                  - "127.0.0.1:{{metrics}}:6060"
                healthcheck:
                  test: ["CMD", "cscli", "lapi", "status"]
                  interval: 30s
                  timeout: 10s
                  retries: 3
                  start_period: 10m
            configs:
            {{string.Concat(engine.Select(item => $"  {item.Name}:\n    content: |\n{Content(item.Content)}"))}}volumes:
              data:
              config:
              # In memory: the certificate's key never reaches the server's disk, and backups and moves skip it.
              state:
                driver_opts:
                  type: tmpfs
                  device: tmpfs
                  o: size=1m,mode=0700

            """;
        var envText = $"{GatewayKey}={StackCatalog.Secret(env, GatewayKey)}\n"
            + $"{MachinePassword}={StackCatalog.Secret(env, MachinePassword)}\n"
            + $"{IntakeToken}={StackCatalog.Secret(env, IntakeToken)}\n";
        return new(compose.ReplaceLineEndings("\n"), envText, [], null, [host]);
    }

    /// <summary>
    /// Writes each file atomically; the certificate's marker comes last, once the pair is complete and everything else is in
    /// place, so the engine starts with the rules and the collector Lucia sent. Inline, so its root stays read-only.
    /// </summary>
    internal const string Sidecar = """
        set -eu
        umask 077
        d=/lucia/state
        put() { printf '%s' "$2" > "$d/.$1" && mv -f "$d/.$1" "$d/$1"; }
        put harbor.txt "$(printf '%s' "$HARBOR" | tr ',' '\n')"
        put enroll-key "$ENROLL_KEY"
        put enroll-id "$ENROLL_ID"
        printf '%s' "$APPSEC_RULES" | base64 -d > "$d/.appsec-rules.yaml" && mv -f "$d/.appsec-rules.yaml" "$d/appsec-rules.yaml"
        put otlp-endpoint "$OTLP_ENDPOINT"
        put otlp-authorization "$OTLP_AUTHORIZATION"
        if [ -n "$TLS_CERT" ] && [ -n "$TLS_KEY" ] && [ -n "$CA_CERT" ]; then
          printf '%s' "$CA_CERT" | base64 -d > "$d/.ca.crt" && chmod 0644 "$d/.ca.crt" && mv -f "$d/.ca.crt" "$d/ca.crt"
          printf '%s' "$TLS_KEY" | base64 -d > "$d/.tls.key" && mv -f "$d/.tls.key" "$d/tls.key"
          printf '%s' "$TLS_CERT" | base64 -d > "$d/.tls.crt" && mv -f "$d/.tls.crt" "$d/tls.crt"
          put tls.sha256 "$(cat "$d/tls.crt" "$d/tls.key" "$d/ca.crt" | sha256sum | cut -c1-64)"
        fi
        exec sleep infinity
        """;

    /// <summary>
    /// OpenTelemetry Collectors on the LAN (Lucia's telemetry relays, or the owner's own) sending OTLP to Observability.
    /// Their user agent, <c>OpenTelemetry Collector Contrib/…</c>, trips http-bad-user-agent's <c>\bCollector\b</c> entry on
    /// every export. Only a POST to an OTLP path, from a private address, with that user agent is dropped; the same user agent
    /// from the internet, or on any other path, is still checked.
    /// </summary>
    internal const string TelemetryWhitelist = """
        name: lucia/telemetry-collectors
        description: "OpenTelemetry Collectors on the LAN exporting OTLP to Lucia's Observability app"
        whitelist:
          reason: "Lucia telemetry export"
          expression:
            - >-
              evt.Meta.log_type == 'http_access-log' && evt.Meta.http_verb == 'POST'
              && evt.Meta.http_user_agent startsWith 'OpenTelemetry Collector'
              && (evt.Meta.http_path in ['/v1/logs', '/v1/metrics', '/v1/traces'] || evt.Meta.http_path startsWith '/opentelemetry.proto.collector.')
              && (IpInRange(evt.Meta.source_ip, '10.0.0.0/8') || IpInRange(evt.Meta.source_ip, '172.16.0.0/12') || IpInRange(evt.Meta.source_ip, '192.168.0.0/16'))
        """;

    /// <summary>
    /// Waits for the certificate, registers the gateway's bouncer, puts the owner's custom AppSec rules and the alert
    /// notifications in place, then runs the image's own start script. A loop beside the engine follows the sidecar's files
    /// (reloading on a renewed certificate, changed custom rules or a new collector, syncing the safe-harbor allowlist,
    /// enrolling in the Console) and logs a <c>lucia-status</c> line every minute for Lucia's CrowdSec page. Nothing in it
    /// can stop the engine. A reload is CrowdSec's own SIGHUP: the process and its container keep running.
    /// </summary>
    internal const string Start = """
        #!/bin/bash
        set -euo pipefail
        state=/lucia/state
        run=/tmp/lucia
        install -d -m 0755 /etc/crowdsec /etc/crowdsec/appsec-configs /etc/crowdsec/appsec-rules
        install -d -m 0700 "$run" "$run/notifications"
        install -m 0644 /lucia/config/config.yaml.local /etc/crowdsec/config.yaml.local
        install -D -m 0644 /lucia/config/lucia-telemetry.yaml /etc/crowdsec/parsers/s02-enrich/lucia-telemetry.yaml
        install -m 0644 /lucia/config/lucia-appsec.yaml /etc/crowdsec/appsec-configs/lucia-custom.yaml
        install -m 0644 /lucia/config/lucia-placeholder.yaml /etc/crowdsec/appsec-rules/lucia-placeholder.yaml

        waited=0
        until [ -s "$state/tls.sha256" ]; do
          if [ $((waited % 300)) -eq 0 ]; then echo "lucia: waiting for this engine's TLS certificate from Lucia"; fi
          sleep 5
          waited=$((waited + 5))
        done

        bouncer="lucia_gateway_$(printf '%s' "$LUCIA_GATEWAY_KEY" | sha256sum | cut -c1-12)"
        export "BOUNCER_KEY_$bouncer=$LUCIA_GATEWAY_KEY"
        unset LUCIA_GATEWAY_KEY

        reload=0
        tls_seen=$(cat "$state/tls.sha256")
        certificate() {
          local marker
          marker=$(cat "$state/tls.sha256" 2>/dev/null || true)
          [ -n "$marker" ] && [ "$marker" != "$tls_seen" ] || return 0
          [ "$(cat "$state/tls.crt" "$state/tls.key" "$state/ca.crt" | sha256sum | cut -c1-64)" = "$marker" ] || return 0
          tls_seen=$marker
          echo "lucia: reloading for the renewed TLS certificate"
          reload=1
        }

        # The owner's custom AppSec rules arrive as one YAML stream; each document becomes one rule file, lucia/custom-<id>.
        # Lucia writes a line of three dashes between rules and keeps every value on one line, so each document stands alone: one
        # that CrowdSec's YAML reader refuses is skipped and named in the status line, and the rest still load.
        rules_seen=""
        rules_problem=""
        rules() {
          local file=$state/appsec-rules.yaml dir=/etc/crowdsec/appsec-rules seen work part name count bad=""
          [ -r "$file" ] || return 0
          seen=$(sha256sum "$file" | cut -c1-64)
          [ "$seen" != "$rules_seen" ] || return 0
          work=$(mktemp -d)
          awk -v dir="$work" 'BEGIN { n = 1; out = sprintf("%s/rule-%03d.yaml", dir, n) }
            /^---$/ { close(out); n++; out = sprintf("%s/rule-%03d.yaml", dir, n); next }
            { print > out }' "$file"
          rm -f "$dir"/lucia-custom-*.yaml
          for part in "$work"/rule-*.yaml; do
            [ -s "$part" ] || continue
            if name=$(yq -r '.name' "$part" 2>/dev/null) && printf '%s' "$name" | grep -Eqx 'lucia/custom-[0-9a-f]{8}'; then
              install -m 0644 "$part" "$dir/lucia-custom-${name#lucia/custom-}.yaml"
            else
              name=$(sed -n 's/^name: \(lucia\/custom-[0-9a-f]\{8\}\)$/\1/p' "$part" | head -n 1)
              bad="${bad:+$bad, }${name:-rule $(basename "$part" .yaml | sed 's/^rule-0*//')}"
            fi
          done
          rm -rf "$work"
          count=$(find "$dir" -maxdepth 1 -name 'lucia-custom-*.yaml' | wc -l)
          rules_seen=$seen
          if [ -n "$bad" ]; then
            rules_problem="CrowdSec couldn't read $bad, so it runs without them."
            echo "lucia: AppSec has $count custom rules from Lucia; skipped $bad, which CrowdSec couldn't read"
          else
            rules_problem=""
            echo "lucia: AppSec has $count custom rules from Lucia"
          fi
          reload=1
        }

        # Every alert goes to Observability's collector as an OTLP log record, through the http notification plugin. Without a
        # collector the profiles name no notification, so nothing waits on one.
        alerts_seen=""
        alerts() {
          local endpoint auth seen
          endpoint=$(cat "$state/otlp-endpoint" 2>/dev/null || true)
          auth=$(cat "$state/otlp-authorization" 2>/dev/null || true)
          if ! printf '%s' "$endpoint" | grep -Eqx 'https://[A-Za-z0-9.-]+(:[0-9]{1,5})?' \
            || ! printf '%s' "$auth" | grep -Eqx 'Basic [A-Za-z0-9+/=]+'; then endpoint=""; auth=""; fi
          seen=$(printf '%s\n%s\n%s' "$endpoint" "$auth" "$(cat "$state/ca.crt")" | sha256sum | cut -c1-64)
          [ "$seen" != "$alerts_seen" ] || return 0
          if [ -n "$endpoint" ]; then
            # The plugin runs as nobody and reads Lucia's root itself, which the state volume (0700) keeps from it.
            install -d -m 0755 /tmp/lucia-ca && install -m 0644 "$state/ca.crt" /tmp/lucia-ca/.ca.crt && mv -f /tmp/lucia-ca/.ca.crt /tmp/lucia-ca/ca.crt || return 1
            (umask 077 && URL="$endpoint/v1/logs" AUTH="$auth" yq '.url = strenv(URL) | .headers.Authorization = strenv(AUTH)' \
              /lucia/config/notifications.yaml > "$run/notifications/.lucia.yaml") || return 1
            mv -f "$run/notifications/.lucia.yaml" "$run/notifications/lucia.yaml"
            install -m 0644 /lucia/config/profiles.yaml "$run/.profiles.yaml" && mv -f "$run/.profiles.yaml" "$run/profiles.yaml"
            echo "lucia: alerts go to Observability's collector at $endpoint"
          else
            rm -f "$run/notifications/lucia.yaml"
            install -m 0644 /lucia/config/profiles-quiet.yaml "$run/.profiles.yaml" && mv -f "$run/.profiles.yaml" "$run/profiles.yaml"
            echo "lucia: no Observability collector, so alerts stay in CrowdSec"
          fi
          alerts_seen=$seen
          reload=1
        }

        rules || echo "lucia: the custom AppSec rules could not be applied; retrying" >&2
        alerts || echo "lucia: the alert notifications could not be configured; retrying" >&2

        harbor_seen=""
        harbor_synced=0
        harbor_count=0
        harbor() {
          local file=$state/harbor.txt list=lucia-safe-harbor wanted current add remove seen now
          [ -r "$file" ] || return 0
          seen=$(sha256sum "$file")
          now=$(date +%s)
          if [ "$seen" = "$harbor_seen" ] && [ $((now - harbor_synced)) -lt 600 ]; then return 0; fi
          wanted=$(grep -E '^[0-9A-Fa-f:.]+(/[0-9]{1,3})?$' "$file" | sort -u || true)
          if ! cscli allowlists inspect "$list" -o json >/dev/null 2>&1; then
            cscli allowlists create "$list" -d "Lucia safe harbor: never banned, never checked by AppSec. Managed by Lucia." >/dev/null || return 1
          fi
          current=$(cscli allowlists inspect "$list" -o json | yq -p json -r '(.items // [])[] | .value' | sort -u) || return 1
          add=$(comm -23 <(printf '%s\n' "$wanted") <(printf '%s\n' "$current") | grep . || true)
          remove=$(comm -13 <(printf '%s\n' "$wanted") <(printf '%s\n' "$current") | grep . || true)
          if [ -n "$add" ]; then
            # shellcheck disable=SC2086
            cscli allowlists add "$list" $add -d "Lucia safe harbor" >/dev/null || return 1
            echo "lucia: the safe harbor now allows" $add
          fi
          if [ -n "$remove" ]; then
            # shellcheck disable=SC2086
            cscli allowlists remove "$list" $remove >/dev/null || return 1
            echo "lucia: the safe harbor no longer allows" $remove
          fi
          harbor_seen=$seen
          harbor_synced=$now
          harbor_count=$(printf '%s' "$wanted" | grep -c . || true)
        }

        # One attempt per key and request: a new one is tried at once, a failed one again every ten minutes. The key is never logged.
        enroll_state=none
        enroll_message=""
        enroll_at=""
        enroll_tried=""
        enroll_last=0
        enroll() {
          local key id attempt marker now output
          key=$(tr -d '[:space:]' < "$state/enroll-key" 2>/dev/null || true)
          if [ -z "$key" ]; then enroll_state=none; enroll_message=""; return 0; fi
          id=$(tr -d '[:space:]' < "$state/enroll-id" 2>/dev/null || true)
          attempt=$(printf '%s\n%s' "$key" "$id" | sha256sum | cut -c1-16)
          marker=/var/lib/crowdsec/data/.lucia-enrolled-$attempt
          if [ -e "$marker" ]; then enroll_state=sent; enroll_message=""; enroll_at=$(date -u -r "$marker" +%Y-%m-%dT%H:%M:%SZ); return 0; fi
          now=$(date +%s)
          if [ "$attempt" = "$enroll_tried" ] && [ $((now - enroll_last)) -lt 600 ]; then return 0; fi
          enroll_tried=$attempt
          enroll_last=$now
          enroll_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
          if output=$(cscli console enroll --name "$LUCIA_ENGINE_NAME" --tags lucia "$key" 2>&1); then
            touch "$marker"
            enroll_state=sent
            enroll_message=""
            console_at=0
            echo "lucia: enrollment sent to the CrowdSec Console; accept this engine there"
          else
            enroll_state=failed
            output=${output//"$key"/<key>}
            enroll_message=$(printf '%s' "$output" | sed -n 's/.*Error: //p' | tail -n 1 | tr -cd 'A-Za-z0-9 .,:;()/_-' | cut -c1-240)
            echo "lucia: CrowdSec Console enrollment failed; retrying in ten minutes" >&2
          fi
        }

        console='{}'
        console_at=0
        enrolled=""
        consolecheck() {
          local now value
          now=$(date +%s)
          [ $((now - console_at)) -ge 300 ] || return 0
          console_at=$now
          value=$(cscli console status -o json 2>/dev/null | yq -p json -o json -I0 '.console // {}') || return 0
          console=$value
          value=$(printf '%s' "$console" | yq -p json -r '.enrolled // false')
          # Console features start with the engine's next reload once the owner accepts it.
          if [ "$value" = true ] && [ "$enrolled" = false ]; then
            echo "lucia: accepted in the CrowdSec Console; reloading"
            kill -HUP "$main"
          fi
          enrolled=$value
        }

        status() {
          local version bouncers metrics capi
          version=$(cscli version 2>&1 | sed -n 's/^version: //p' | head -n 1)
          bouncers=$(cscli bouncers list -o json 2>/dev/null \
            | B="$bouncer" yq -p json -o json -I0 '[.[] | select(.name == strenv(B)) | {"name": .name, "lastPull": .last_pull, "revoked": .revoked}]') || bouncers='[]'
          metrics=$(cscli metrics show acquisition appsec -o json 2>/dev/null | yq -p json -o json -I0 '{
            "linesRead": ([(.acquisition // {})[] | .reads // 0] | .[] as $x ireduce (0; . + $x)),
            "linesParsed": ([(.acquisition // {})[] | .parsed // 0] | .[] as $x ireduce (0; . + $x)),
            "linesUnparsed": ([(.acquisition // {})[] | .unparsed // 0] | .[] as $x ireduce (0; . + $x)),
            "appsecProcessed": ([(."appsec-engine" // {})[] | .processed // 0] | .[] as $x ireduce (0; . + $x)),
            "appsecBlocked": ([(."appsec-engine" // {})[] | .blocked // 0] | .[] as $x ireduce (0; . + $x))}') || metrics='{}'
          capi=$(cscli config show-yaml 2>/dev/null | yq -o json -I0 '{
            "registered": ((.api.server.online_client.credentials_path // "") != ""),
            "sharing": (.api.server.online_client.sharing == false | not),
            "blocklist": (.api.server.online_client.pull.community == false | not)}') || capi='{}'
          NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ) VERSION=$version BOUNCERS=$bouncers METRICS=$metrics CAPI=$capi CONSOLE=$console \
            ENROLL_STATE=$enroll_state ENROLL_MESSAGE=$enroll_message ENROLL_AT=$enroll_at HARBOR=$harbor_count \
            RULES=$(find /etc/crowdsec/appsec-rules -maxdepth 1 -name 'lucia-custom-*.yaml' | wc -l) RULES_PROBLEM=$rules_problem \
            yq -n -o json -I0 '{"v": 1, "at": strenv(NOW), "version": strenv(VERSION), "bouncers": (strenv(BOUNCERS) | from_json),
              "metrics": (strenv(METRICS) | from_json), "capi": (strenv(CAPI) | from_json), "console": (strenv(CONSOLE) | from_json),
              "enroll": {"state": strenv(ENROLL_STATE), "message": strenv(ENROLL_MESSAGE), "at": strenv(ENROLL_AT)},
              "harbor": (strenv(HARBOR) | to_number), "rules": (strenv(RULES) | to_number), "rulesProblem": strenv(RULES_PROBLEM)}' | sed 's/^/lucia-status /' || true
        }

        main=$$
        (
          set +e
          until cscli lapi status >/dev/null 2>&1; do sleep 5; done
          # Keys Lucia replaced leave their old registrations behind.
          for stale in $(cscli bouncers list -o json 2>/dev/null | yq -p json -r '.[].name' | grep '^lucia_gateway_' | grep -vx "$bouncer"); do
            cscli bouncers delete "$stale" >/dev/null 2>&1 && echo "lucia: removed the replaced bouncer $stale"
          done
          tick=0
          while :; do
            reload=0
            certificate
            rules || echo "lucia: the custom AppSec rules could not be applied; retrying" >&2
            alerts || echo "lucia: the alert notifications could not be configured; retrying" >&2
            if [ "$reload" = 1 ]; then kill -HUP "$main"; fi
            harbor || echo "lucia: the safe-harbor allowlist could not be updated; retrying" >&2
            enroll
            consolecheck
            if [ $((tick % 3)) -eq 0 ]; then status; fi
            tick=$((tick + 1))
            sleep 20
          done
        ) &

        exec /bin/bash /docker_start.sh "$@"
        """;

    /// <summary>Lucia's changes to the image's config.yaml; CrowdSec merges this file over it at every start and reload.</summary>
    internal const string Settings = """
        crowdsec_service:
          acquisition_path: /lucia/config/acquis.yaml
        config_paths:
          # The start script writes the alert notifications here, in memory, with the collector's address and credentials.
          notification_dir: /tmp/lucia/notifications/
        # The engine keeps only SETUID and SETGID, which it needs to start the notification plugin as this user.
        plugin_config:
          user: nobody
          group: nobody
        api:
          server:
            listen_uri: 0.0.0.0:8080
            # The start script picks profiles.yaml (with notifications) or profiles-quiet.yaml (without) and copies it here.
            profiles_path: /tmp/lucia/profiles.yaml
            tls:
              cert_file: /lucia/state/tls.crt
              key_file: /lucia/state/tls.key
              # USE_TLS also sets the CA for client certificates; clients sign in with keys and passwords only.
              ca_cert_path: ""
            online_client:
              sharing: true
        db_config:
          flush:
            # Ban escalation (profiles.yaml) counts an address's decisions from the last 30 days.
            max_age: 30d
        prometheus:
          enabled: true
          # full adds cs_active_decisions, cs_alerts and the local API's per-route timings, which the dashboards chart.
          level: full
          listen_addr: 0.0.0.0
          listen_port: 6060
        """;

    /// <summary>
    /// Observability's collector posts the gateway's access-log records as OTLP/JSON. The transform turns each record's
    /// attributes into the JSON line Traefik would have written, so crowdsecurity/traefik-logs reads it, with ClientHost
    /// (the visitor chain behind Cloudflare, the real address everywhere else) as the source.
    /// </summary>
    internal const string Acquisition = """
        source: http
        listen_addr: 0.0.0.0:7423
        path: /v1/logs
        auth_type: headers
        headers:
          X-Lucia-Intake: ${LUCIA_INTAKE_TOKEN}
        tls:
          server_cert: /lucia/state/tls.crt
          server_key: /lucia/state/tls.key
        # CrowdSec 1.8.1 files every transform under an empty id, so it also runs on any datasource without an id of its own:
        # AppSec below gets one. The guard passes anything that isn't an access-log post through unchanged all the same.
        transform: |
          evt.Line.Labels.type != "traefik" ? nil : map(flatten(map(flatten(map(JsonExtractSlice(evt.Line.Raw, "resourceLogs") ?? [], #.scopeLogs ?? [])), #.logRecords ?? [])), "{" + join(map(#.attributes ?? [], ToJsonString(#.key) + ":" + ToJsonString(#.value.stringValue ?? #.value.intValue ?? "")), ",") + "}")
        labels:
          type: traefik
        ---
        # AppSec: the gateway's bouncer asks here before passing a request on. Its blocks become alerts (kind waf), which the
        # profiles treat like any other: LAN addresses are recorded only. lucia/custom adds the owner's own rules.
        source: appsec
        unique_id: lucia-appsec
        listen_addr: 0.0.0.0:7422
        cert_file: /lucia/state/tls.crt
        key_file: /lucia/state/tls.key
        appsec_configs:
          - crowdsecurity/appsec-default
          - lucia/custom
        labels:
          type: appsec
        """;

    /// <summary>
    /// The AppSec config for the owner's custom rules, which Lucia renders from its own list (<see cref="Security.CrowdSecRules"/>)
    /// into <c>appsec-rules/lucia-custom-*.yaml</c>. They run in band, beside the community rules, and block with a 403.
    /// </summary>
    internal const string AppSecConfig = """
        name: lucia/custom
        default_remediation: ban
        inband_rules:
          - lucia/custom-*
        """;

    /// <summary>CrowdSec refuses to start when <c>lucia/custom-*</c> names no rule, so this one is always there. No request path can equal it.</summary>
    internal const string AppSecPlaceholder = """
        name: lucia/custom-none
        description: "Keeps lucia/custom-* from being empty. Matches nothing: request paths start with /."
        rules:
          - zones:
              - URI
            match:
              type: equals
              value: "lucia:never-matches"
        labels:
          type: exploit
          service: http
          confidence: 0
          spoofable: 0
          behavior: "http:exploit"
          label: "Lucia placeholder"
        """;

    /// <summary>The CrowdSec <c>source_class</c> each profile's alerts carry in Loki: trusted for Cloudflare's proxies, lan, range, public or other.</summary>
    internal static readonly string[] AlertClasses = ["trusted", "lan", "range", "public", "other"];

    /// <summary>
    /// Lucia's remediation policy. Profiles run in order and the first match decides; the safe harbor never gets here. With
    /// <paramref name="notify"/>, each profile also sends its alerts to Observability's collector, named by their class, and
    /// two last profiles catch what no other matches (an AppSec block from the internet, which bans nobody) for the same reason.
    /// </summary>
    internal static string Profiles(IEnumerable<string> cloudflare, bool notify)
    {
        static string Ranges(IEnumerable<string> ranges) =>
            string.Join(" ||\n", ranges.Select(range => $"      IpInRange(Alert.GetValue(), '{range}')"));
        string Notify(string type) => notify ? $"notifications:\n  - lucia_alerts_{type}\n" : "";
        var record = notify ? $$"""
            ---
            # Nothing above matched, so nothing is banned: an AppSec block from the internet, which CrowdSec counts as handled. The
            # alert still goes to Observability.
            name: lucia_public_recorded
            filters:
              - Alert.GetScope() == "Ip"
            {{Notify("public")}}on_success: break
            ---
            name: lucia_other_recorded
            filters:
              - "true"
            {{Notify("other")}}on_success: break

            """ : "";
        return $$"""
            # Lucia's remediation policy for alerts from the gateway's access log and AppSec. Profiles run in order and the first
            # match decides. Safe-harbor addresses never get here: the engine's lucia-safe-harbor allowlist drops their alerts first.

            # Cloudflare's proxies carry every public visitor. The access log and the bouncer both name the visitor, so a scenario
            # should never see a proxy; if one does, it's recorded and never banned.
            name: lucia_cloudflare_never_banned
            filters:
              - >-
                Alert.GetScope() == "Ip" && (
            {{Ranges(cloudflare)}})
            {{Notify("trusted")}}on_success: break
            ---
            # The LAN and other private addresses: the alert is recorded and nothing is banned automatically. A ban by hand
            # (cscli decisions add, the CrowdSec Console or Lucia's assistant) still applies.
            name: lucia_private_alert_only
            filters:
              - >-
                Alert.GetScope() == "Ip" && (
            {{Ranges(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "100.64.0.0/10", "::1/128", "fc00::/7", "fe80::/10"])}})
            {{Notify("lan")}}on_success: break
            ---
            # A range could cover the LAN or a proxy, so range alerts are recorded only. The community blocklist still bans ranges.
            name: lucia_range_alert_only
            filters:
              - Alert.GetScope() == "Range"
            {{Notify("range")}}on_success: break
            ---
            # Every other address is on the internet: banned for 4 hours the first time, a day the second, then a week, counting
            # the address's decisions from the last 30 days (config.yaml.local keeps alerts and their decisions that long).
            name: lucia_public_escalating_ban
            filters:
              - Alert.Remediation == true && Alert.GetScope() == "Ip"
            decisions:
              - type: ban
                duration: 4h
            duration_expr: >-
              GetDecisionsSinceCount(Alert.GetValue(), '720h') < 1 ? '4h' :
              (GetDecisionsSinceCount(Alert.GetValue(), '720h') < 2 ? '24h' : '168h')
            {{Notify("public")}}on_success: break
            {{record}}
            """;
    }

    /// <summary>
    /// The alert notifications: one per class, each the http plugin posting OTLP/JSON log records to Observability's alert
    /// intake over TLS that trusts only Lucia's root. The start script fills in the intake's address and the collector's
    /// credentials. Alerts wait up to five seconds to go together; an intake that's down costs one retry, and the engine
    /// carries on without it.
    /// </summary>
    internal static string Notifications => string.Join("---\n", AlertClasses.Select(type => $"""
        type: http
        name: lucia_alerts_{type}
        log_level: info
        group_wait: 5s
        group_threshold: 50
        max_retry: 1
        timeout: 10s
        format: |
        {string.Join("\n", AlertTemplate.Replace("LUCIA_CLASS", type).ReplaceLineEndings("\n").Split('\n').Select(line => line.Length == 0 ? line : "  " + line))}
        url: ""
        method: POST
        # The alert intake's certificate comes from Lucia's authority; the plugin trusts only that root for it.
        ca_cert_path: /tmp/lucia-ca/ca.crt
        headers:
          Content-Type: application/json
          Authorization: ""

        """.ReplaceLineEndings("\n")));

    /// <summary>
    /// One OTLP log record per alert, as Loki stores it: service.name crowdsec and the alert's kind (crowdsec or waf) on the
    /// resource, which become Loki's only labels, and everything else as attributes, which Loki keeps as structured metadata.
    /// The path, method, user agent and host come from the alert's first event, else from its context.
    /// </summary>
    internal const string AlertTemplate = """
        {"resourceLogs":[{{ range $i, $a := . }}{{ if $i }},{{ end }}
        {{- $path := "" }}{{ $method := "" }}{{ $ua := "" }}{{ $host := "" }}{{ $rule := "" }}{{ $status := "" }}
        {{- range $a.Events }}{{ range .Meta }}
        {{- if and (eq $path "") (or (eq .Key "http_path") (eq .Key "uri")) }}{{ $path = .Value }}{{ end }}
        {{- if and (eq $method "") (eq .Key "http_verb") }}{{ $method = .Value }}{{ end }}
        {{- if and (eq $ua "") (eq .Key "http_user_agent") }}{{ $ua = .Value }}{{ end }}
        {{- if and (eq $host "") (eq .Key "target_fqdn") }}{{ $host = .Value }}{{ end }}
        {{- if and (eq $rule "") (eq .Key "rule_name") }}{{ $rule = .Value }}{{ end }}
        {{- if and (eq $status "") (eq .Key "http_status") }}{{ $status = .Value }}{{ end }}
        {{- end }}{{ end }}
        {{- range $a.Meta }}{{ $list := fromJson .Value }}{{ if and (kindIs "slice" $list) $list }}{{ $first := first $list | toString }}
        {{- if and (eq $path "") (eq .Key "target_uri") }}{{ $path = $first }}{{ end }}
        {{- if and (eq $method "") (eq .Key "method") }}{{ $method = $first }}{{ end }}
        {{- if and (eq $ua "") (eq .Key "user_agent") }}{{ $ua = $first }}{{ end }}
        {{- if and (eq $status "") (eq .Key "status") }}{{ $status = $first }}{{ end }}
        {{- end }}{{ end }}
        {{- $scenario := default "" (toJson $a.Scenario | fromJson) }}{{ $kind := default "crowdsec" (toJson $a.Kind | fromJson) }}
        {{- if and (eq $rule "") (eq $kind "waf") }}{{ $rule = $scenario }}{{ end }}
        {{- $scope := "" }}{{ $value := "" }}{{ $ip := "" }}{{ $cn := "" }}{{ $asName := "" }}{{ $asNumber := "" }}
        {{- with $a.Source }}{{ $scope = default "" (toJson .Scope | fromJson) }}{{ $value = default "" (toJson .Value | fromJson) }}
        {{- $ip = .IP }}{{ $cn = .Cn }}{{ $asName = .AsName }}{{ $asNumber = .AsNumber }}{{ end }}
        {{- if eq $ip "" }}{{ $ip = $value }}{{ end }}
        {{- $decision := "none" }}{{ $duration := "" }}{{ $origin := "" }}
        {{- range $a.Decisions }}{{ if eq $decision "none" }}{{ $decision = default "ban" (toJson .Type | fromJson) }}
        {{- $duration = default "" (toJson .Duration | fromJson) }}{{ $origin = default "" (toJson .Origin | fromJson) }}{{ end }}{{ end }}
        {{- $stop := default "" (toJson $a.StopAt | fromJson) }}{{ $time := "0" }}
        {{- if $stop }}{{ $time = (toDate "2006-01-02T15:04:05Z07:00" $stop).UnixNano | toString }}{{ end }}
        {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"crowdsec"}},{"key":"crowdsec.kind","value":{"stringValue":{{ toJson $kind }}}}]},
        "scopeLogs":[{"scope":{"name":"lucia.crowdsec"},"logRecords":[{"timeUnixNano":{{ toJson $time }},"severityNumber":13,"severityText":"WARN",
        "body":{"stringValue":{{ printf "%s from %s (%s): %s %s" $scenario $value "LUCIA_CLASS" $decision $duration | trim | toJson }}},
        "attributes":[{"key":"source.address","value":{"stringValue":{{ toJson $ip }}}},
        {"key":"crowdsec.scope","value":{"stringValue":{{ toJson $scope }}}},
        {"key":"crowdsec.value","value":{"stringValue":{{ toJson $value }}}},
        {"key":"crowdsec.scenario","value":{"stringValue":{{ toJson $scenario }}}},
        {"key":"crowdsec.rule","value":{"stringValue":{{ toJson $rule }}}},
        {"key":"crowdsec.source_class","value":{"stringValue":"LUCIA_CLASS"}},
        {"key":"crowdsec.decision","value":{"stringValue":{{ toJson $decision }}}},
        {"key":"crowdsec.duration","value":{"stringValue":{{ toJson $duration }}}},
        {"key":"crowdsec.origin","value":{"stringValue":{{ toJson $origin }}}},
        {"key":"crowdsec.events","value":{"intValue":{{ toJson (default 0 (toJson $a.EventsCount | fromJson)) }}}},
        {"key":"crowdsec.alert_uuid","value":{"stringValue":{{ toJson $a.UUID }}}},
        {"key":"url.path","value":{"stringValue":{{ toJson $path }}}},
        {"key":"http.request.method","value":{"stringValue":{{ toJson $method }}}},
        {"key":"http.response.status_code","value":{"stringValue":{{ toJson $status }}}},
        {"key":"user_agent.original","value":{"stringValue":{{ toJson $ua }}}},
        {"key":"server.address","value":{"stringValue":{{ toJson $host }}}},
        {"key":"source.geo.country_iso_code","value":{"stringValue":{{ toJson $cn }}}},
        {"key":"source.as.organization.name","value":{"stringValue":{{ toJson $asName }}}},
        {"key":"source.as.number","value":{"stringValue":{{ toJson $asNumber }}}}]}]}]}
        {{- end }}]}
        """;
}
