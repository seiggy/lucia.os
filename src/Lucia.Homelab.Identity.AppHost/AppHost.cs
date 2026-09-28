using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);
var state = Path.GetFullPath(builder.Configuration["LUCIA_IDENTITY_STATE"]
    ?? throw new InvalidOperationException("Set LUCIA_IDENTITY_STATE by running tools/identity/provision.py."));
using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "settings.json")));
var config = settings.RootElement;
if (config.GetProperty("certificate_mode").GetString() != "private-ca")
    throw new InvalidOperationException("This provisioning phase supports private-ca. DNS-backed certificate migration is the next phase.");
var publicHost = config.GetProperty("public_host").GetString()!;
var baseDn = config.GetProperty("ldap_base_dn").GetString()!;
var uid = config.GetProperty("uid").GetInt32();
var gid = config.GetProperty("gid").GetInt32();
var ports = config.GetProperty("ports");
using var hostSettings = ReadHostSettings(state, publicHost, ports.GetProperty("authentik").GetInt32(), uid, gid);
using var bootSettings = ReadBootSettings(state, hostSettings);
if (hostSettings is not null && ports.EnumerateObject().Any(port => port.Value.GetInt32() == 443))
    throw new InvalidOperationException("Managed host HTTPS port 443 conflicts with an existing identity endpoint.");
if (bootSettings is not null && ports.EnumerateObject().Any(port =>
        port.Value.GetInt32() == bootSettings.RootElement.GetProperty("http_port").GetInt32()))
    throw new InvalidOperationException("Boot HTTP port conflicts with an existing identity endpoint.");
var compose = builder.AddDockerComposeEnvironment("identity");
compose.Resource.DashboardEnabled = false;
compose.ConfigureComposeFile(file => file.Name = "lucia-identity");

string StatePath(params string[] parts) => Path.Combine([state, .. parts]);
IResourceBuilder<ParameterResource> Secret(string name) => builder.AddParameter(
    name, () => File.ReadAllText(StatePath("secrets", name)).TrimEnd('\r', '\n'), publishValueAsDefault: false, secret: true);

static void Persistent(Service service, string name)
{
    service.Name = name;
    service.ContainerName = "lucia-" + name;
    service.Restart = "unless-stopped";
    service.Ports.Clear();
    service.Labels["io.lucia.component"] = "identity";
    service.SecurityOpt.Add("no-new-privileges:true");
}

static Healthcheck Health(params string[] command) => new()
{
    Test = [.. command], Interval = "10s", Timeout = "5s", StartPeriod = "30s", Retries = 12
};

var databasePassword = Secret("identity-db-password");
var authentikSecret = Secret("authentik-secret-key");
var bootstrapPassword = Secret("authentik-admin-password");
var bootstrapToken = Secret("authentik-bootstrap-token");

var postgres = builder.AddPostgres("identity-db", password: databasePassword)
    .WithImageTag("16-alpine")
    .WithImageSHA256("721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea")
    .WithVolume("lucia-identity-postgres", "/var/lib/postgresql/data")
    .WithEnvironment("POSTGRES_DB", "authentik")
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-db");
        service.Healthcheck = Health("CMD-SHELL", "pg_isready -U postgres -d authentik");
    });
var database = postgres.AddDatabase("identity-store", "authentik");

var ca = builder.AddContainer("identity-ca", "smallstep/step-ca", "0.30.2")
    .WithImageSHA256("a2b17872915c193259b75a5474c398326f41bd199f0842093e52cf4182bc8270")
    .WithEntrypoint("/bin/sh")
    .WithArgs("/config/start-ca.sh")
    .WithEnvironment("LUCIA_PUBLIC_HOST", publicHost)
    .WithEnvironment("LUCIA_CA_NAME", "Lucia local certificate authority")
    .WithBindMount(StatePath("ca"), "/home/step")
    .WithBindMount(StatePath("certificates"), "/certificates")
    .WithBindMount(StatePath("config"), "/config", isReadOnly: true)
    .WithBindMount(StatePath("secrets", "ca-password"), "/run/secrets/ca-password", isReadOnly: true)
    .WithHttpsEndpoint(port: ports.GetProperty("ca").GetInt32(), targetPort: 9000, name: "https")
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-ca");
        service.User = $"{uid}:{gid}";
        service.Ports.Add($"0.0.0.0:{ports.GetProperty("ca").GetInt32()}:9000");
        service.Healthcheck = Health("CMD", "step", "ca", "health", "--ca-url", "https://localhost:9000", "--root", "/home/step/certs/root_ca.crt");
    });

