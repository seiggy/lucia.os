using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

internal static class StackChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        void Rejects(Action action, string message)
        {
            try { action(); }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, message); return; }
            throw new InvalidOperationException(message);
        }

        foreach (var name in new[] { "plex", "media-automation", "a", "a1" }) StackStore.ValidateName(name);
        check(true, "Valid stack names were rejected.");
        foreach (var name in new[] { "", "Plex", "1plex", "plex-", "plex_x", "../etc", new string('a', 41), "catalog", "install", "new" })
            Rejects(() => StackStore.ValidateName(name), $"The stack name '{name}' was accepted.");

        check(StackStore.Normalize("services:\r\n  web:\r\n\timage: x\r\n", 1024, "compose", true) == "services:\n  web:\n\timage: x\n",
            "Compose line endings weren't normalised.");
        Rejects(() => StackStore.Normalize("services:\u0000", 1024, "compose", true), "Control characters were accepted.");
        Rejects(() => StackStore.Normalize(new string('x', 2048), 1024, "compose", true), "An oversized compose file was accepted.");
        Rejects(() => StackStore.Normalize("  \n", 1024, "compose", true), "An empty compose file was accepted.");
        check(StackStore.Normalize(null, 1024, "environment", false) == "", "A missing environment file was rejected.");

        StackStore.ValidateEnv("# comment\nTZ=America/Chicago\n\nPUID=1000\nEMPTY=\n");
        check(true, "A valid environment file was rejected.");
        Rejects(() => StackStore.ValidateEnv("TZ America/Chicago"), "An environment line without '=' was accepted.");
        Rejects(() => StackStore.ValidateEnv("1TZ=x"), "An environment name starting with a digit was accepted.");

        var id = new string('a', 64);
        var report = new NodeStackReport(
            [new("plex", "Running", 3, null, [new("plex", "running", "healthy", "plexinc/pms-docker", null)])],
            [new(id, "lucia-plex-plex-1", "plexinc/pms-docker", "running", "Up 2 hours (healthy)", "lucia-plex", "plex", "32400/tcp")],
            [new("tcp", "0.0.0.0", 32400), new("udp", "::", 1900)]);
        StackStore.ValidateReport(report);
        check(true, "A valid stack report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Stacks = [report.Stacks[0] with { State = "Exploded" }] }),
            "An unknown stack state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Containers = [report.Containers[0] with { Id = "not-an-id" }] }),
            "A malformed container id was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Listeners = [new("sctp", "0.0.0.0", 1)] }),
            "An unknown listener protocol was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Listeners = [new("tcp", "0.0.0.0", 70000)] }),
            "An out-of-range port was accepted.");

        var snapshot = new string('c', 64);
        StackStore.ValidateReport(report with
        {
            Stacks = [report.Stacks[0] with { Backup = new(Guid.NewGuid(), "Succeeded", DateTimeOffset.UtcNow, Snapshot: snapshot, Added: 10, Total: 20) }],
            Snapshots = [new(snapshot, "plex", "lucialab01", DateTimeOffset.UtcNow, 20)],
        });
        check(true, "A valid backup report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Stacks = [report.Stacks[0] with { Backup = new(Guid.NewGuid(), "Exploded", DateTimeOffset.UtcNow) }] }),
            "An unknown backup state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Snapshots = [new("abc", "plex", "lucialab01", DateTimeOffset.UtcNow)] }),
            "A malformed snapshot id was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Snapshots = [new(snapshot, "../etc", "lucialab01", DateTimeOffset.UtcNow)] }),
            "A snapshot of an invalid stack name was accepted.");
        Rejects(() => StackStore.ValidBackup(new(true, "pause")), "An unknown backup mode was accepted.");

        check(StackStore.ValidAddress(" 192.168.1.230 ") == "192.168.1.230" && StackStore.ValidAddress("") is null,
            "A valid app address was rejected.");
        foreach (var bad in new[] { "8.8.8.8", "192.168.1.0", "192.168.1.255", "192.168.01.5", "192.168.1", "::1", "fd00::53", "10.0.0.5/24" })
            Rejects(() => StackStore.ValidAddress(bad), $"The app address '{bad}' was accepted.");
        Rejects(() => StackStore.ValidateEnv("LUCIA_ADDRESS=192.168.1.9"), "An environment that sets Lucia's address was accepted.");
        var manifest = new StackManifest(1, new(), Address: "192.168.1.230");
        check(StackStore.NodeEnv("TZ=UTC\n", manifest) == "TZ=UTC\nLUCIA_ADDRESS=192.168.1.230\n"
            && StackStore.NodeEnv("", manifest) == "LUCIA_ADDRESS=192.168.1.230\n" && StackStore.NodeEnv("TZ=UTC\n", manifest with { Address = null }) == "TZ=UTC\n",
            "The node must get the app's address in its environment.");
        var routedManifest = new StackManifest(1, new(), Routes: [new("grafana", 3030), new("otlp-in", 4318, 4317)]);
        check(StackStore.NodeEnv("TZ=UTC\n", routedManifest, "homelab.example.com")
                == "TZ=UTC\nLUCIA_URL_GRAFANA=https://grafana.homelab.example.com\nLUCIA_URL_OTLP_IN=https://otlp-in.homelab.example.com\n"
            && StackStore.NodeEnv("TZ=UTC\n", routedManifest) == "TZ=UTC\n", "The node must get each web address once a domain is active.");
        var gateway = System.Text.Json.Nodes.JsonNode.Parse(AppGateway.Build(
            [new("obs", new("grafana", 3030), "192.168.1.9"), new("obs", new("otlp", 4318, 4317), "192.168.1.9")], "homelab.example.com")!)!;
        check(gateway["http"]!["routers"]!["app-obs-otlp-grpc"]!["rule"]!.GetValue<string>()
                == "Host(`otlp.homelab.example.com`) && HeaderRegexp(`Content-Type`, `^application/grpc`)"
            && gateway["http"]!["services"]!["app-obs-otlp-grpc"]!["loadBalancer"]!["servers"]![0]!["url"]!.GetValue<string>() == "h2c://192.168.1.9:4317"
            && gateway["http"]!["services"]!["app-obs-grafana"]!["loadBalancer"]!["servers"]![0]!["url"]!.GetValue<string>() == "http://192.168.1.9:3030"
            && gateway["http"]!["routers"]!["app-obs-grafana"]!["tls"] is not null && AppGateway.Build([], "homelab.example.com") is null,
            "The gateway must send gRPC to the app's gRPC port and everything else to its web port.");
        ActiveRoute[] publicRoutes = [new("immich", new("photos", 2283, null, "images"), "192.168.0.241"), new("obs", new("grafana", 3030), "192.168.1.9")];
        var open = System.Text.Json.Nodes.JsonNode.Parse(AppGateway.Build(publicRoutes, "homelab.example.com", "example.com")!)!;
        check(open["http"]!["routers"]!["app-immich-photos-public"]!["rule"]!.GetValue<string>() == "Host(`images.example.com`)"
            && open["http"]!["routers"]!["app-immich-photos-public"]!["service"]!.GetValue<string>() == "app-immich-photos"
            && open["http"]!["routers"]!["app-immich-photos-public"]!["entryPoints"]!.AsArray().Count == 2
            && open["http"]!["routers"]!["app-obs-grafana-public"] is null
            && open["http"]!["routers"]!["app-obs-grafana"]!["entryPoints"]!.AsArray().Count == 1
            && System.Text.Json.Nodes.JsonNode.Parse(AppGateway.Build(publicRoutes, "homelab.example.com")!)!["http"]!["routers"]!["app-immich-photos-public"] is null,
            "Only routes with a public name, and only while public access is on, may answer on the public entrypoint.");
        var publicNaming = Lucia.Homelab.Server.Domains.DomainNames.WithPublic(
            Lucia.Homelab.Server.Domains.DomainNames.Plan(new("example.com", "homelab", "atlas"), "example.com"), true);
        var publicDns = Lucia.Homelab.Server.Nodes.ManagedNodeDns.Wanted(publicNaming, [], "192.168.0.222", publicRoutes, "example.com");
        check(publicDns.Any(record => record.Domain == "images.example.com" && record.Answer == "192.168.0.222") && publicDns.Length == 3
            && Lucia.Homelab.Server.Nodes.ManagedNodeDns.Wanted(publicNaming, [], "192.168.0.222", publicRoutes).Length == 2,
            "Public names must resolve to the gateway on the LAN while public access is on.");
        foreach (var label in new[] { "", "Grafana!", "-x", "a.b", new string('a', 64) })
            check(!StackStore.RouteHostPattern().IsMatch(label), $"The web address name '{label}' was accepted.");
        var observability = StackCatalog.Find("observability");
        var obs = observability.Render(StackCatalog.Settings(observability, null), new(Guid.NewGuid(), "lucialab01", true, null),
            new Dictionary<string, string> { ["OTLP_PASSWORD"] = new string('p', 43) });
        check(obs.Env.Contains($"OTLP_PASSWORD={new string('p', 43)}\n", StringComparison.Ordinal) && obs.Env.Contains("GRAFANA_ADMIN_PASSWORD=", StringComparison.Ordinal)
            && obs.Compose.Contains("$${env:OTLP_USERNAME}:$${env:OTLP_PASSWORD}", StringComparison.Ordinal)
            && obs.Compose.Contains("url: '$$$${__value.raw}'", StringComparison.Ordinal)
            && obs.Compose.Contains("GF_SERVER_ROOT_URL: ${LUCIA_URL_GRAFANA:-http://lucialab01:3030}\n", StringComparison.Ordinal)
            && obs.Compose.Split('\n').Count(line => System.Text.RegularExpressions.Regex.IsMatch(line, "^      lucia\\.configs: \"[0-9a-f]{16}\"$")) == 5
            && obs.Routes is [{ Host: "grafana", Port: 3030, GrpcPort: null }, { Host: "otlp", Port: 4318, GrpcPort: 4317 }],
            "Observability must keep its secrets, escape its configs for compose, recreate services whose configs change and route Grafana and OTLP.");
        foreach (var dashboard in new[] { "hardware", "inference" })
        {
            var dashboardLine = obs.Compose.Split('\n').Single(line => line.TrimStart().StartsWith($"{{\"uid\":\"lucia-{dashboard}\"", StringComparison.Ordinal));
            System.Text.Json.JsonDocument.Parse(dashboardLine.Replace("$$", "$", StringComparison.Ordinal)).Dispose();
            check(!System.Text.RegularExpressions.Regex.IsMatch(dashboardLine.Replace("$$", "", StringComparison.Ordinal), @"\$")
                && dashboardLine.Contains("$$__rate_interval", StringComparison.Ordinal)
                && obs.Compose.Contains($"target: /etc/grafana/dashboards/lucia/{dashboard}.json\n", StringComparison.Ordinal),
                $"The {dashboard} dashboard must be valid JSON with every Grafana variable escaped for compose.");
        }
        check(observability.Sso(StackCatalog.Settings(observability, null)) == new AppSso("Grafana", "grafana", "/login/generic_oauth")
            && obs.Env.Contains($"{StackStore.SsoSecret}=", StringComparison.Ordinal)
            && obs.Compose.Contains("GF_AUTH_GENERIC_OAUTH_ENABLED: ${LUCIA_SSO_ENABLED:-false}\n", StringComparison.Ordinal)
            && obs.Compose.Contains("GF_AUTH_GENERIC_OAUTH_CLIENT_SECRET: ${SSO_CLIENT_SECRET}\n", StringComparison.Ordinal)
            && obs.Compose.Contains("GF_AUTH_SIGNOUT_REDIRECT_URL: ${LUCIA_SSO_SIGNOUT:-}\n", StringComparison.Ordinal)
            && StackCatalog.Find("local-ai").Sso(new Dictionary<string, string>()) is null,
            "Grafana must sign in through Lucia only once Lucia sets its client, with a generated secret.");
        var musicBrainz = StackCatalog.Find("musicbrainz");
        var mbSettings = StackCatalog.Settings(musicBrainz, new() { ["metabrainz-access-token"] = " tok_EN.123 " });
        var mbEnv = StackCatalog.KeepSecrets(musicBrainz, mbSettings, new() { ["POSTGRES_PASSWORD"] = new string('p', 43) });
        var mb = musicBrainz.Render(mbSettings, new(Guid.NewGuid(), "lucialab01", true, null), mbEnv);
        var kept = StackCatalog.KeepSecrets(musicBrainz, StackCatalog.Settings(musicBrainz, null), new(mbEnv));
        var mbPlain = musicBrainz.Render(StackCatalog.Settings(musicBrainz, null),         new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(mbSettings["metabrainz-access-token"] == "" && kept["METABRAINZ_ACCESS_TOKEN"] == "tok_EN.123"
            && mb.Env == $"POSTGRES_PASSWORD={new string('p', 43)}\nMETABRAINZ_ACCESS_TOKEN=tok_EN.123\n"
            && mb.Compose.Contains("      - metabrainz_access_token\n", StringComparison.Ordinal)
            && mb.Compose.Contains("    environment: METABRAINZ_ACCESS_TOKEN\n", StringComparison.Ordinal)
            && mb.Compose.Contains("      0 * * * * /usr/local/bin/replication.sh\n", StringComparison.Ordinal)
            && mb.Compose.Contains("      password = $${POSTGRES_PASSWORD}\n", StringComparison.Ordinal)
            && mb.Compose.Contains("      LUCIA_URL: ${LUCIA_URL_MUSICBRAINZ:-}\n", StringComparison.Ordinal)
            && !mbPlain.Compose.Contains("secrets:", StringComparison.Ordinal) && !mbPlain.Compose.Contains("replication.sh", StringComparison.Ordinal)
            && !mbPlain.Env.Contains("METABRAINZ", StringComparison.Ordinal)
            // Nothing waits on the import, so it must idle rather than exit or the app would read as needing attention.
            && !mb.Compose.Contains("on-failure", StringComparison.Ordinal) && mb.Compose.Contains("then touch .ready; exec sleep infinity;", StringComparison.Ordinal)
            && mb.Routes is [{ Host: "musicbrainz", Port: 5000, GrpcPort: null }] && musicBrainz.BackupMode == "stop",
            "MusicBrainz must keep its token out of the manifest, replicate hourly only with a token and route its website.");
        Rejects(() => StackCatalog.Settings(musicBrainz, new() { ["metabrainz-access-token"] = "a$b" }), "A secret that compose would interpolate was accepted.");
        var immich = StackCatalog.Find("immich");
        var im = immich.Render(StackCatalog.Settings(immich, new() { ["library"] = "/mnt/lucia/nas/unas/Media/Photos/library/", ["machine-learning"] = "cuda" }),
            new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string> { ["DB_PASSWORD"] = new string('a', 48) });
        var imLocal = immich.Render(StackCatalog.Settings(immich, null), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(im.Compose.Contains("      - /mnt/lucia/nas/unas/Media/Photos/library:/data\n", StringComparison.Ordinal)
            && im.Compose.Contains("-cuda@sha256:", StringComparison.Ordinal) && im.Compose.Contains("driver: nvidia", StringComparison.Ordinal)
            && im.Require.SequenceEqual(["nas=unas/Media", "gpu.vendor=nvidia"]) && im.Env.StartsWith($"DB_PASSWORD={new string('a', 48)}\n{StackStore.SsoSecret}=", StringComparison.Ordinal)
            && imLocal.Compose.Contains("      - library:/data\n", StringComparison.Ordinal) && imLocal.Compose.Contains("\n  library:\n", StringComparison.Ordinal)
            && !imLocal.Compose.Contains("nvidia", StringComparison.Ordinal) && imLocal.Require.Length == 0
            && StackCatalog.ReadEnv(imLocal.Env)["DB_PASSWORD"].All(char.IsAsciiLetterOrDigit)
            && imLocal.Routes is [{ Host: "photos", Port: 2283, GrpcPort: null }] && immich.BackupMode == "stop",
            "Immich must keep photos on the chosen NAS folder or the server, use the GPU only when asked and route its website.");
        check(immich.Sso(StackCatalog.Settings(immich, null)) == new AppSso("Immich", "photos", "/auth/login", "/user-settings", "/api/oauth/mobile-redirect")
            && imLocal.Compose.Contains("      APP_URL: ${LUCIA_URL_PHOTOS:-}\n", StringComparison.Ordinal)
            && imLocal.Compose.Contains("      CLIENT_SECRET: ${SSO_CLIENT_SECRET}\n", StringComparison.Ordinal)
            && imLocal.Compose.Contains("\n        SQL\n", StringComparison.Ordinal) && imLocal.Compose.Contains("if [ \"$$ENABLED\" = true ]", StringComparison.Ordinal),
            "Immich must sign in through Lucia only once Lucia sets its client, and send the phone app through its web address.");
        foreach (var path in new[] { "/srv/photos", "/mnt/lucia/nas/unas", "/mnt/lucia/nas/unas/Media/../etc", "/mnt/lucia/nas/unas/Media/a b" })
            Rejects(() => immich.Render(StackCatalog.Settings(immich, new() { ["library"] = path }), new(Guid.NewGuid(), "lucialab01", true, null),
                new Dictionary<string, string>()), $"Immich accepted {path} as its photo library.");
        check(StackStore.LuciaLines("A=1\nLUCIA_SSO_ENABLED=true\nLUCIA_OTLP_ENDPOINT=https://otlp.example\nLUCIA_SSO_ORIGIN=https://auth.example\nB=2\n")
                == "LUCIA_SSO_ENABLED=true\nLUCIA_OTLP_ENDPOINT=https://otlp.example\nLUCIA_SSO_ORIGIN=https://auth.example\n" && StackStore.LuciaLines("A=1\n") == "",
            "A catalog app's sign-in and telemetry variables must survive re-rendering.");
        var liteLlm = StackCatalog.Find("litellm");
        var ll = liteLlm.Render(StackCatalog.Settings(liteLlm, null), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        var llEnv = StackCatalog.ReadEnv(ll.Env);
        var llAgain = liteLlm.Render(StackCatalog.Settings(liteLlm, null), new(Guid.NewGuid(), "lucialab01", true, null), llEnv);
        check(llAgain.Env == ll.Env && llEnv["LITELLM_MASTER_KEY"].StartsWith("sk-", StringComparison.Ordinal) && llEnv["LITELLM_SALT_KEY"].Length >= 32
            && llEnv.ContainsKey(StackStore.SsoSecret) && liteLlm.Telemetry && !immich.Telemetry && liteLlm.BackupMode == "stop"
            && liteLlm.Sso(StackCatalog.Settings(liteLlm, null)) == new AppSso("LiteLLM", "litellm", "/sso/callback")
            && ll.Compose.Contains("      PROXY_BASE_URL: ${LUCIA_URL_LITELLM:-http://lucialab01:4000}\n", StringComparison.Ordinal)
            && ll.Compose.Contains("'[ \"$$SSO\" = true ] || unset GENERIC_CLIENT_ID GENERIC_CLIENT_SECRET;", StringComparison.Ordinal)
            && ll.Compose.Contains("      OTEL_HEADERS: Authorization=${LUCIA_OTLP_AUTHORIZATION:-}\n", StringComparison.Ordinal)
            && ll.Compose.Contains("        callbacks: [${LUCIA_OTLP_ENDPOINT:+otel}]\n", StringComparison.Ordinal)
            && ll.Compose.Contains("        disable_env_credential_login: ${LUCIA_SSO_ENABLED:-false}\n", StringComparison.Ordinal)
            && ll.Routes is [{ Host: "litellm", Port: 4000, GrpcPort: null }],
            "LiteLLM must keep its keys, sign in through Lucia only once its client exists and export telemetry only with an Observability app.");
        var plex = StackCatalog.Find("plex");
        var plexSettings = StackCatalog.Settings(plex, new() { ["media"] = "/mnt/lucia/nas/truenas/media/", ["media-2"] = "/mnt/lucia/nas/unas/Media", ["plex-claim"] = "claim-abc" });
        var plexEnv = StackCatalog.KeepSecrets(plex, plexSettings, []);
        var px = plex.Render(plexSettings, new(Guid.NewGuid(), "lucialab01", true, null), plexEnv);
        var pxGpu = plex.Render(StackCatalog.Settings(plex, new() { ["transcoding"] = "nvidia" }), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(plexSettings["plex-claim"] == "" && px.Env == "PLEX_CLAIM=claim-abc\n" && plex.BackupMode == "stop" && plex.BackupExclude.Length == 0
            && px.Compose.Contains("      - /mnt/lucia/nas/truenas/media:/data\n      - /mnt/lucia/nas/unas/Media:/media\nvolumes:\n", StringComparison.Ordinal)
            && px.Compose.Contains("    network_mode: host\n", StringComparison.Ordinal) && !px.Compose.Contains("nvidia", StringComparison.Ordinal)
            && px.Require.SequenceEqual(["nas=truenas/media", "nas=unas/Media"]) && px.Routes is [{ Host: "plex", Port: 32400 }]
            && pxGpu.Require is ["gpu.vendor=nvidia"] && pxGpu.Env == "" && pxGpu.Compose.Contains("      NVIDIA_DRIVER_CAPABILITIES: compute,video,utility\n    volumes:\n", StringComparison.Ordinal)
            && pxGpu.Compose.Contains("      - /etc/localtime:/etc/localtime:ro\n    deploy:\n", StringComparison.Ordinal),
            "Plex must mount its media folders at the same paths, keep its claim token out of the manifest and reserve a GPU only for NVIDIA transcoding.");
        Rejects(() => plex.Render(StackCatalog.Settings(plex, new() { ["media"] = "/srv/media" }), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()),
            "Plex must refuse a media folder outside a NAS share.");
        var sonarr = StackCatalog.Find("sonarr");
        var so = sonarr.Render(StackCatalog.Settings(sonarr, new() { ["media"] = "/mnt/lucia/nas/unas/Media/", ["also-at"] = "/qbitvpn, /media2/", ["downloads"] = "/mnt/lucia/nas/unas/Media/downloads" }),
            new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(so.Env == "" && sonarr.BackupMode == "stop" && so.Require is ["nas=unas/Media"] && so.Routes is [{ Host: "sonarr", Port: 8989 }]
            && so.Compose.Contains("""
                      - "/mnt/lucia/nas/unas/Media:/data"
                      - "/mnt/lucia/nas/unas/Media:/qbitvpn"
                      - "/mnt/lucia/nas/unas/Media:/media2"
                      - "/mnt/lucia/nas/unas/Media/downloads:/downloads"
                    ports:
                      - "8989:8989"
                    depends_on:
                      init:
                        condition: service_completed_successfully
                volumes:
                  config:

                """.ReplaceLineEndings("\n"), StringComparison.Ordinal)
            && so.Compose.Contains("    user: \"1000:1000\"\n    environment:\n      SONARR__SERVER__PORT: \"8989\"\n", StringComparison.Ordinal)
            && so.Compose.Contains("chown -R 1000:1000 /config", StringComparison.Ordinal),
            "Sonarr must run as its owner with its config folder given to it, and show the media folder at every old path.");
        var lidarr = StackCatalog.Find("lidarr").Render(StackCatalog.Settings(StackCatalog.Find("lidarr"), []), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        var flare = StackCatalog.Find("flaresolverr");
        var fs = flare.Render(StackCatalog.Settings(flare, []), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>());
        check(!lidarr.Compose.Contains("init:", StringComparison.Ordinal) && lidarr.Compose.Contains("      PUID: \"1000\"\n", StringComparison.Ordinal) && lidarr.Require.Length == 0
            && flare.Fields.All(field => field.Id == "port") && fs.Routes is [] && flare.BackupMode == "live" && !fs.Compose.Contains("volumes:\n  config:", StringComparison.Ordinal)
            && StackCatalog.Find("seerr").Render(StackCatalog.Settings(StackCatalog.Find("seerr"), []), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>())
                .Compose.Contains("      - config:/app/config\n", StringComparison.Ordinal),
            "PUID images must take the owner by environment, and FlareSolverr must keep nothing and have no web address.");
        foreach (var bad in new[] { "/config", "/data/x", "relative", "/etc/passwd" })
            Rejects(() => sonarr.Render(StackCatalog.Settings(sonarr, new() { ["media"] = "/mnt/lucia/nas/unas/Media", ["also-at"] = bad }),
                new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()), $"Sonarr must refuse the media path {bad}.");
        Rejects(() => sonarr.Render(StackCatalog.Settings(sonarr, new() { ["also-at"] = "/tv" }), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()),
            "Other media paths must need a media folder.");
        var downloads = StackCatalog.Find("download-client");
        var dlSettings = StackCatalog.Settings(downloads, new() { ["client"] = "soulseek", ["web-host"] = "soulseek", ["port"] = "6080",
            ["downloads"] = "/mnt/lucia/nas/unas/Media/soulseek/downloads", ["media"] = "/mnt/lucia/nas/unas/Media/soulseek/shared",
            ["vpn-user"] = "p123", ["vpn-password"] = "pw-1", ["vpn-regions"] = "Netherlands,US East", ["port-forwarding"] = "on", ["local-networks"] = "192.168.0.0/23, 192.168.4.0/23" });
        var dl = downloads.Render(dlSettings, new(Guid.NewGuid(), "lucialab01", true, null), StackCatalog.KeepSecrets(downloads, dlSettings, []));
        check(dlSettings["vpn-password"] == "" && dl.Env == "VPN_USER=p123\nVPN_PASSWORD=pw-1\n" && dl.Require is ["nas=unas/Media"] && dl.Routes is [{ Host: "soulseek", Port: 6080 }]
            && dl.Compose.Contains("""
                      VPN_PORT_FORWARDING: "on"
                      PORT_FORWARD_ONLY: "true"
                      SERVER_REGIONS: "Netherlands,US East"
                      FIREWALL_OUTBOUND_SUBNETS: "192.168.0.0/23,192.168.4.0/23"
                    volumes:
                      - vpn:/gluetun
                    ports:
                      - "6080:6080"
                  soulseek:
                """.ReplaceLineEndings("\n"), StringComparison.Ordinal)
            && dl.Compose.Contains("""
                    network_mode: service:vpn
                    environment:
                      PUID: "1000"
                      PGID: "1000"
                    volumes:
                      - config:/data
                      - /etc/localtime:/etc/localtime:ro
                      - "/mnt/lucia/nas/unas/Media/soulseek/downloads:/data/Soulseek Downloads"
                      - "/mnt/lucia/nas/unas/Media/soulseek/shared:/data/Soulseek Shared Folder"
                    depends_on:
                """.ReplaceLineEndings("\n"), StringComparison.Ordinal)
            && !dl.Compose.Contains("pw-1", StringComparison.Ordinal),
            "The download client must route through gluetun, keep the VPN account in its environment and mount Soulseek's folders in its home.");
        Rejects(() => downloads.Render(StackCatalog.Settings(downloads, []), new(Guid.NewGuid(), "lucialab01", true, null), new Dictionary<string, string>()),
            "The download client must need a VPN account.");
        Rejects(() => downloads.Render(StackCatalog.Settings(downloads, new() { ["vpn-provider"] = "pia\" x" }), new(Guid.NewGuid(), "lucialab01", true, null),
            new Dictionary<string, string> { ["VPN_USER"] = "u", ["VPN_PASSWORD"] = "p" }), "The download client must refuse a provider that could break its compose.");
        var home = new Lucia.Homelab.Server.Nodes.ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, null);
        var none = new Dictionary<string, string>();
        var ha = StackCatalog.Find("home-assistant");
        var haOut = ha.Render(StackCatalog.Settings(ha, null), home, none);
        check(ha.BackupMode == "stop" && haOut.Env == "" && haOut.Routes is [{ Host: "homeassistant", Port: 8123 }]
            && haOut.Compose.Contains("    network_mode: host\n", StringComparison.Ordinal)
            && haOut.Compose.Contains("\"[ -e /config/configuration.yaml ] || {", StringComparison.Ordinal)
            && haOut.Compose.Contains("        use_x_forwarded_for: true\n", StringComparison.Ordinal),
            "Home Assistant must use the host's network, leave an existing configuration alone and trust Lucia's gateway in a new one.");
        var mosquitto = StackCatalog.Find("mosquitto");
        var mqSettings = StackCatalog.Settings(mosquitto, new() { ["mqtt-password"] = "pw-1" });
        var mq = mosquitto.Render(mqSettings, home, StackCatalog.KeepSecrets(mosquitto, mqSettings, []));
        check(mqSettings["mqtt-password"] == "" && mq.Env == "MQTT_PASSWORD=pw-1\n" && !mq.Compose.Contains("pw-1", StringComparison.Ordinal)
            && mq.Compose.Contains("      MQTT_USER: \"homeassistant\"\n", StringComparison.Ordinal)
            && mq.Compose.Contains("      allow_anonymous false\n", StringComparison.Ordinal) && mq.Compose.Contains("      - \"1883:1883\"\n", StringComparison.Ordinal),
            "Mosquitto must refuse anonymous clients and keep its login's password in its environment.");
        Rejects(() => mosquitto.Render(StackCatalog.Settings(mosquitto, []), home, none), "Mosquitto must need a password.");
        Rejects(() => mosquitto.Render(StackCatalog.Settings(mosquitto, new() { ["mqtt-user"] = "a\" b" }), home, new Dictionary<string, string> { ["MQTT_PASSWORD"] = "p" }),
            "Mosquitto must refuse a username that could break its compose.");
        var voice = StackCatalog.Find("voice");
        var vo = voice.Render(StackCatalog.Settings(voice, null), home, none);
        check(vo.Routes is null && voice.BackupExclude is ["volumes/whisper", "volumes/piper"]
            && vo.Compose.Contains("    command: [\"--model\", \"auto\", \"--language\", \"en\"]\n", StringComparison.Ordinal)
            && vo.Compose.Contains("    command: [\"--voice\", \"en_US-lessac-medium\"]\n", StringComparison.Ordinal),
            "Voice must pass its model, language and voice to the Wyoming servers and skip the models in backups.");
        Rejects(() => voice.Render(StackCatalog.Settings(voice, new() { ["language"] = "en\", \"--x" }), home, none), "Voice must refuse a language that could break its compose.");
        var runner = StackCatalog.Find("github-runner");
        var runnerSettings = StackCatalog.Settings(runner, new()
        {
            ["repositories"] = "https://github.com/seiggy/lucia.os/, seiggy/lucia-dotnet, seiggy, other/lucia.os", ["access-token"] = "github_pat_" + new string('a', 40), ["labels"] = "arm, LuciaLab01",
        });
        var gr = runner.Render(runnerSettings, home, StackCatalog.KeepSecrets(runner, runnerSettings, []));
        check(runner.RunsOnSpark && gr.Env == $"ACCESS_TOKEN=github_pat_{new string('a', 40)}\n" && !gr.Compose.Contains("github_pat_", StringComparison.Ordinal)
            && gr.Compose.Contains("  runner-lucia-os:\n", StringComparison.Ordinal) && gr.Compose.Contains("  runner-lucia-os-2:\n", StringComparison.Ordinal)
            && gr.Compose.Contains("      REPO_URL: \"https://github.com/seiggy/lucia.os\"\n", StringComparison.Ordinal)
            && gr.Compose.Contains("      RUNNER_SCOPE: org\n      ORG_NAME: \"seiggy\"\n", StringComparison.Ordinal)
            && gr.Compose.Contains("      LABELS: \"lucialab01,arm\"\n", StringComparison.Ordinal)
            && gr.Compose.Contains("      EPHEMERAL: \"true\"\n", StringComparison.Ordinal)
            && gr.Compose.Contains("    privileged: true\n", StringComparison.Ordinal) && !gr.Compose.Contains("docker.sock:/var/run", StringComparison.Ordinal)
            && !gr.Compose.Contains("2375", StringComparison.Ordinal) && runner.BackupExclude.Contains("volumes/docker"),
            "The GitHub runner must run one ephemeral runner per repository on its own Docker, with the token only in its environment.");
        Rejects(() => runner.Render(StackCatalog.Settings(runner, new() { ["repositories"] = "seiggy/lucia.os" }), home, none), "The GitHub runner must need a token.");
        foreach (var bad in new[] { "", "a/b/c", "seiggy/lucia.os.git", "seiggy/\"x", string.Join(',', Enumerable.Range(1, 9).Select(n => $"o/r{n}")) })
            Rejects(() => GitHubRunnerApp.Repositories(bad), $"The GitHub runner must refuse repositories \"{bad}\".");
        Rejects(() => GitHubRunnerApp.Labels("ok, bad label", "x"), "The GitHub runner must refuse a label that could break its compose.");
        var esphome = StackCatalog.Find("esphome").Render(StackCatalog.Settings(StackCatalog.Find("esphome"), null), home, none);
        var matter = StackCatalog.Find("matter-server");
        var ms = matter.Render(StackCatalog.Settings(matter, null), home, none);
        var nodeRed = StackCatalog.Find("node-red").Render(StackCatalog.Settings(StackCatalog.Find("node-red"), new() { ["port"] = "1881" }), home, none);
        check(esphome.Routes is [{ Host: "esphome", Port: 6052 }] && esphome.Compose.Contains("    network_mode: host\n", StringComparison.Ordinal)
            && !esphome.Compose.Contains("ports:", StringComparison.Ordinal) && !esphome.Compose.Contains("init:", StringComparison.Ordinal)
            && matter.Fields.Length == 0 && ms.Routes is [] && ms.Compose.Contains("chown -R 1000:1000 /data", StringComparison.Ordinal)
            && nodeRed.Routes is [{ Host: "node-red", Port: 1881 }] && nodeRed.Compose.Contains("""
                    user: "1000:1000"
                    volumes:
                      - data:/data
                      - /etc/localtime:/etc/localtime:ro
                    ports:
                      - "1881:1880"
                    depends_on:
                """.ReplaceLineEndings("\n"), StringComparison.Ordinal),
            "Home Assistant's companions must use the host's network only where they find devices, and give their data to the user they run as.");
        Rejects(() => StackStore.ValidateName(StackStore.RelayName), "An app could take the telemetry relay's name.");
        var relayConfig = Lucia.Homelab.Server.Telemetry.TelemetryRelay.Config("\"lucialab01\"", true,
            [new("vllm", "127.0.0.1:8000"), new("llama-cpp", "127.0.0.1:8080", ["org/model:Q4_K_M"])],
            Lucia.Homelab.Server.Telemetry.TelemetryRelay.EnvExporter);
        check(relayConfig.Contains("""
                  - job_name: llama-cpp
                    authorization:
                      credentials: ${env:LOCAL_AI_KEY}
                    params:
                      autoload: ["false"]
                    static_configs:
                      - targets: [127.0.0.1:8080]
                        labels:
                          model: "org/model:Q4_K_M"
                    relabel_configs:
                      - source_labels: [model]
                        target_label: __param_model
                      - target_label: instance
                        replacement: "lucialab01"
            """.ReplaceLineEndings("\n"), StringComparison.Ordinal)
            && relayConfig.Contains("      - job_name: gpu\n", StringComparison.Ordinal),
            "The relay must scrape each llama.cpp model with the Local AI key, without loading it, labelled with the machine.");
        var relayCompose = Lucia.Homelab.Server.Telemetry.TelemetryRelay.Compose(relayConfig, gpu: false);
        check(relayCompose.Contains("credentials: $${env:LOCAL_AI_KEY}", StringComparison.Ordinal)
            && relayCompose.Contains("LOCAL_AI_KEY: ${LOCAL_AI_KEY}\n", StringComparison.Ordinal)
            && !relayCompose.Contains("gpu-exporter", StringComparison.Ordinal)
            && Lucia.Homelab.Server.Telemetry.TelemetryRelay.Compose(relayConfig, gpu: true).Contains("  gpu-exporter:\n", StringComparison.Ordinal),
            "The relay's compose must escape its config and run the GPU exporter only with a GPU.");
        var adguard = StackCatalog.Find("adguard");
        var adguardCompose = adguard.Render(StackCatalog.Settings(adguard, null), null!, new Dictionary<string, string>()).Compose;
        check(adguard.UsesAddress && !adguard.ServerBound && adguardCompose.Contains("      - \"${LUCIA_ADDRESS}:53:53/udp\"\n", StringComparison.Ordinal)
            && adguardCompose.Contains("      - \"${LUCIA_ADDRESS}:853:853/tcp\"\n", StringComparison.Ordinal)
            && !System.Text.RegularExpressions.Regex.IsMatch(adguardCompose, @"- ""\d"), "AdGuard must publish every port on its own address only.");
        StackStore.ValidateReport(report with { Addresses = [new("192.168.1.230", "Held"), new("192.168.1.231", "InUse", "aa:bb:cc:dd:ee:ff")] });
        check(true, "A valid address report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Addresses = [new("192.168.1.230", "Stolen")] }), "An unknown address state was accepted.");
        Rejects(() => StackStore.ValidateReport(report with { Addresses = [new("eth0; rm", "Held")] }), "A malformed reported address was accepted.");
        check(StackStore.ValidBackup(new(false, "stop")) == new StackBackup(false, "stop"), "A valid backup setting was rejected.");

        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-06-01T12:00:00Z"), chicago) == DateTimeOffset.Parse("2025-06-02T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-06-02T07:59:00Z"), chicago) == DateTimeOffset.Parse("2025-06-02T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-06-02T08:00:00Z"), chicago) == DateTimeOffset.Parse("2025-06-03T08:00:00Z"),
            "Backups must run at the next 03:00 in the owner's zone, never at the instant the last one was asked for.");
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-03-08T10:00:00Z"), chicago) == DateTimeOffset.Parse("2025-03-09T08:00:00Z")
            && StackStore.NextRun(DateTimeOffset.Parse("2025-11-01T09:00:00Z"), chicago) == DateTimeOffset.Parse("2025-11-02T09:00:00Z"),
            "Backups must follow 03:00 local time across daylight-saving changes.");
        // A zone whose clocks jump from 03:00 to 04:00 has no 03:00 that day; the backup runs an hour later instead of skipping it.
        var jump = TimeZoneInfo.CreateCustomTimeZone("Jump", TimeSpan.Zero, "Jump", "Jump", "Jump Summer",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 3, 30),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 26))]);
        check(StackStore.NextRun(DateTimeOffset.Parse("2025-03-29T12:00:00Z"), jump) == DateTimeOffset.Parse("2025-03-30T03:00:00Z"),
            "A day without a 03:00 must still get its backup.");

        var requests = new NodeRequests();
        var node = Guid.NewGuid();
        var logs = requests.Logs(node, "lucia-plex-plex-1", 50, CancellationToken.None);
        var pending = await requests.Wait(node, CancellationToken.None);
        check(pending is [{ Kind: "logs", Container: "lucia-plex-plex-1", Tail: 50 }], "The node didn't receive the logs request.");
        requests.Complete(Guid.NewGuid(), new(pending[0].RequestId, true, "spoofed", null));
        requests.Complete(node, new(pending[0].RequestId, true, "line one", null));
        check((await logs).Output == "line one", "Another node answered a request, or the answer was lost.");
        try
        {
            await requests.Logs(node, "bad name; rm -rf /", 50, CancellationToken.None);
            throw new InvalidOperationException("A container name with shell characters was accepted.");
        }
        catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Unsafe container name rejected."); }
        var job = Guid.NewGuid();
        var exec = requests.Exec(node, job, "nvidia-smi\nexit 3", "bash", 60, CancellationToken.None);
        pending = await requests.Wait(node, CancellationToken.None);
        check(pending is [{ Kind: "exec", Command: "nvidia-smi\nexit 3", Interpreter: "bash", Timeout: 60, Job: null }] && pending[0].RequestId == job,
            "The node didn't receive the command under its job id.");
        requests.Complete(node, new(job, true, "No devices were found", null, 3));
        check(await exec is { Output: "No devices were found", ExitCode: 3, Running: false }, "The command's exit code was lost.");
        var stop = requests.Follow(node, job, stop: true, CancellationToken.None);
        pending = await requests.Wait(node, CancellationToken.None);
        check(pending is [{ Kind: "exec-stop", Command: null }] && pending[0].Job == job && pending[0].RequestId != job, "A stop must name its job.");
        requests.Complete(node, new(pending[0].RequestId, true, null, "Stopping it."));
        await stop;
        foreach (var (command, interpreter, seconds) in new[] { ("", "bash", 60), (new string('x', NodeRequests.MaxCommandChars + 1), "bash", 60),
            ("a\0b", "bash", 60), ("ls", "zsh", 60), ("ls", "bash", 0), ("ls", "bash", NodeRequests.MaxCommandSeconds + 1) })
        {
            try
            {
                await requests.Exec(node, Guid.NewGuid(), command, interpreter, seconds, CancellationToken.None);
                throw new InvalidOperationException($"The command ({interpreter}, {seconds} s, {command.Length} chars) was accepted.");
            }
            catch (HardwareOnboardingException error) when (error.StatusCode == 400) { check(true, "Unbounded command rejected."); }
        }

        foreach (var bad in new[] { "gpus", "gpu.vram>=lots", "gpu.vendor", "memory=32G", "gpu.compute>=x", "gpu;rm" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        Rejects(() => StackRequirements.Normalize(Enumerable.Range(0, 17).Select(i => $"memory>={i}G").ToArray()),
            "Too many requirements were accepted.");
        check(StackRequirements.Normalize([" gpu ", "", "gpu"]) is ["gpu"], "Requirements weren't trimmed and de-duplicated.");
        var gib = 1L << 30;
        var host = new Lucia.Homelab.Server.Nodes.NodeHeartbeat(Guid.NewGuid(), "spark", "Debian 13", 1, null, 64 * gib, 32 * gib, null, null,
            new("Ready", "28", "2.30", true, [new("nvidia", "NVIDIA GeForce RTX 4090", (long)(23.6 * gib), "8.9"),
                new("nvidia", "NVIDIA T400", 2 * gib, "7.5")]));
        check(StackRequirements.Unmet(["gpu", "gpu.vendor=nvidia", "gpu.vram>=24G", "gpu.compute>=8.6", "memory>=64G"], host) is null,
            "A node meeting every requirement was ruled out (a 24 GB card reports a little under 24 GiB).");
        check(StackRequirements.Unmet(["gpu.model~T400", "gpu.vram>=8G"], host) is not null,
            "GPU requirements were met by two different cards.");
        check(StackRequirements.Unmet(["memory<=32G"], host) == "memory<=32G", "A memory maximum was ignored.");
        check(StackRequirements.Unmet(["gpu"], host with { Runtime = host.Runtime! with { GpuContainers = false } }) == "gpu",
            "A GPU that containers can't use met a gpu requirement.");

        foreach (var bad in new[] { "nas", "nas=unas", "nas=Unas/Media", "nas=unas/../x", "nas=unas/Media/x", "nas~unas/Media" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        check(StackRequirements.Unmet(["nas=unas/Media"], host, null, new HashSet<string> { "unas/Media" }) is null
            && StackRequirements.Unmet(["nas=unas/Media"], host, null, new HashSet<string> { "unas/Models" }) == "nas=unas/Media"
            && StackRequirements.Unmet(["nas=unas/Media"], host) == "nas=unas/Media", "NAS requirements must follow the node's mounts.");
        (string, string)[] shares = [("unas", "Media"), ("unas", "Models")];
        check(StackStore.NasRequirements("volumes:\n  - /mnt/lucia/nas/unas/Media/Movies:/movies:ro\n  - \"/mnt/lucia/nas/unas/Media:/m\"\n", shares)
            is ["nas=unas/Media"] && StackStore.NasRequirements("services: {}", shares) is [],
            "Compose NAS paths must become requirements.");
        Rejects(() => StackStore.NasRequirements("- /mnt/lucia/nas/unas/Photos:/p", shares), "A compose path to an unknown share was accepted.");
        Rejects(() => StackStore.ValidateNasId("Bad Name"), "An invalid NAS name was accepted.");
        StackStore.ValidateReport(report with { Mounts = [new("unas", "Media", "Mounted"), new("unas", "Models", "Failed", "access denied")] });
        check(true, "A valid mount report was rejected.");
        Rejects(() => StackStore.ValidateReport(report with { Mounts = [new("unas", "Media", "Exploded")] }), "An unknown mount state was accepted.");

        foreach (var alias in new[] { "docker.io", "hub.docker.com", " Index.Docker.IO ", "registry-1.docker.io", "registry.hub.docker.com" })
            check(StackStore.RegistryHost(alias) == "docker.io", $"The Docker Hub name '{alias}' wasn't kept as docker.io.");
        check(StackStore.RegistryHost("GHCR.io") == "ghcr.io" && StackStore.RegistryHost("localhost:5000") == "localhost:5000"
            && StackStore.RegistryApi("docker.io") == "registry-1.docker.io" && StackStore.RegistryApi("ghcr.io") == "ghcr.io",
            "Registry hosts weren't normalised.");
        foreach (var bad in new[] { "", "dockerhub", "bad host", "https://ghcr.io", "ghcr.io/zack", "-x.io" })
            Rejects(() => StackStore.RegistryHost(bad), $"The registry '{bad}' was accepted.");
        var registryRoot = Directory.CreateTempSubdirectory("lucia-registries-");
        try
        {
            const string token = "dckr_pat_s3cret";
            var registries = new StackStore(Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(registryRoot.FullName, "state") }),
                new EphemeralDataProtectionProvider(), null!, null!, null!, null!, TimeProvider.System, null!)
            { RegistrySignIn = (_, _, secret, _) => Task.FromResult<string?>(secret == token ? null : "Docker Hub didn't accept that username and token.") };
            async Task Refused(Func<Task> action, int status, string code, string message)
            {
                try { await action(); }
                catch (HardwareOnboardingException error) when (error.StatusCode == status && error.Code == code) { check(true, message); return; }
                throw new InvalidOperationException(message);
            }
            Task Save(string host, string? username, string? secret) => registries.SaveRegistry(host, new(username, secret), "tester", CancellationToken.None);

            await Save("hub.docker.com", "zack", token);
            await Save("docker.io", " zack2 ", null);
            check(await registries.DesiredRegistries(CancellationToken.None) is [{ Host: "docker.io", Username: "zack2", Secret: token }],
                "A sign-in saved without a token didn't keep the saved token.");
            var listed = System.Text.Json.JsonSerializer.Serialize(await registries.RegistryList(CancellationToken.None));
            check(listed.Contains("docker.io", StringComparison.Ordinal) && !listed.Contains(token, StringComparison.Ordinal)
                && !File.ReadAllText(Path.Combine(registryRoot.FullName, "stacks", "registries.json")).Contains(token, StringComparison.Ordinal),
                "A registry token was listed or stored in plain text.");
            await Refused(() => Save("docker.io", "zack", "revoked"), 400, "registry_sign_in_failed", "A token the registry refused was saved.");
            await Refused(() => Save("ghcr.io", "zack", null), 400, "invalid_registry_secret", "A new registry was saved without a token.");
            await Refused(() => Save("ghcr.io", "za ck", token), 400, "invalid_registry_user", "A username with a space was accepted.");
            await Refused(() => Save("ghcr.io", "zack", "a\nb"), 400, "invalid_registry_secret", "A token with a line break was accepted.");
            for (var i = 1; i < StackStore.MaxRegistries; i++) await Save($"r{i}.example:5000", "org+robot", token);
            await Save("r1.example:5000", "robot$two", token);
            await Refused(() => Save("ghcr.io", "zack", token), 409, "too_many_registries", "A registry beyond the limit was saved.");
            check((await registries.RegistryLogins(CancellationToken.None)).GetValueOrDefault("registry-1.docker.io") == $"zack2:{token}"
                && (await registries.DesiredRegistries(CancellationToken.None)).Length == StackStore.MaxRegistries,
                "Update checks didn't get Docker Hub's sign-in at its API host.");
            await registries.DeleteRegistry("index.docker.io", CancellationToken.None);
            await Refused(() => registries.DeleteRegistry("docker.io", CancellationToken.None), 404, "registry_not_found", "A removed registry was removed again.");
        }
        finally { registryRoot.Delete(true); }
        check(StackRequirements.Unmet(["memory>=1G"], null) == "memory>=1G", "A node that never reported met a requirement.");

        foreach (var bad in new[] { "cuda", "cuda=11", "cuda>=12", "cuda=12.4" })
            Rejects(() => StackRequirements.Normalize([bad]), $"The requirement '{bad}' was accepted.");
        var uuid = "GPU-cbeac6c4-3134-d34a-9fb5-fc0a0daf1981";
        var cudaRuntime = host.Runtime! with { CudaVersion = "13.3", Gpus = [host.Runtime.Gpus[0] with { Uuid = uuid }] };
        var cudaHost = host with { Runtime = cudaRuntime };
        var pinned12 = new NodeGpuSettings(12);
        check(StackRequirements.Unmet(["cuda=12"], cudaHost, pinned12) is null, "A server pinned to CUDA 12 didn't meet cuda=12.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost, pinned12) == "cuda=13", "A CUDA 12 server met cuda=13.");
        check(StackRequirements.Unmet(["cuda=12"], cudaHost) == "cuda=12", "A server without a pinned line met a cuda requirement.");
        check(StackRequirements.Unmet(["cuda=13"], cudaHost with { Runtime = cudaRuntime with { CudaVersion = "12.8" } },
            new NodeGpuSettings(13)) == "cuda=13", "A pinned line the driver no longer supports still met its requirement.");
        check(CudaLines.Unsupported(13, cudaRuntime) is null && CudaLines.Unsupported(12, cudaRuntime) is null,
            "A current driver and GPU were ruled out.");
        check(CudaLines.Unsupported(12, cudaRuntime with { CudaVersion = null }) is not null
            && CudaLines.Unsupported(13, cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] }) is { } pascal
            && pascal.Contains("7.5") && CudaLines.Unsupported(12, cudaRuntime with { Gpus = [] }) is not null,
            "CUDA line support ignored the driver's CUDA version, a GPU's compute capability, or a server without GPUs.");
        foreach (var (bad, why, runtime) in new (SaveNodeGpuRequest, string, NodeRuntime)[] {
            (new(11), "on an unknown line", cudaRuntime),
            (new(13), "on a Pascal GPU", cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] }) })
        {
            try { CudaLines.Validate(bad, runtime); throw new InvalidOperationException($"A CUDA line {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400 or 409) { check(true, why); }
        }

        var localAi = StackCatalog.Find("local-ai");
        var gpuNode = new ManagedNodeFacts(Guid.NewGuid(), "lucialab01", true, cudaHost, new(13));
        var settings = StackCatalog.Settings(localAi, new() { ["gpus"] = uuid });
        check(settings["port"] == "8080", "Local AI's default port wasn't filled in.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["prot"] = "1" }), "An unknown catalog setting was accepted.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["port"] = "80a" }), "A malformed port was accepted.");
        Rejects(() => StackCatalog.Settings(localAi, new()), "Local AI without GPUs was accepted.");
        check(StackCatalog.Server(localAi, gpuNode) is { Unmet: null, Reason: null, Gpus: [{ Unsupported: null }] }
            && StackCatalog.Server(localAi, gpuNode with { Gpu = null }).Reason is not null
            && StackCatalog.Server(localAi, gpuNode with { Online = false }).Reason is not null,
            "Local AI eligibility ignored the CUDA line or the server being offline.");
        var rendered = localAi.Render(settings, gpuNode, new Dictionary<string, string>());
        var env = StackCatalog.ReadEnv(rendered.Env);
        check(!rendered.Compose.Contains('\r'), "Local AI's compose has Windows line endings.");
        check(rendered.Compose.Contains("lucia-inference:0.1.8-cuda13@sha256:") && rendered.Compose.Contains($"device_ids: [\"{uuid}\"]")
            && rendered.Compose.Contains("HostPlatform__MemoryBudgetGiB: \"23\"") && rendered.Require.Contains("cuda=13")
            && env["LUCIA_WORKER_KEY"].Length >= 32 && env["LUCIA_WORKER_KEY"] != env["LUCIA_INFERENCE_KEY"],
            "Local AI rendered the wrong image, GPUs, memory budget, requirements or keys.");
        check(localAi.Render(settings, gpuNode, env).Env == rendered.Env, "Re-rendering Local AI replaced its keys.");
        check(settings["engine"] == "lucia" && !rendered.Compose.Contains("vllm") && rendered.Compose.Contains("- models:/models")
            && rendered.Compose.Contains("HostPlatform__ModelDirectory: /models\n"), "Local AI's default engine isn't Lucia Inference with a local library.");
        Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["engine"] = "ollama" }), "An unknown engine was accepted.");
        var model = Guid.NewGuid();
        var vllmSettings = StackCatalog.Settings(localAi, new()
        {
            ["gpus"] = uuid, ["engine"] = "vllm", ["library"] = "192.168.0.172:/var/nfs/shared/Models",
            ["vllm-model"] = model.ToString(), ["vllm-name"] = "Qwen/Qwen3-0.6B",
        });
        var library = localAi.Render(vllmSettings, gpuNode, env).Compose;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lucia-vllm-compose.yml"), library);
        check(library.Contains("vllm/vllm-openai:v0.30.0@sha256:") && library.Contains("\"8080:8000\"") && library.Contains("\"8081:8080\"")
            && library.Contains($"command: [\"/models/lucialab01/{model:N}/files\", \"--served-model-name\", \"Qwen/Qwen3-0.6B\", \"--max-model-len\", \"auto\", \"--tensor-parallel-size\", \"1\"]")
            && library.Contains("device: \":/var/nfs/shared/Models\"") && library.Contains("addr=192.168.0.172,nfsvers=3")
            && library.Contains("- library:/models:ro,nocopy") && library.Contains("- library:/models:nocopy") && library.Contains("Worker__LibraryOnly: \"true\"") && library.Contains("  vllm-cache:")
            && library.Split("device_ids").Length == 2 && !library.Contains("\n\n\n"),
            "vLLM rendered the wrong image, ports, command, NFS library or GPU reservation.");
        var idle = localAi.Render(new Dictionary<string, string>(vllmSettings) { ["vllm-model"] = "" }, gpuNode, env).Compose;
        check(!idle.Contains("vllm:") && !idle.Contains("vllm-cache") && idle.Contains("\"8081:8080\""), "vLLM without a model still rendered a vLLM service.");
        foreach (var (key, value, why) in new[] {
            ("library", "nas:relative", "a library that isn't host:/path"), ("library", "nas:/a b", "a library path with a space"),
            ("vllm-name", "../etc", "a served name that isn't a repository"), ("vllm-context", "12", "a context below 256"),
            ("vllm-model", "not-a-guid", "a model that isn't an id") })
        {
            var bad = new Dictionary<string, string>(vllmSettings) { [key] = value };
            try { localAi.Render(bad, gpuNode, env); throw new InvalidOperationException($"vLLM with {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400) { check(true, why); }
        }
        try
        {
            localAi.Render(vllmSettings, gpuNode with { Gpu = new(12), Status = cudaHost with { Runtime = cudaRuntime with { CudaVersion = "12.8" } } }, env);
            throw new InvalidOperationException("vLLM on a CUDA 12.8 driver was accepted.");
        }
        catch (HardwareOnboardingException error) when (error.Code == "vllm_driver_too_old") { check(true, "old driver"); }
        // llama.cpp serves the whole library through its router, so it runs without a chosen model.
        var llamaSettings = new Dictionary<string, string>(vllmSettings) { ["engine"] = "llamacpp", ["vllm-model"] = "", ["vllm-name"] = "" };
        var llama = localAi.Render(llamaSettings, gpuNode, env).Compose;
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "lucia-llama-compose.yml"), llama);
        check(llama.Contains("llama.cpp:server-cuda13-b11206@sha256:") && llama.Contains("\"8080:8080\"") && llama.Contains("\"8081:8080\"")
            && llama.Contains("command: [\"--models-preset\", \"/cache/llama-models.ini\", \"--models-max\", \"1\", \"--host\", \"0.0.0.0\", \"--port\", \"8080\"]")
            && llama.Contains("HostPlatform__LlamaCache: /cache") && llama.Contains("- llama-cache:/cache\n") && llama.Contains("- llama-cache:/cache:ro")
            && llama.Contains("\n  llama-cache:") && llama.Split("/models").Length == 3 && llama.Contains("condition: service_healthy")
            && llama.Contains("LLAMA_API_KEY: ${LUCIA_INFERENCE_KEY}") && !llama.Contains("vllm") && llama.Split("device_ids").Length == 2 && !llama.Contains("\n\n\n"),
            "llama.cpp rendered the wrong image, ports, command, cache, key or GPU reservation.");
        check(localAi.Render(new Dictionary<string, string>(llamaSettings) { ["models-max"] = "2" }, gpuNode, env).Compose.Contains("\"--models-max\", \"2\","),
            "llama.cpp ignored the models-loaded-at-once setting.");
        foreach (var count in new[] { "0", "6", "two", "-1" })
            Rejects(() => StackCatalog.Settings(localAi, new() { ["gpus"] = uuid, ["engine"] = "llamacpp", ["models-max"] = count }), $"models-max {count} was accepted.");
        check(llama.Contains("HostPlatform__LlamaLoadOnStartup: \"\"") && llama.Contains("lucia.load-on-startup: \"\""), "llama.cpp loads models at start nobody chose.");
        var pinnedLlama = localAi.Render(StackCatalog.Settings(localAi, new(llamaSettings)
            { ["models-max"] = "2", ["llama-load"] = "a/embed-GGUF:Q8_0, unsloth/Big-GGUF:Q5_K_M" }), gpuNode, env).Compose;
        check(pinnedLlama.Contains("HostPlatform__LlamaLoadOnStartup: \"a/embed-GGUF:Q8_0,unsloth/Big-GGUF:Q5_K_M\"")
            && pinnedLlama.Contains("lucia.load-on-startup: \"a/embed-GGUF:Q8_0,unsloth/Big-GGUF:Q5_K_M\""),
            "llama.cpp's worker or container didn't get the models to load at start.");
        foreach (var (load, max) in new[] { ("a/x:Q4,a/y:Q4", "1"), ("a/x:Q4\"", "2"), ("x:Q4", "2"), ("a/x", "2") })
            Rejects(() => localAi.Render(new Dictionary<string, string>(llamaSettings) { ["models-max"] = max, ["llama-load"] = load }, gpuNode, env),
                $"Loaded at start {load} with models-max {max} was accepted.");
        check(!library.Contains("LlamaCache") && !library.Contains("llama-cache"), "vLLM's worker mirrored models for llama.cpp.");
        var created = DateTimeOffset.UnixEpoch;
        LocalModel Gguf(string repository, string file, ModelDownloadState state = ModelDownloadState.Ready, ModelKind kind = ModelKind.Chat) =>
            new(Guid.NewGuid(), new("huggingface", repository, file, kind), state, created = created.AddMinutes(1), created);
        var first = Gguf("Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF", "L3.3-70B-Magnum-Diamond-Q4_K_M.gguf");
        var sharded = Gguf("unsloth/Big-GGUF", "Q5_K_M/Big-Q5_K_M-00001-of-00002.gguf");
        var plain = Gguf("a/plain-GGUF", "plain.gguf");
        var embed = Gguf("a/embed-GGUF", "e-Q8_0.gguf", kind: ModelKind.Embedding) with
        {
            Inspection = new("bert", ModelKind.Embedding, 1, 1, 512, 0, 0, []),
        };
        var longEmbed = Gguf("a/long-embed-GGUF", "long-F16.gguf", kind: ModelKind.Embedding);
        var presets = ModelCatalog.LlamaPresets("/models/lucialab01", [first, sharded,
            Gguf("Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF", "copy/L3.3-70B-Magnum-Diamond-Q4_K_M.gguf"),
            Gguf("a/b-GGUF", "b-Q4_K_M.gguf", ModelDownloadState.Downloading), embed, longEmbed,
            Gguf("a/odd-GGUF", "odd;x-Q4_K_M.gguf"), plain]);
        check(presets == "version = 1\n"
            + $"\n[Doctor-Shotgun/L3.3-70B-Magnum-Diamond-GGUF:Q4_K_M]\nmodel = /models/lucialab01/{first.Id:N}/files/L3.3-70B-Magnum-Diamond-Q4_K_M.gguf\n"
            + $"\n[unsloth/Big-GGUF:Q5_K_M]\nmodel = /models/lucialab01/{sharded.Id:N}/files/Q5_K_M/Big-Q5_K_M-00001-of-00002.gguf\n"
            + $"\n[a/plain-GGUF:plain]\nmodel = /models/lucialab01/{plain.Id:N}/files/plain.gguf\n"
            + $"\n[a/embed-GGUF:Q8_0]\nmodel = /models/lucialab01/{embed.Id:N}/files/e-Q8_0.gguf\nembeddings = true\nc = 512\nbatch-size = 512\nubatch-size = 512\n"
            + $"\n[a/long-embed-GGUF:F16]\nmodel = /models/lucialab01/{longEmbed.Id:N}/files/long-F16.gguf\nembeddings = true\nc = 8192\nbatch-size = 8192\nubatch-size = 8192\n",
            "llama.cpp presets listed a model that isn't a Ready GGUF, a duplicate name, a path INI can't hold, or an embedding model without its batch.");
        check(ModelCatalog.LlamaPresets("/m", [plain, embed], new HashSet<string>(["A/Embed-GGUF:Q8_0"], StringComparer.OrdinalIgnoreCase))
                == $"version = 1\n\n[a/plain-GGUF:plain]\nmodel = /m/{plain.Id:N}/files/plain.gguf\n"
                + $"\n[a/embed-GGUF:Q8_0]\nmodel = /m/{embed.Id:N}/files/e-Q8_0.gguf\nembeddings = true\nc = 512\nbatch-size = 512\nubatch-size = 512\nload-on-startup = true\n",
            "llama.cpp presets didn't mark exactly the chosen model to load at start.");
        var pooled = Gguf("a/pooled-GGUF", "p-Q4_K_M.gguf") with { Inspection = new("mistral3", ModelKind.Embedding, 1, 1, 262144, 0, 0, []) };
        check(ModelCatalog.LlamaPresets("/m", [pooled]).EndsWith("embeddings = true\nc = 8192\nbatch-size = 8192\nubatch-size = 8192\n"),
            "llama.cpp served a pooling GGUF downloaded as chat without embeddings.");
        foreach (var (bad, why, target) in new (Dictionary<string, string>, string, ManagedNodeFacts)[] {
            (new() { ["gpus"] = "GPU-00000000-0000-0000-0000-000000000000", ["port"] = "8080" }, "with a GPU the server doesn't have", gpuNode),
            (settings, "without a CUDA line", gpuNode with { Gpu = null }),
            (settings, "on a Pascal GPU with CUDA 12", gpuNode with { Gpu = new(12),
                Status = cudaHost with { Runtime = cudaRuntime with { Gpus = [cudaRuntime.Gpus[0] with { ComputeCapability = "6.1" }] } } }) })
        {
            try { localAi.Render(bad, target, new Dictionary<string, string>()); throw new InvalidOperationException($"Local AI {why} was accepted."); }
            catch (HardwareOnboardingException error) when (error.StatusCode is 400 or 409) { check(true, why); }
        }

        var transfers = new StackTransfers(TimeProvider.System);
        var move = Guid.NewGuid();
        using var received = new MemoryStream();
        var receiving = transfers.Receive(move, received, CancellationToken.None);
        var payload = Enumerable.Range(0, 600_000).Select(i => (byte)i).ToArray();
        var sent = await transfers.Send(move, new MemoryStream(payload), payload.Length, CancellationToken.None);
        check(await receiving && sent == payload.Length && received.ToArray().SequenceEqual(payload)
            && transfers.Status(move) is { Bytes: 600_000, Total: 600_000 }, "A move's data didn't reach the receiving node intact.");
        var cancelled = Guid.NewGuid();
        var abandoned = transfers.Receive(cancelled, new MemoryStream(), CancellationToken.None);
        transfers.Forget(cancelled);
        check(!await abandoned.WaitAsync(TimeSpan.FromSeconds(5)), "A cancelled move left its receiver waiting.");

        check(StackImages.Compare("v8.9.0-ls104", "v9.1.0-ls108") > 0 && StackImages.Compare("v8.9.0-ls104", "v8.9.0-ls105") > 0
            && StackImages.Compare("v8.9.0-ls104", "9.1.0-dev") == 0 && StackImages.Compare("2026.9.2", "2026.10.0b1") == 0
            && StackImages.Compare("2026.9.2", "2026.10.0") > 0 && StackImages.Compare("2026.9.2", "2026.9.1") < 0
            && StackImages.Compare("1.43.4.10903-e5521bd8c-ls326", "1.43.5.11000-0ab12cd34-ls327") > 0
            && StackImages.Version("latest") is null, "Image tags compared wrongly.");
        check(new ImageRef("lscr.io/linuxserver/plex", "1", null).Source == ("lscr.io", "linuxserver/plex")
            && new ImageRef("eclipse-mosquitto", "2", null).Source == ("registry-1.docker.io", "library/eclipse-mosquitto")
            && new ImageRef("docker.io/valkey/valkey", "9", null).Source == ("registry-1.docker.io", "valkey/valkey")
            && new ImageRef("grafana/loki", "3", null).Source == ("registry-1.docker.io", "grafana/loki"), "An image's registry was misread.");
        check(StackImages.DataStore("ghcr.io/immich-app/postgres") && StackImages.DataStore("valkey/valkey")
            && StackImages.DataStore("metabrainz/musicbrainz-docker-db") && !StackImages.DataStore("docker.litellm.ai/berriai/litellm-database"),
            "A database image was offered major upgrades, or an app wasn't.");
        const string pinnedCompose = "services:\n  a:\n    image: lscr.io/linuxserver/nzbhydra2:v8.9.0-ls104@sha256:aa\n  b:\n    image: \"nodered/node-red:5.0.4\"\n  c:\n    image: ${IMAGE}\n";
        var upgraded = StackImages.Apply(pinnedCompose, new Dictionary<string, string>
        {
            ["lscr.io/linuxserver/nzbhydra2"] = "v9.1.0-ls108@sha256:bb", ["nodered/node-red"] = "5.1.0@sha256:cc",
        });
        check(upgraded.Contains("image: lscr.io/linuxserver/nzbhydra2:v9.1.0-ls108@sha256:bb\n", StringComparison.Ordinal)
            && upgraded.Contains("image: \"nodered/node-red:5.1.0\"\n", StringComparison.Ordinal) && upgraded.Contains("image: ${IMAGE}\n", StringComparison.Ordinal),
            "Upgrading rewrote a compose's images wrongly.");
        check(StackImages.Keep(pinnedCompose, new Dictionary<string, string> { ["lscr.io/linuxserver/nzbhydra2"] = "v9.1.0-ls108@sha256:bb" })?.Count == 1
            && StackImages.Keep(pinnedCompose, new Dictionary<string, string> { ["lscr.io/linuxserver/nzbhydra2"] = "v8.9.0-ls104@sha256:aa" }) is null
            && StackImages.Keep(pinnedCompose, new Dictionary<string, string> { ["gone/app"] = "2.0@sha256:dd" }) is null,
            "A catalog app kept an image override its catalog had caught up with.");
        check(!StackImages.Refs($"services:\n  init:\n    image: {ObservabilityApp.Alpine}\n").Any(), "Lucia's own helper image was offered as an app update.");
    }
}
