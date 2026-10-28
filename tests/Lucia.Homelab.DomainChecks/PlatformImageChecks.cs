using System.Text.Json;
using System.Text.Json.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Packages;
using Lucia.Homelab.Server.Platform;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The Spark's platform images: the built-in pins and their policy classes, the owner's override file the identity AppHost
/// reads, what the host offers and writes for the Spark's worker, and the pins and rules the worker shares.
/// </summary>
internal static class PlatformImageChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        void Throws(string json, string contains, string why)
        {
            try { PlatformImages.Effective(json, out _); }
            catch (InvalidOperationException error) when (error.Message.Contains(contains, StringComparison.Ordinal)) { check(true, why); return; }
            check(false, why);
        }

        // Versions read exactly as the catalog's checker reads them.
        foreach (var tag in new[] { "16.15-alpine", "v3.7.16", "2026.8.5", "0.164.0", "v8.9.0-ls104", "1.2.3-abcdef12", "latest", "2.6.10" })
            check(PlatformImages.Version(tag)?.Shape == StackImages.Version(tag)?.Shape
                && (PlatformImages.Version(tag)?.Numbers ?? []).SequenceEqual(StackImages.Version(tag)?.Numbers ?? []),
                $"PlatformImages and StackImages read {tag} the same way.");

        // What each policy class offers.
        string Offered(string policy, string current, params string[] tags) =>
            string.Join(" ", PlatformImages.Candidates(policy, current, tags).Select(item => $"{item.Tag}:{item.Kind}:{(item.Applicable ? "y" : "n")}"));
        check(Offered("patch", "16.15-alpine", "16.16-alpine", "16.17-alpine", "17.1-alpine", "16.20", "latest") == "16.17-alpine:patch:y",
            "PostgreSQL is offered only its newest patch of the same shape.");
        check(Offered("authentik", "2026.8.5", "2026.8.6", "2026.8.7", "2026.10.0", "2026.10.2", "2026.12.1", "2026.10.0-rc1")
            == "2026.8.7:patch:y 2026.10.2:release-line:y", "Authentik is offered its newest patch and the newest of the next release line only.");
        check(Offered("minor", "v3.7.16", "v3.7.17", "v3.8.2", "v4.0.1", "v3.8") == "v3.7.17:patch:y v3.8.2:minor:y v4.0.1:major:n",
            "Traefik is offered a patch and a minor, and a major that can't be applied.");
        check(Offered("minor", "0.30.2") == "" && Offered("plugin", "v1.8.1", "v1.9.0") == "", "Nothing newer means no offers; the plugin is never offered as an image.");
        check(PlatformImages.Allowed("authentik", "2026.8.5", "2026.10.1", ["2026.10.1", "2026.12.0"])
            && !PlatformImages.Allowed("authentik", "2026.8.5", "2026.12.0", ["2026.10.1", "2026.12.0"])
            && PlatformImages.Allowed("authentik", "2026.12.3", "2027.2.0", ["2027.2.0", "2027.4.0"])
            && !PlatformImages.Allowed("minor", "v3.7.16", "v4.0.0") && !PlatformImages.Allowed("patch", "2.6.10", "2.7.0")
            && !PlatformImages.Allowed("patch", "2.6.10", "2.6.9"), "Policies allow only what they offer, and Authentik one release line at a time.");

        // The override file: used while newer, dropped once Lucia catches up, refused when it isn't exactly what the worker writes.
        var authentik = PlatformImages.Pin(PlatformImages.BuiltIn, "authentik");
        var database = PlatformImages.Pin(PlatformImages.BuiltIn, "database");
        string Digest(char c) => "sha256:" + new string(c, 64);
        string Entry(PlatformPin pin, string tag, string digest, string? replacedTag = null, string? replacedDigest = null, string? repository = null) =>
            "{\"repository\":\"" + (repository ?? pin.Image) + "\",\"tag\":\"" + tag + "\",\"digest\":\"" + digest + "\",\"replaces\":{\"tag\":\"" + (replacedTag ?? pin.Tag) + "\",\"digest\":\"" + (replacedDigest ?? pin.Digest) + "\"}}";
        string File(params string[] entries) => "{\"schemaVersion\":1,\"images\":{" + string.Join(",", entries) + "}}";
        check(PlatformImages.Effective(null, out var none) == PlatformImages.BuiltIn && none is null, "Without an override file the built-in pins deploy.");
        var newer = PlatformImages.Effective(File($"\"authentik\":{Entry(authentik, "2026.8.6", Digest('b'))}",
            $"\"database\":{Entry(database, database.Tag, Digest('c'))}"), out var kept);
        check(PlatformImages.Pin(newer, "authentik") is { Tag: "2026.8.6" } pinned && pinned.Digest == Digest('b') && pinned.Containers.SequenceEqual(authentik.Containers)
            && PlatformImages.Pin(newer, "database").Digest == Digest('c') && PlatformImages.Pin(newer, "database").Tag == database.Tag && kept is null,
            "A newer tag and a rebuild of the built-in tag both replace the built-in pin, Authentik's server and worker together.");
        var caughtUp = PlatformImages.Effective(File($"\"authentik\":{Entry(authentik, "2026.8.4", Digest('b'), "2026.8.3")}",
            $"\"database\":{Entry(database, database.Tag, Digest('c'), database.Tag, Digest('d'))}",
            $"\"directory\":{Entry(PlatformImages.Pin(PlatformImages.BuiltIn, "directory"), "2.6.11", Digest('e'))}"), out var pruned);
        check(PlatformImages.Pin(caughtUp, "authentik") == authentik && PlatformImages.Pin(caughtUp, "database") == database
            && PlatformImages.Pin(caughtUp, "directory").Tag == "2.6.11" && pruned?["images"] is JsonObject left && left.Count == 1 && left.ContainsKey("directory"),
            "Overrides a Lucia release caught up with (an older tag, or a rebuild recorded against an older built-in digest) are dropped from the file.");
        Throws("[]", "isn't a JSON object", "An override file must be an object.");
        Throws("""{"schemaVersion":2,"images":{}}""", "schemaVersion 1", "An override file has schema 1.");
        Throws("""{"schemaVersion":1,"images":{},"note":"x"}""", "exactly schemaVersion 1", "An override file has no other fields.");
        void Dropped(string json, string why) =>
            check(PlatformImages.Effective(json, out var left).SequenceEqual(PlatformImages.BuiltIn) && left?["images"] is JsonObject { Count: 0 }, why);
        Dropped(File($"\"portainer\":{Entry(authentik, "2026.8.6", Digest('b'))}"), "An unknown service is dropped from the file, never deployed and never fatal.");
        Dropped(File($"\"authentik\":{Entry(authentik, "2026.8.6", Digest('b'), repository: "ghcr.io/attacker/server")}"),
            "An override that changes an image's repository is dropped; the built-in image deploys.");
        Throws(File($"\"authentik\":{Entry(authentik, "2026.8.6", "sha256:XYZ")}"), "sha256 digests", "Digests are sha256 hex.");
        Throws(File($"\"authentik\":{Entry(authentik, "2026.8.6 --privileged", Digest('b'))}"), "plain tags", "Tags are plain tags.");
        Dropped(File($"\"gateway\":{Entry(PlatformImages.Pin(PlatformImages.BuiltIn, "gateway"), "v4.0.0", Digest('b'))}"),
            "A version its policy doesn't allow (a major, or after a release tightens the policy) is dropped, not pinned and not fatal.");
        Throws(File($"\"authentik\":{Entry(authentik, "2026.8.6", Digest('b')).Replace("}}", "},\"extra\":1}")}"), "needs exactly", "Entries have exactly their fields.");
        Throws(File($"\"authentik\":{Entry(authentik, "2026.8.6", Digest('b'))}") + new string(' ', 17 * 1024), "16 KB", "Override files are bounded.");

        // The manifest the AppHost writes for the worker and the host.
        var manifest = JsonNode.Parse(PlatformImages.Manifest(newer, DateTimeOffset.UnixEpoch))!;
        check(manifest["images"]!.AsArray().Count == PlatformImages.BuiltIn.Length
            && manifest["images"]!.AsArray().Single(item => (string?)item!["service"] == "authentik") is { } recorded
            && (bool)recorded["override"]! && (string?)recorded["builtIn"]!["tag"] == authentik.Tag && (string?)recorded["registry"] == "ghcr.io"
            && !(bool)manifest["images"]!.AsArray().Single(item => (string?)item!["service"] == "ca")!["override"]!,
            "The manifest records each running pin, its built-in pin and whether the owner's override is in force.");

        // The worker shares the services, their policies and the version rules.
        var root = Root();
        var python = System.IO.File.ReadAllText(Path.Combine(root, "tools", "runners", "platform_images.py"));
        check(PlatformImages.BuiltIn.All(pin => python.Contains($"\"{pin.Service}\": \"{pin.Policy}\"", StringComparison.Ordinal))
            && python.Contains("\"docker.io\": \"registry-1.docker.io\"", StringComparison.Ordinal) && python.Contains("\"ghcr.io\": \"ghcr.io\"", StringComparison.Ordinal),
            "platform_images.py knows every platform image with its policy class and registry.");
        var apphost = System.IO.File.ReadAllText(Path.Combine(root, "src", "Lucia.Homelab.Identity.AppHost", "AppHost.cs"));
        check(PlatformImages.BuiltIn.All(pin => !apphost.Contains(pin.Sha256, StringComparison.Ordinal) && apphost.Contains($"\"{pin.Service}\"", StringComparison.Ordinal))
            && apphost.Contains("PlatformImages.Effective(", StringComparison.Ordinal),
            "The AppHost takes every third-party image from PlatformImages, so an override reaches every container that runs it.");
        var worker = System.IO.File.ReadAllText(Path.Combine(root, "tools", "runners", "platform_update_worker.py"));
        check(worker.Contains("BOOTSTRAP / \".run.lock\", STATE / \".provision.lock\"", StringComparison.Ordinal)
            && worker.Contains("fcntl.LOCK_EX | fcntl.LOCK_NB", StringComparison.Ordinal) && worker.Contains("application.application_status(p, origin)", StringComparison.Ordinal)
            && worker.Contains($"\"{PlatformImages.Folder}\"", StringComparison.Ordinal) && worker.Contains("KEEP_BACKUPS = 3", StringComparison.Ordinal)
            && worker.Contains("images.STALE_GPU", StringComparison.Ordinal) && !worker.Contains("shell=True", StringComparison.Ordinal),
            "The worker takes the deploy locks without waiting, checks Authentik as the host deploy does, keeps three dumps and never runs a shell.");
        var deploy = System.IO.File.ReadAllText(Path.Combine(root, ".github", "skills", "lucia-host-deploy", "scripts", "deploy-agent-jobs.py"));
        var bootstrap = System.IO.File.ReadAllText(Path.Combine(root, "tools", "desktop", "bootstrap.py"));
        var payload = System.IO.File.ReadAllText(Path.Combine(root, "src", "Lucia.Desktop.Core", "BootstrapPayload.cs"));
        string[] shipped = ["tools/runners/platform_update_worker.py", "tools/runners/platform_images.py", "src/Lucia.Homelab.Server/Platform/PlatformImages.cs"];
        check(shipped.All(file => deploy.Contains(file, StringComparison.Ordinal) && bootstrap.Contains(file, StringComparison.Ordinal) && payload.Contains(file, StringComparison.Ordinal))
            && deploy.Contains("for worker in (\"tools/runners/platform_update_worker.py\"", StringComparison.Ordinal)
            && bootstrap.Contains("p.run([sys.executable, str(platform_worker), \"install\"])", StringComparison.Ordinal),
            "Fresh installs and host deploys ship the platform update worker, its rules and the shared pins, and install the worker.");

        await HostChecks(check);
    }

    /// <summary>The host's side: what it reads, what it offers and the only request it writes.</summary>
    private static async Task HostChecks(Action<bool, string> check)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "platform-checks-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(data, PlatformImages.Folder);
        try
        {
            var updates = new PlatformImageUpdates(Options.Create(new HardwareOnboardingOptions { StateDirectory = Path.Combine(data, "onboarding") }),
                TimeProvider.System, NullLogger<PlatformImageUpdates>.Instance);
            JsonNode Get() => JsonSerializer.SerializeToNode(updates.Get(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            async Task<PackageUpdatesException?> Refused(PlatformUpdateRequest request)
            {
                try { await updates.Apply(request, default); return null; }
                catch (PackageUpdatesException error) { return error; }
            }
            var (builtIn, unwritten, unrecorded) = updates.Running();
            check(unwritten is null && unrecorded is not null && builtIn == PlatformImages.BuiltIn
                && (string?)Get()["worker"]!["state"] == "missing", "Before an identity deploy records them, the host shows the built-in pins and no worker.");
            check(await Refused(new(Services: ["authentik"])) is { StatusCode: 503, Code: "worker_missing" }, "Nothing is requested without the Spark's worker.");

            var pins = PlatformImages.BuiltIn.Select(pin => pin.Service == "directory" ? pin with { Tag = "2.6.11", Digest = "sha256:" + new string('e', 64) } : pin).ToArray();
            Directory.CreateDirectory(folder);
            await System.IO.File.WriteAllTextAsync(Path.Combine(folder, PlatformImages.ManifestFile), PlatformImages.Manifest(pins, DateTimeOffset.UtcNow));
            var (recorded, writtenAt, problem) = updates.Running();
            check(problem is null && writtenAt is not null && PlatformImages.Pin(recorded, "directory").Tag == "2.6.11"
                && updates.Rows().Single(row => row.Service == "directory").Override, "The host reads what runs from the AppHost's manifest, override included.");

            async Task Status(string state, long generation, DateTimeOffset checkedAt) =>
                await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "status.json"), new JsonObject
                {
                    ["schemaVersion"] = 1, ["generation"] = generation, ["state"] = state, ["step"] = null, ["message"] = null, ["startedAt"] = null,
                    ["finishedAt"] = null, ["updates"] = new JsonArray(), ["rolledBack"] = false, ["backup"] = null, ["history"] = new JsonArray(),
                    ["checkedAt"] = checkedAt.ToString("O"),
                }.ToJsonString());
            await Status("idle", 0, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5));
            check(await Refused(new(Services: ["authentik"])) is { StatusCode: 503, Code: "worker_unavailable" }, "A silent worker gets no requests.");
            await Status("idle", 0, DateTimeOffset.UtcNow);
            check(await Refused(new(Services: ["authentik"], PatchOnly: true)) is { StatusCode: 409, Code: "no_platform_updates" }, "With nothing found, nothing is requested.");

            var authentik = PlatformImages.Pin(pins, "authentik");
            var gateway = PlatformImages.Pin(pins, "gateway");
            var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
            PlatformOffer Offer(string tag, char digest, string kind, bool applicable = true, DateTimeOffset? seen = null) =>
                new(tag, "sha256:" + new string(digest, 64), kind, applicable, PlatformImageUpdates.Notes("authentik", tag), null, seen ?? DateTimeOffset.UtcNow);
            updates.Found(authentik, [Offer("2026.8.6", 'b', "patch", seen: old), Offer("2026.10.1", 'c', "release-line")]);
            updates.Found(gateway, [Offer("v4.0.1", 'd', "major", applicable: false)]);
            var listed = Get();
            check(listed["securityWaiting"]!.AsArray().Single() is { } waiting && (string?)waiting["service"] == "authentik" && (int)waiting["days"]! >= 7,
                "An Authentik patch that has waited more than a week is flagged for the dashboard.");
            check(await Refused(new([new("gateway", "v4.0.1")])) is { StatusCode: 409, Code: "update_unavailable" }, "A major is shown but never applied.");
            check(await Refused(new([new("authentik", "2026.9.9")])) is { StatusCode: 409, Code: "update_unavailable" }, "Only versions Lucia found can be asked for.");
            check(await Refused(new([new("authentik", "2026.10.1"), new("database")])) is { StatusCode: 409 } or { StatusCode: 400 },
                "Asking for a service with nothing to apply is refused.");
            check(await Refused(new([new("portainer")])) is { StatusCode: 400, Code: "unknown_service" }, "Unknown services are refused.");
            check(!System.IO.File.Exists(Path.Combine(folder, "request.json")), "Refused updates write no request.");

            var applied = JsonSerializer.SerializeToNode(await updates.Apply(new(Services: ["authentik", "gateway"], PatchOnly: true), default),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var request = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(folder, "request.json")))!.AsObject();
            check((long)request["generation"]! == 1 && request.Select(pair => pair.Key).Order().SequenceEqual(["generation", "requestedAt", "schemaVersion", "updates"])
                && request["updates"]!.AsArray().Single() is { } asked && (string?)asked["service"] == "authentik" && (string?)asked["tag"] == "2026.8.6"
                && (string?)asked["digest"] == "sha256:" + new string('b', 64) && ((string?)applied["disruption"])!.Contains("10 to 15 seconds", StringComparison.Ordinal),
                "Update all patches asks for Authentik's patch with the digest Lucia found, skips the gateway's major, and states the disruption.");
            check(await Refused(new(Services: ["authentik"])) is { StatusCode: 409, Code: "update_busy" }, "A second update waits for the first.");
            await Status("succeeded", 1, DateTimeOffset.UtcNow);
            check(await Refused(new([new("authentik", "2026.10.1"), new("directory", "2.6.12")])) is { StatusCode: 409 or 400 },
                "An Authentik release line is never combined with other updates.");
            updates.Found(PlatformImages.Pin(pins, "node-exporter"), [Offer("v1.13.0", 'f', "minor")]);
            check(await Refused(new([new("authentik", "2026.10.1"), new("node-exporter", "v1.13.0")])) is { StatusCode: 400, Code: "release_line_alone" },
                "Authentik's release line goes on its own.");
            var line = JsonSerializer.SerializeToNode(await updates.Apply(new([new("authentik", "2026.10.1")]), default),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            check((long)line["generation"]! == 2 && ((string?)line["disruption"])!.Contains("backs it up first", StringComparison.Ordinal),
                "A release line is requested alone, says the database is backed up, and raises the generation.");
            check(PlatformImageUpdates.Disruption(["node-exporter", "telemetry-relay"]).StartsWith("Only telemetry", StringComparison.Ordinal)
                && PlatformImageUpdates.Disruption(["ca"]).StartsWith("Certificates", StringComparison.Ordinal),
                "Telemetry-only and CA updates say they don't touch web traffic.");
            check(PlatformImageUpdates.Notes("database", "16.16-alpine") == "https://www.postgresql.org/docs/release/16.16/"
                && PlatformImageUpdates.Notes("authentik", "2026.8.6") == "https://github.com/goauthentik/authentik/releases/tag/version/2026.8.6"
                && PlatformImageUpdates.Notes("ca", "0.30.3") == "https://github.com/smallstep/certificates/releases/tag/v0.30.3",
                "Every offer links its project's release notes.");
        }
        finally { if (Directory.Exists(data)) Directory.Delete(data, recursive: true); }
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "tools", "identity", "provision.py")))
                return directory.FullName;
        throw new InvalidOperationException("The repository root wasn't found.");
    }
}