var ldap = builder.AddContainer("identity-ldap", "vegardit/openldap", "latest")
    .WithImageSHA256("b81f6c360830b21b9e6d8878563e3e11bd81e243693c83fa371efeee34556ce9")
    .WithEnvironment("LDAP_INIT_ORG_DN", baseDn)
    .WithEnvironment("LDAP_INIT_ORG_NAME", "Lucia")
    .WithEnvironment("LDAP_INIT_ROOT_USER_DN", $"uid=admin,{baseDn}")
    .WithEnvironment("LDAP_INIT_ROOT_USER_PW_FILE", "/run/secrets/ldap-admin-password")
    .WithEnvironment("LDAP_INIT_RFC2307BIS_SCHEMA", "1")
    .WithEnvironment("LDAP_INIT_PASSWORD_HASH", "ARGON2")
    .WithEnvironment("LDAP_INIT_PPOLICY_PW_MIN_LENGTH", "14")
    .WithEnvironment("LDAP_TLS_ENABLED", "false")
    .WithBindMount(StatePath("ldap", "data"), "/var/lib/ldap")
    .WithBindMount(StatePath("ldap", "config"), "/etc/ldap/slapd.d")
    .WithBindMount(StatePath("config", "init_org_tree.ldif"), "/opt/ldifs/init_org_tree.ldif", isReadOnly: true)
    .WithBindMount(StatePath("config", "init_org_entries.ldif"), "/opt/ldifs/init_org_entries.ldif", isReadOnly: true)
    .WithBindMount(StatePath("secrets", "ldap-admin-password"), "/run/secrets/ldap-admin-password", isReadOnly: true)
    .WithBindMount(StatePath("secrets", "authentik-ldap-password"), "/run/secrets/authentik-ldap-password", isReadOnly: true)
    .WithBindMount(StatePath("trust"), "/trust", isReadOnly: true)
    .WithEndpoint(targetPort: 389, name: "ldap", scheme: "tcp")
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-ldap");
        service.Healthcheck = Health("CMD", "ldapsearch", "-x", "-H", "ldap://127.0.0.1:389", "-b", "", "-s", "base", "namingContexts");
    });

var server = builder.AddContainer("identity-server", "goauthentik/server", "2026.8.3")
    .WithImageRegistry("ghcr.io")
    .WithImageSHA256("ab9b4e8cc4ab3f8d1198d2db6aeea66bafea1963b3f2843589e0d163f97d9849")
    .WithArgs("server")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", postgres.Resource.Name)
    .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", "authentik")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", "postgres")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", databasePassword)
    .WithEnvironment("AUTHENTIK_SECRET_KEY", authentikSecret)
    .WithEnvironment("AUTHENTIK_ERROR_REPORTING__ENABLED", "false")
    .WithVolume("lucia-authentik-data", "/data")
    .WithHttpEndpoint(targetPort: 9000, name: "http")
    .WaitFor(database)
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-server");
        service.DependsOn["identity-db"] = new ServiceDependency { Condition = "service_healthy" };
        service.ShmSize = "512mb";
    });

builder.AddContainer("identity-worker", "goauthentik/server", "2026.8.3")
    .WithImageRegistry("ghcr.io")
    .WithImageSHA256("ab9b4e8cc4ab3f8d1198d2db6aeea66bafea1963b3f2843589e0d163f97d9849")
    .WithArgs("worker")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__HOST", postgres.Resource.Name)
    .WithEnvironment("AUTHENTIK_POSTGRESQL__NAME", "authentik")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__USER", "postgres")
    .WithEnvironment("AUTHENTIK_POSTGRESQL__PASSWORD", databasePassword)
    .WithEnvironment("AUTHENTIK_SECRET_KEY", authentikSecret)
    .WithEnvironment("AUTHENTIK_BOOTSTRAP_PASSWORD", bootstrapPassword)
    .WithEnvironment("AUTHENTIK_BOOTSTRAP_TOKEN", bootstrapToken)
    .WithEnvironment("AUTHENTIK_ERROR_REPORTING__ENABLED", "false")
    .WithVolume("lucia-authentik-data", "/data")
    .WaitFor(database)
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-worker");
        service.DependsOn["identity-db"] = new ServiceDependency { Condition = "service_healthy" };
        service.ShmSize = "512mb";
    });

if (hostSettings is not null)
{
    var host = hostSettings.RootElement;
    var authentication = host.GetProperty("authentication");
    var publicHostIsDnsName = !IPAddress.TryParse(publicHost, out _);
    var managedHost = builder.AddContainer("lucia-host", "lucia-managed-host", host.GetProperty("image_tag").GetString()!.Split(':')[1])
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
        .WithEnvironment("ASPNETCORE_HTTP_PORTS", "8080")
        .WithEnvironment("HostPlatform__ApiKey", Secret("host-owner-api-key"))
        .WithEnvironment("HostPlatform__InferenceApiKey", Secret("host-inference-api-key"))
        .WithEnvironment("HostPlatform__ModelDirectory", "/models")
        .WithEnvironment("HostPlatform__HuggingFaceExecutable", "/opt/hf/bin/hf")
        .WithEnvironment("HostPlatform__HuggingFaceHomeDirectory", "/data/huggingface")
        .WithEnvironment("HostPlatform__Backend", "ggml_cuda")
        .WithEnvironment("HostPlatform__ContextTokens", host.GetProperty("context_tokens").GetRawText())
        .WithEnvironment("HostPlatform__VoiceReserveGiB", host.GetProperty("voice_reserve_gib").GetRawText())
        .WithEnvironment("HF_HOME", "/data/huggingface")
        .WithEnvironment("HOME", "/data")
        .WithEnvironment("HostAuthentication__Enabled", "true")
        .WithEnvironment("HostAuthentication__Authority", authentication.GetProperty("authority").GetString()!)
        .WithEnvironment("HostAuthentication__ClientId", authentication.GetProperty("client_id").GetString()!)
        .WithEnvironment("HostAuthentication__ClientSecretFile", "/run/secrets/host-oidc-client-secret")
        .WithEnvironment("HostAuthentication__PublicOrigin", authentication.GetProperty("public_origin").GetString()!)
        .WithEnvironment("HostAuthentication__CaCertificatePath", "/trust/lucia-root-ca.crt")
        .WithEnvironment("HostAuthentication__DataProtectionKeysDirectory", "/data/data-protection")
        .WithEnvironment("InferenceKeys__Directory", "/data/inference-keys")
        .WithEnvironment("HuggingFaceManagement__CredentialsDirectory", "/data/provider-credentials")
        .WithEnvironment("AdGuardManagement__CredentialsDirectory", "/data/network-credentials")
        .WithEnvironment("CloudflareDomains__CredentialsDirectory", "/data/network-credentials")
        .WithEnvironment("DomainOnboarding__StateDirectory", "/data/domains")
        .WithEnvironment("DomainOnboarding__GatewayDirectory", "/domain-gateway")
        .WithEnvironment("DomainOnboarding__InstallationId", host.GetProperty("installation_id").GetString()!)
        .WithEnvironment("DomainOnboarding__IngressAddress", bootSettings?.RootElement.GetProperty("bind_address").GetString() ?? "")
        .WithBindMount(StatePath("gateway", "domains"), "/domain-gateway")
        .WithEnvironment("HardwareOnboarding__StateDirectory", "/data/onboarding")
        .WithEnvironment("PackageUpdates__Directory", "/data/packages")
        .WithEnvironment("SparkTelemetry__Enabled", "true")
        .WithEnvironment("SparkTelemetry__ProcDirectory", "/host-metrics")
        .WithEnvironment("SparkTelemetry__StoragePath", "/data")
        .WithBindMount("/proc/stat", "/host-metrics/stat", isReadOnly: true)
        .WithBindMount("/proc/meminfo", "/host-metrics/meminfo", isReadOnly: true)
        .WithBindMount("/proc/loadavg", "/host-metrics/loadavg", isReadOnly: true)
        .WithBindMount("/proc/uptime", "/host-metrics/uptime", isReadOnly: true)
        .WithBindMount("/proc/1/net/dev", "/host-metrics/netdev", isReadOnly: true)
        .WithBindMount("/proc/1/net/route", "/host-metrics/route", isReadOnly: true)
        .WithBindMount(host.GetProperty("data_directory").GetString()!, "/data")
        .WithBindMount(host.GetProperty("model_directory").GetString()!, "/models")
        .WithBindMount(StatePath("trust"), "/trust", isReadOnly: true)
        .WithBindMount(authentication.GetProperty("client_secret_file").GetString()!, "/run/secrets/host-oidc-client-secret", isReadOnly: true)
        .WithHttpEndpoint(targetPort: 8080, name: "http")
        .PublishAsDockerComposeService((_, service) =>
        {
            Persistent(service, "lucia-host");
            service.ContainerName = "lucia-homelab-host";
            service.Image = host.GetProperty("image_id").GetString();
            service.PullPolicy = "never";
            service.User = $"{uid}:{gid}";
            service.Labels["io.lucia.component"] = "managedhost";
            service.Labels["io.lucia.host-installation"] = host.GetProperty("installation_id").GetString()!;
            service.Labels["io.lucia.artifact-sha256"] = host.GetProperty("artifact_sha256").GetString()!;
            service.Devices.Add("nvidia.com/gpu=all");
            if (publicHostIsDnsName)
                service.ExtraHosts[publicHost] = "host-gateway";
            service.CapDrop.Add("ALL");
            service.Init = true;
            service.Healthcheck = Health("CMD", "/usr/bin/python3", "-c",
                "import json,urllib.request; r=urllib.request.urlopen('http://127.0.0.1:8080/health/live',timeout=4); " +
                "assert json.load(r).get('status') == 'ok'");
        });
    var index = 0;
    foreach (var network in host.GetProperty("trusted_proxy_networks").EnumerateArray())
        managedHost.WithEnvironment($"HostAuthentication__TrustedProxyNetworks__{index++}", network.GetString()!);
    if (bootSettings is not null)
    {
        var boot = bootSettings.RootElement;
        var installationQualified = boot.TryGetProperty("installation_qualified", out var installation)
            && installation.GetBoolean();
        managedHost
            .WithEnvironment("HardwareOnboarding__DiscoveryNetworkCidr", boot.GetProperty("network_cidr").GetString()!)
            .WithEnvironment("HardwareOnboarding__BootBaseUrl", authentication.GetProperty("public_origin").GetString()!)
            .WithEnvironment("HardwareOnboarding__DiscoveryAdapterQualified", boot.GetProperty("discovery_qualified").GetBoolean() ? "true" : "false")
            .WithEnvironment("HardwareOnboarding__InstallationEnabled", installationQualified ? "true" : "false")
            .WithEnvironment("HardwareOnboarding__BootArtifactsQualified", installationQualified ? "true" : "false")
            .WithEnvironment("HardwareOnboarding__EnrollmentQualified", installationQualified ? "true" : "false")
            .WithEnvironment("HardwareOnboarding__EnrollmentStatusFile", "/data/nodes/enrollment-worker.json")
            .WithEnvironment("Boot__Enabled", "true")
            .WithEnvironment("Boot__ControlDirectory", "/data/boot-control")
            .WithEnvironment("Boot__AllowedNetworks__0", boot.GetProperty("network_cidr").GetString()!);
        var address = boot.GetProperty("bind_address").GetString()!;
        var httpPort = boot.GetProperty("http_port").GetInt32();
        builder.AddContainer("boot", "lucia-boot", boot.GetProperty("image_tag").GetString()!.Split(':')[1])
            .WithBindMount(boot.GetProperty("assets_directory").GetString()!, "/boot", isReadOnly: true)
            .WithBindMount(boot.GetProperty("control_directory").GetString()!, "/control", isReadOnly: true)
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .PublishAsDockerComposeService((_, service) =>
            {
                Persistent(service, "boot");
                service.Image = boot.GetProperty("image_id").GetString()!;
                service.PullPolicy = "never";
                service.Labels["io.lucia.component"] = "boot";
                service.Labels["io.lucia.host-installation"] = host.GetProperty("installation_id").GetString()!;
                service.User = "65534:65534";
                service.ReadOnly = true;
                service.CapDrop.Add("ALL");
                service.Networks.Clear();
                service.NetworkMode = "bridge";
                service.Sysctls["net.ipv4.ip_unprivileged_port_start"] = "0";
                service.Tmpfs.Add("/tmp:rw,noexec,nosuid,size=16m,uid=65534,gid=65534");
                service.Init = true;
                service.Ports.Add($"{address}:69:69/udp");
                service.Ports.Add($"{address}:40000-40016:40000-40016/udp");
                service.Ports.Add($"{address}:{httpPort}:8080");
                service.Healthcheck = Health("CMD", "python3", "-c",
                    "import urllib.request,urllib.error; " +
                    "\ntry: urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:8080/debian-installer/amd64/linux',method='HEAD'),timeout=4)" +
                    "\nexcept urllib.error.HTTPError as e:\n if e.code != 403: raise");
            });
    }
}

var gateway = builder.AddContainer("identity-gateway", "traefik", "v3.6")
    .WithImageSHA256("31267173a15b4944e797a76ffd9c419707c8d8b32fe5b610f80cd0cfa05f372d")
    .WithEntrypoint("/bin/sh")
    .WithArgs("-c",
        "while ! (cd /certificates && sha256sum -c ready.sha256 >/dev/null 2>&1); do sleep 1; done; " +
        "exec traefik --entrypoints.authentik.address=:8443 --entrypoints.ldaps.address=:8636 " +
        // No read timeout on the host entrypoint: a stack move's upload can wait minutes for its receiver and then stream for
        // hours. Kestrel still enforces header timeouts and minimum body rates on every other route.
        (hostSettings is not null ? "--entrypoints.host.address=:8444 --entrypoints.host.transport.respondingTimeouts.readTimeout=0 " : "") +
        "--providers.file.directory=/config --providers.file.watch=true --api.dashboard=false --log.level=INFO")
    .WithBindMount(StatePath("gateway"), "/config", isReadOnly: true)
    .WithBindMount(StatePath("certificates"), "/certificates", isReadOnly: true)
    .WithHttpsEndpoint(port: ports.GetProperty("authentik").GetInt32(), targetPort: 8443, name: "https")
    .WithEndpoint(port: ports.GetProperty("ldaps").GetInt32(), targetPort: 8636, name: "ldaps", scheme: "tcp")
    .WaitFor(server)
    .WaitFor(ldap)
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-gateway");
        service.DependsOn["identity-ldap"] = new ServiceDependency { Condition = "service_healthy" };
        service.Ports.Add($"0.0.0.0:{ports.GetProperty("authentik").GetInt32()}:8443");
        service.Ports.Add($"0.0.0.0:{ports.GetProperty("ldaps").GetInt32()}:8636");
        if (hostSettings is not null)
            service.Ports.Add("0.0.0.0:443:8444");
    });

if (hostSettings is not null)
{
    gateway.WithHttpsEndpoint(port: 443, targetPort: 8444, name: "host");
    gateway.WithBindMount(Path.Combine(hostSettings.RootElement.GetProperty("data_directory").GetString()!, "domains", "certificates"),
        "/domain-certificates", isReadOnly: true);
}

builder.AddContainer("identity-renewer", "smallstep/step-ca", "0.30.2")
    .WithImageSHA256("a2b17872915c193259b75a5474c398326f41bd199f0842093e52cf4182bc8270")
    .WithEntrypoint("/bin/sh")
    .WithArgs("/config/renew-certificate.sh")
    .WithEnvironment("STEPPATH", "/tmp/step")
    // A bind-mounted script update must also replace the already-running renewal process.
    .WithEnvironment("LUCIA_RENEWAL_SCRIPT_SHA256",
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(StatePath("config", "renew-certificate.sh")))))
    .WithBindMount(StatePath("config"), "/config", isReadOnly: true)
    .WithBindMount(StatePath("certificates"), "/certificates")
    .WithBindMount(StatePath("trust"), "/trust", isReadOnly: true)
    .WithBindMount(StatePath("gateway"), "/gateway")
    .WaitFor(ca)
    .PublishAsDockerComposeService((_, service) =>
    {
        Persistent(service, "identity-renewer");
        service.DependsOn["identity-ca"] = new ServiceDependency { Condition = "service_healthy" };
        service.User = $"{uid}:{gid}";
        service.Healthcheck = Health("CMD", "step", "certificate", "verify",
            "/certificates/identity.crt", "--roots", "/trust/lucia-root-ca.crt");
    });

builder.Build().Run();

static JsonDocument? ReadBootSettings(string state, JsonDocument? hostSettings)
{
    var path = Path.Combine(state, "boot-settings.json");
    CheckPath(path);
    if (!File.Exists(path)) return null;
    if (hostSettings is null)
        throw new InvalidOperationException("Boot services require the existing managed host.");
    using var source = File.OpenRead(CheckedFile(path));
    var document = JsonDocument.Parse(source);
    try
    {
        var boot = document.RootElement;
        var host = hostSettings.RootElement;
        string Text(string name) => boot.GetProperty(name).GetString()
            ?? throw new InvalidOperationException("Boot settings contain a null field.");
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Invalid boot settings: " + message);
        }
        Require(boot.GetProperty("schema_version").GetInt32() == 1 && Text("component") == "boot",
            "unsupported component or schema.");
        Require(Text("installation_id") == host.GetProperty("installation_id").GetString(), "installation ownership mismatch.");
        Require(Regex.IsMatch(Text("image_source_sha256"), "\\A[a-f0-9]{64}\\z")
            && Regex.IsMatch(Text("bundle_sha256"), "\\A[a-f0-9]{64}\\z")
            && Regex.IsMatch(Text("image_id"), "\\Asha256:[a-f0-9]{64}\\z"), "immutable prepared artifact identities are required.");
        var imageKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            Text("installation_id") + ":" + Text("image_source_sha256"))));
        Require(Text("image_tag") == "lucia-boot:" + imageKey, "boot image tag does not match its source and installation.");
        var root = Text("boot_state");
        Require(root == Path.Combine(Path.GetDirectoryName(state)!, "boot"), "boot state must stay in its dedicated sibling directory.");
        CheckPath(root);
        var assets = Text("assets_directory");
        CheckPath(assets);
        Require(assets == Path.Combine(root, "bundles", Text("bundle_sha256"), "public") && Directory.Exists(assets),
            "public boot assets must stay in their dedicated prepared directory.");
        var control = Text("control_directory");
        CheckPath(control);
        Require(control == Path.Combine(host.GetProperty("data_directory").GetString()!, "boot-control")
            && Directory.Exists(control), "the host-owned boot admission directory is missing.");
        if (!IPAddress.TryParse(Text("bind_address"), out var address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || address.ToString() != Text("bind_address"))
            throw new InvalidOperationException("Invalid boot settings: an explicit canonical IPv4 bind address is required.");
        Require(IPNetwork.TryParse(Text("network_cidr"), out var network)
            && network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && network.Contains(address), "bind address must belong to the provisioning network.");
        Require(new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Select(IPNetwork.Parse)
            .Any(range => network.PrefixLength >= range.PrefixLength && range.Contains(network.BaseAddress)),
            "provisioning must use an explicit private subnet.");
        Require(boot.GetProperty("http_port").GetInt32() is >= 1024 and <= 65535,
            "the public boot HTTP port must be unprivileged.");
        _ = boot.GetProperty("discovery_qualified").GetBoolean();
        using var owner = JsonDocument.Parse(File.ReadAllText(CheckedFile(Path.Combine(root, "owner.json"))));
        Require(owner.RootElement.GetProperty("schema_version").GetInt32() == 1
            && owner.RootElement.GetProperty("component").GetString() == "boot"
            && owner.RootElement.GetProperty("identity_state").GetString() == state, "boot state is not owned by this installation.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(CheckedFile(Path.Combine(root, "images", imageKey + ".json"))));
        var image = receipt.RootElement;
        var labels = image.GetProperty("labels");
        Require(image.GetProperty("image_id").GetString() == Text("image_id")
            && image.GetProperty("image_tag").GetString() == Text("image_tag")
            && image.GetProperty("source_sha256").GetString() == Text("image_source_sha256")
            && labels.GetProperty("io.lucia.component").GetString() == "boot"
            && labels.GetProperty("io.lucia.host-installation").GetString() == Text("installation_id")
            && labels.GetProperty("io.lucia.boot-source-sha256").GetString() == Text("image_source_sha256"),
            "boot image does not match its owned preparation receipt.");
        return document;
    }
    catch
    {
        document.Dispose();
        throw;
    }
}

static JsonDocument? ReadHostSettings(string state, string publicHost, int authPort, int uid, int gid)
{
    var path = Path.Combine(state, "host-settings.json");
    CheckPath(path);
    if (!File.Exists(path))
        return null;
    if (new FileInfo(path).Length > 65536)
        throw new InvalidOperationException("Managed host settings are oversized.");
    var document = JsonDocument.Parse(File.ReadAllText(path));
    try
    {
        var host = document.RootElement;
        string Text(string name) => host.GetProperty(name).GetString()
            ?? throw new InvalidOperationException("Managed host settings contain a null value.");
        bool Hash(string value) => Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z");
        void Require(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("Invalid managed host settings: " + message);
        }
        Require(host.GetProperty("schema_version").GetInt32() == 1 && Text("component") == "managedhost"
            && Text("rid") == "linux-arm64" && Text("version") == "0.1.0", "unsupported schema or artifact.");
        Require(uid > 0 && gid > 0 && authPort != 443, "host must run as a non-root identity UID/GID with a separate HTTPS port.");
        var installation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        Require(Text("installation_id") == installation, "installation ownership mismatch.");
        Require(Hash(Text("artifact_sha256")) && Hash(Text("dockerfile_sha256")), "invalid artifact hashes.");
        var imageKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text("artifact_sha256") + ":" + Text("dockerfile_sha256"))));
        Require(Text("image_tag") == "lucia-managed-host:" + imageKey
            && Regex.IsMatch(Text("image_id"), "\\Asha256:[a-f0-9]{64}\\z"), "image must have an immutable, recorded identity.");
        var root = Text("host_state");
        Require(root != Path.GetPathRoot(root) && root != Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "host state must be a dedicated directory.");
        foreach (var name in new[] { "host_state", "data_directory", "model_directory" })
        {
            CheckPath(Text(name));
            Require(Directory.Exists(Text(name)), "persistent directories are missing.");
        }
        Require(Text("data_directory") == Path.Combine(root, "data"), "data directory changed.");
        var models = Text("model_directory");
        static bool Within(string candidate, string directory) =>
            candidate == directory || candidate.StartsWith(
                directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
        Require(!Within(models, state) && !Within(state, models)
            && (models == Path.Combine(root, "models") || (!Within(models, root) && !Within(root, models))),
            "models must not expose identity secrets or overlap host data/configuration.");
        foreach (var directory in new[] { "data-protection", "huggingface" })
        {
            var dataPath = Path.Combine(root, "data", directory);
            CheckPath(dataPath);
            Require(Directory.Exists(dataPath), "persistent data subdirectory is missing.");
        }
        using var owner = JsonDocument.Parse(File.ReadAllText(CheckedFile(Path.Combine(root, "owner.json"))));
        Require(owner.RootElement.GetProperty("component").GetString() == "managedhost"
            && owner.RootElement.GetProperty("identity_state").GetString() == state
            && owner.RootElement.GetProperty("model_directory").GetString() == models, "unowned or inconsistent host state.");
        using var receipt = JsonDocument.Parse(File.ReadAllText(CheckedFile(Path.Combine(root, "images", imageKey + ".json"))));
        var image = receipt.RootElement;
        var labels = image.GetProperty("labels");
        Require(image.GetProperty("image_id").GetString() == Text("image_id")
            && image.GetProperty("image_tag").GetString() == Text("image_tag")
            && labels.GetProperty("io.lucia.component").GetString() == "managedhost"
            && labels.GetProperty("io.lucia.host-installation").GetString() == installation
            && labels.GetProperty("io.lucia.artifact-sha256").GetString() == Text("artifact_sha256")
            && labels.GetProperty("io.lucia.dockerfile-sha256").GetString() == Text("dockerfile_sha256"),
            "image does not match its verified preparation receipt.");
        Require(host.GetProperty("context_tokens").GetInt32() is >= 256 and <= 1048576
            && host.GetProperty("voice_reserve_gib").GetDouble() is >= 8 and <= 1024, "invalid memory/context profile.");
        var authorityHost = publicHost.Contains(':') ? $"[{publicHost}]" : publicHost;
        var authentication = host.GetProperty("authentication");
        Require(authentication.GetProperty("public_origin").GetString() == $"https://{authorityHost}"
            && authentication.GetProperty("authority").GetString() == $"https://{authorityHost}:{authPort}/application/o/lucia/",
            "OIDC URLs do not match the existing identity.");
        var clientId = authentication.GetProperty("client_id").GetString();
        Require(!string.IsNullOrWhiteSpace(clientId) && clientId.Length <= 256 && !clientId.Any(char.IsWhiteSpace)
            && !clientId.Any(char.IsControl), "invalid OIDC client ID.");
        var clientSecret = CheckedFile(authentication.GetProperty("client_secret_file").GetString()!);
        CheckSecret(clientSecret, 1);
        foreach (var name in new[] { "host-owner-api-key", "host-inference-api-key" })
            CheckSecret(CheckedFile(Path.Combine(state, "secrets", name)), 32);
        CheckedFile(Path.Combine(state, "trust", "lucia-root-ca.crt"));
        CheckedFile(Path.Combine(state, "gateway", "host.yml"));
        var networks = host.GetProperty("trusted_proxy_networks");
        Require(networks.GetArrayLength() is > 0 and <= 8, "explicit proxy networks are required.");
        foreach (var network in networks.EnumerateArray())
        {
            Require(IPNetwork.TryParse(network.GetString(), out var parsed), "invalid proxy network CIDR.");
            var bytes = parsed.BaseAddress.GetAddressBytes();
            var isPrivate = bytes.Length == 4
                ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                : (bytes[0] & 0xfe) == 0xfc;
            Require(isPrivate && parsed.PrefixLength >= (bytes.Length == 4 ? 8 : 32),
                "proxy networks must be isolated private subnets, never default routes.");
        }
        return document;
    }
    catch
    {
        document.Dispose();
        throw;
    }
}

static void CheckPath(string path)
{
    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path)
        throw new InvalidOperationException("Managed host paths must be canonical absolute paths.");
    for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        if (new FileInfo(current).LinkTarget is not null || ((File.Exists(current) || Directory.Exists(current))
            && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Managed host paths must not contain symbolic links or junctions.");
}

static void CheckSecret(string path, int minimum)
{
    if (new FileInfo(path).Length > 16384 || (!OperatingSystem.IsWindows()
        && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0))
        throw new InvalidOperationException("Managed host secrets must be bounded, private files.");
    var value = File.ReadAllText(path).TrimEnd('\r', '\n');
    if (string.IsNullOrWhiteSpace(value) || value.Length < minimum || value.Any(char.IsControl))
        throw new InvalidOperationException("Persistent managed host secret is missing or invalid; restore it.");
}

static string CheckedFile(string path)
{
    CheckPath(path);
    if (!File.Exists(path) || new FileInfo(path).Length > 65536)
        throw new InvalidOperationException("Required managed host file is missing or oversized; restore it before deploying.");
    return path;
}
