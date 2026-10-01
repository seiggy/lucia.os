using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentGovernance.Mcp;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Stacks;
using Microsoft.Extensions.AI;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Lucia.Homelab.AssistantChecks")]

namespace Lucia.Homelab.Server.Assistant;

public sealed record TextEdit(string Find, string Replace);

/// <summary>A web address for a custom app. Publishing it on the internet is a separate, always-approved tool.</summary>
public sealed record AppRoute(string Host, int Port, int? GrpcPort = null);

/// <summary>
/// One Lucia tool. Its tier decides whether the owner approves the call; its result is redacted and capped before the
/// model sees it, and only Lucia's own errors reach the model, never an unexpected exception's text.
/// </summary>
public sealed class AssistantTool(AIFunction inner, string tier, Func<string, CancellationToken, Task<string>> redact, ILogger logger)
    : DelegatingAIFunction(inner)
{
    public const int MaxOutput = 48 * 1024;

    public string Tier { get; } = tier;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        object? result;
        try { result = await base.InvokeCoreAsync(arguments, cancellationToken); }
        catch (Exception error)
        {
            var cause = error is TargetInvocationException { InnerException: { } wrapped } ? wrapped : error;
            if (cause is OperationCanceledException) ExceptionDispatchInfo.Throw(cause);
            if (cause is not (HardwareOnboardingException or UniFiException or AdGuardManagementException or AssistantException
                or ArgumentException or JsonException))
            {
                logger.LogWarning("Assistant tool {Tool} failed ({ErrorType}) at {Trace}", Name, cause.GetType().Name, cause.StackTrace);
                throw new AssistantException(500, "tool_failed", "The tool failed unexpectedly.");
            }
            throw new AssistantException(400, "tool_error", await redact(cause.Message, cancellationToken));
        }
        var text = result switch
        {
            null => "Done.",
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } value => value.GetString() ?? "",
            JsonElement value => value.GetRawText(),
            _ => JsonSerializer.Serialize(result, AssistantStream.Json),
        };
        text = await redact(text, cancellationToken);
        return new TextContent(text.Length <= MaxOutput ? text
            : text[..MaxOutput] + "\n[Cut at 48 KB. Ask for less, such as fewer log lines or one app.]");
    }
}

/// <summary>
/// The assistant's view of the lab: servers, apps, logs, the catalog, DNS, the network and storage, plus the changes it
/// may propose. The broker decides which calls wait for the owner; these only do the work and keep secrets out of results.
/// </summary>
public sealed partial class AssistantTools(ManagedNodeEnrollment nodes, StackStore stacks, NodeRequests requests,
    UniFiConnectionService unifi, AdGuardConnectionService adguard, DomainOnboardingStore domains, AssistantBroker broker,
    ILogger<AssistantTools> logger)
{
    private const string NodeHelp = "The server: its hostname, DNS name, address or id.";
    private const string AppHelp = "The app's name, from list_apps.";
    private const string Delivered = "Lucia delivers it within a minute; check get_app or list_apps, and read_logs if it fails.";
    private const string Masked = "••••••";
    private const int MaxLogChars = 40 * 1024;
    private const int MaxClients = 300;
    private const int MaxChoices = 8;
    private static readonly McpCredentialRedactor Redactor = new();
    private static readonly HashSet<string> SecretFields = new(StackCatalog.Apps.SelectMany(app => app.Fields)
        .Where(field => field.Kind == "secret").SelectMany(field => new[] { field.Id, StackCatalog.SecretKey(field) }),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The tools for one turn, acting as <paramref name="actor"/> for the Lucia user <paramref name="user"/>.</summary>
    public IReadOnlyList<AssistantTool> Create(string actor, string? user)
    {
        string? login;
        try { login = OwnerSshKeys.Username(user); }
        catch (HardwareOnboardingException) { login = null; }
        return
        [
            Tool(ToolTier.Read, "list_nodes",
                "Lists Lucia's managed servers: hostname, address, DNS name, state, when each last reported, agent and OS status, GPUs and pending updates.",
                async Task<object?> (CancellationToken ct) =>
                {
                    var rows = await NodeRows(ct);
                    foreach (var row in rows.OfType<JsonObject>())
                        if (row["status"] is JsonObject status && status["updates"] is JsonObject updates && updates["packages"] is JsonArray packages)
                        {
                            updates.Remove("packages");
                            updates["packageCount"] = packages.Count;
                        }
                    return rows;
                }),
            Tool(ToolTier.Read, "get_node", "Shows one managed server in full, with how to reach it over SSH.",
                async Task<object?> ([Description(NodeHelp)] string node, CancellationToken ct) =>
                {
                    var (id, hostname) = await Node(node, ct);
                    var row = (await NodeRows(ct)).OfType<JsonObject>().FirstOrDefault(item => Text(item["nodeId"]) == id.ToString())
                        ?? throw Unknown(node);
                    row["connect"] = new JsonObject
                    {
                        ["ssh"] = $"ssh {login ?? "<your Lucia username>"}@{Text(row["dnsName"]) ?? Text(row["address"]) ?? hostname}",
                        ["note"] = "Owners sign in with their Lucia username and password, and sudo asks for the password again. "
                            + "Keys added in Settings → SSH keys reach every server in about a minute. lucia-recovery accepts only the recovery key.",
                    };
                    return row;
                }),
            Tool(ToolTier.Read, "list_containers",
                "Lists every container and listening port a server last reported, including ones Lucia doesn't manage.",
                async Task<object?> ([Description(NodeHelp)] string node, CancellationToken ct) => stacks.Inventory((await Node(node, ct)).Id)),
            Tool(ToolTier.Read, "read_logs", "Reads the newest lines of a container's logs. Container names come from list_containers or get_app.",
                async Task<object?> ([Description(NodeHelp)] string node, [Description("The container's name.")] string container,
                    [Description("How many of the newest lines to read, 1 to 1000.")] int tail = 200, CancellationToken ct = default) =>
                {
                    var result = await requests.Logs((await Node(node, ct)).Id, container, Math.Clamp(tail, 1, 1000), ct);
                    if (!result.Success)
                        throw new AssistantException(404, "logs_unavailable", result.Message ?? "The server couldn't read those logs.");
                    var output = result.Output ?? "";
                    if (output.Length == 0) return "The container has written no logs.";
                    if (output.Length <= MaxLogChars) return output;
                    var cut = output.IndexOf('\n', output.Length - MaxLogChars);
                    return "[Earlier lines cut.]\n" + (cut < 0 ? output[^MaxLogChars..] : output[(cut + 1)..]);
                }),
            Tool(ToolTier.Read, "list_apps",
                "Lists the apps Lucia runs: server, desired and actual state, revision, address, catalog template and image updates.",
                async Task<object?> (CancellationToken ct) =>
                {
                    var list = JsonSerializer.SerializeToNode(await stacks.List(ct), AssistantStream.Json);
                    if (list is JsonObject root && root["stacks"] is JsonArray apps)
                        foreach (var app in apps.OfType<JsonObject>())
                        {
                            app.Remove("containers");
                            if (app["template"] is JsonObject template) template.Remove("settings");
                        }
                    return list;
                }),
            Tool(ToolTier.Read, "get_app",
                "Shows one app: its compose, environment (secret values hidden), manifest, web addresses and container status.",
                async Task<object?> ([Description(AppHelp)] string app, CancellationToken ct) =>
                {
                    var detail = JsonSerializer.SerializeToNode(await stacks.Get(App(app), ct), AssistantStream.Json);
                    if (detail is JsonObject root && Text(root["env"]) is { } env)
                        root["env"] = new JsonObject(StackCatalog.ReadEnv(env).Select(pair =>
                            KeyValuePair.Create(pair.Key, (JsonNode?)(IsSecret(pair.Key) ? "[hidden]" : pair.Value))));
                    return detail;
                }),
            Tool(ToolTier.Read, "list_catalog", "Lists the apps Lucia can install from its catalog, and which servers can run each.",
                async Task<object?> (CancellationToken ct) =>
                {
                    var facts = await nodes.Facts(ct);
                    return StackCatalog.Apps.Select(entry => new
                    {
                        entry.Id, entry.Name, entry.Summary, entry.Needs, entry.ServerBound, entry.UsesAddress,
                        Servers = facts.Select(fact => StackCatalog.Server(entry, fact)).Select(server =>
                            server.Unmet is null && server.Reason is null ? server.Hostname
                                : $"{server.Hostname} (no: {server.Reason ?? "needs " + server.Unmet})").ToArray(),
                    }).ToArray();
                }),
            Tool(ToolTier.Read, "get_catalog_app", "Shows a catalog app's settings. Give a server to preview the compose Lucia would run there.",
                async Task<object?> ([Description("The catalog app's id, from list_catalog.")] string app,
                    [Description("A server to preview the app on.")] string? node = null,
                    [Description("Setting id → value for the preview; the rest keep their defaults.")] Dictionary<string, string>? settings = null,
                    CancellationToken ct = default) =>
                {
                    var entry = StackCatalog.Find(App(app));
                    object? preview = null;
                    if (node is not null)
                    {
                        var given = Typed(entry, settings);
                        foreach (var field in entry.Fields.Where(field => field is { Kind: "secret", Optional: false }))
                            given.TryAdd(field.Id, "preview_only_placeholder");
                        preview = await stacks.Preview(entry.Id, new CatalogPreviewRequest((await Node(node, ct)).Hostname, given), ct);
                    }
                    return new
                    {
                        entry.Id, entry.Version, entry.Name, entry.Summary, entry.Needs, entry.Require, entry.ServerBound, entry.UsesAddress,
                        entry.BackupMode,
                        Fields = entry.Fields.Where(field => field.Kind != "hidden").Select(field => new
                        {
                            field.Id, field.Label, field.Kind, field.Default, field.Help, field.Options, field.When, field.Optional,
                            Note = field.Kind == "secret" ? "Only the owner types this, on the Apps page. Never ask for it in chat." : null,
                        }),
                        Preview = preview,
                    };
                }),
            Tool(ToolTier.Read, "list_storage", "Lists the NAS shares Lucia mounts on servers, and their state.",
                async Task<object?> (CancellationToken ct) => await stacks.NasList(ct)),
            Tool(ToolTier.Read, "list_backups", "Shows each app's backup settings, recent runs and newest snapshots.",
                async Task<object?> (CancellationToken ct) =>
                {
                    var backups = JsonSerializer.SerializeToNode(await stacks.Backups(ct), AssistantStream.Json);
                    if (backups is JsonObject root && root["apps"] is JsonArray apps)
                        foreach (var app in apps.OfType<JsonObject>())
                            if (app["snapshots"] is JsonArray { Count: > 10 } snapshots)
                            {
                                app["snapshots"] = new JsonArray(snapshots.Take(10).Select(item => item?.DeepClone()).ToArray());
                                app["olderSnapshots"] = snapshots.Count - 10;
                            }
                    return backups;
                }),
            Tool(ToolTier.Read, "dns_lookup", "Resolves a host name with the Lucia host's DNS, and lists the AdGuard rewrites that match it.",
                async Task<object?> ([Description("The host name, such as nas.home.example.com.")] string name, CancellationToken ct) =>
                {
                    var host = AssistantBroker.Host(name)
                        ?? throw new AssistantException(400, "invalid_name", "Give a host name, not an address or a URL.");
                    object addresses;
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        try
                        {
                            addresses = (await Dns.GetHostAddressesAsync(host, timeout.Token).WaitAsync(timeout.Token))
                                .Select(ip => ip.ToString()).Distinct().ToArray();
                        }
                        catch (SocketException error) when (error.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
                        {
                            addresses = "It doesn't resolve.";
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            addresses = "The lookup timed out after 5 seconds.";
                        }
                    }
                    object? rewrites = null;
                    string? adguardNote = null;
                    try
                    {
                        rewrites = (await adguard.ListRewritesAsync(ct)).Where(rewrite => Matches(rewrite.Domain, host)
                            || string.Equals(rewrite.Answer, host, StringComparison.OrdinalIgnoreCase)).ToArray();
                    }
                    catch (AdGuardManagementException error) { adguardNote = $"Lucia couldn't read AdGuard: {error.Message}"; }
                    return new { Name = host, Addresses = addresses, Rewrites = rewrites, AdguardNote = adguardNote };
                }),
            Tool(ToolTier.Read, "list_network_clients", "Lists the devices UniFi knows: MAC, address, fixed IP, network and whether each is online.",
                async Task<object?> ([Description("Only devices whose MAC or address contains this text.")] string? search = null,
                    CancellationToken ct = default) =>
                {
                    var (known, online) = await unifi.ListClientsAsync(ct);
                    var live = ByMac(online);
                    var saved = ByMac(known);
                    var clients = live.Keys.Union(saved.Keys).Select(mac =>
                    {
                        var now = live.GetValueOrDefault(mac);
                        var before = saved.GetValueOrDefault(mac);
                        return new
                        {
                            Mac = mac,
                            Address = now?.Address ?? before?.Address,
                            FixedIp = now is { UseFixedIp: true } ? now.FixedIp : before is { UseFixedIp: true } ? before.FixedIp : null,
                            Online = now is not null,
                            NetworkId = now?.NetworkId ?? before?.NetworkId,
                        };
                    }).Where(client => string.IsNullOrWhiteSpace(search) || client.Mac.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || client.Address?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true
                        || client.FixedIp?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
                    return new
                    {
                        Clients = clients.Take(MaxClients),
                        Truncated = clients.Count > MaxClients ? true : (bool?)null,
                        Note = "UniFi gives Lucia no device names; match devices by MAC or address.",
                    };
                }),
            // Not "web_fetch": that name is the Copilot runtime's own tool, which would read the lab's network.
            Tool(ToolTier.Web, "read_web_page",
                "Reads a public web page or text file as text, such as documentation or a raw file on GitHub, 20,000 characters at a "
                + "time. Sites on the owner's allowed list open at once; others wait for the owner's approval. It can't reach the "
                + "owner's network: use the lab tools for that.",
                async Task<object?> ([Description("The page's absolute http or https URL.")] string url,
                    [Description("The character to start at, for a page longer than one read.")] int start = 0, CancellationToken ct = default) =>
                    await AssistantWeb.Read(url, start, ct)),
            // Not "ask_user": that is the Copilot runtime's own tool, which answers through a callback without a tool call id.
            Tool(ToolTier.Read, "ask_owner",
                "Asks the owner a question in the chat and waits for their answer. Ask only when you can't go on without a decision "
                + "only they can make, and offer choices when there are a few clear options. Never ask for passwords, keys or tokens: "
                + "use request_secret.",
                async Task<object?> ([Description("The question, in a sentence or two.")] string question, AIFunctionArguments args,
                    [Description("Up to 8 short answers to pick from.")] string[]? choices = null,
                    [Description("Whether the owner may type their own answer instead of a choice.")] bool allowFreeform = true,
                    CancellationToken ct = default) =>
                {
                    var (session, call) = Call(args);
                    if (string.IsNullOrWhiteSpace(question) || question.Length > 1000)
                        throw new AssistantException(400, "invalid_question", "Ask a question of up to 1,000 characters.");
                    if (choices?.Length > MaxChoices || choices?.Any(choice => string.IsNullOrWhiteSpace(choice) || choice.Length > 200) == true)
                        throw new AssistantException(400, "invalid_choices", $"Offer up to {MaxChoices} choices of up to 200 characters each.");
                    if (choices is null or [] && !allowFreeform)
                        throw new AssistantException(400, "no_way_to_answer", "Offer choices, or let the owner type an answer.");
                    return await broker.AskAsync(session, call, "answer", ct) is { } answer ? new { Answer = answer }
                        : "The owner chose not to answer. Go on without it if you can; otherwise say what you need and stop.";
                }),
            Tool(ToolTier.Secret, "request_secret",
                "Asks the owner to type a password, key or token for a custom app; Lucia saves it in the app's environment and you never "
                + "see it. Name the variable as a secret, such as PLEX_CLAIM_TOKEN or DB_PASSWORD, and pass it to the container in the "
                + "compose, such as PLEX_CLAIM: ${PLEX_CLAIM_TOKEN}. For a secret nobody needs to know, use generate in save_custom_app instead.",
                async Task<object?> ([Description(AppHelp)] string app,
                    [Description("The environment variable to save it as.")] string name,
                    [Description("What the value is and where the owner finds it, in a sentence or two.")] string description,
                    AIFunctionArguments args, CancellationToken ct) =>
                    await RequestSecret(actor, app, name, description, args, ct)),
            Tool(ToolTier.Change, "save_custom_app",
                "Creates or changes a custom app: the owner's own docker compose. Change a saved compose with edits instead of resending it. "
                + "Environment values you don't mention are kept. Never write a password or key yourself: list its variable in generate.",
                ([Description("The app's name: lowercase letters, digits and dashes.")] string app,
                    [Description("The whole compose YAML, for a new app or a rewrite.")] string? compose = null,
                    [Description("Find/replace edits to the saved compose, applied in order; each find must occur exactly once.")] TextEdit[]? edits = null,
                    [Description("Environment variables to set, name → value.")] Dictionary<string, string>? setEnv = null,
                    [Description("Environment variable names to remove.")] string[]? removeEnv = null,
                    [Description("Environment variable names to fill with a random secret nobody sees; a strong saved value is kept.")] string[]? generate = null,
                    [Description("The server for a new app. Use move_app to move one.")] string? node = null,
                    [Description("Placement requirements, such as gpu=nvidia. Replaces the saved list.")] string[]? require = null,
                    [Description("A LAN address of the app's own, or empty text to drop it.")] string? address = null,
                    [Description("The app's web addresses. Replaces the saved list.")] AppRoute[]? routes = null,
                    CancellationToken ct = default) =>
                    SaveCustom(actor, app, compose, edits, setEnv, removeEnv, generate, node, require, address, routes, ct)),
            Tool(ToolTier.Change, "install_catalog_app",
                "Installs a catalog app, or changes the settings of an installed one. Secrets are typed by the owner on the Apps page, never here.",
                ([Description("The catalog app's id, from list_catalog.")] string app,
                    [Description("The installed app's name; the catalog id by default.")] string? name = null,
                    [Description("The server for a new install. Use move_app to move one.")] string? node = null,
                    [Description("Setting id → value; settings you leave out are kept.")] Dictionary<string, string>? settings = null,
                    [Description("A LAN address of the app's own, or empty text to drop it.")] string? address = null,
                    CancellationToken ct = default) =>
                    InstallCatalog(actor, app, name, node, settings, address, ct)),
            Tool(ToolTier.Change, "app_action",
                "Starts, stops, restarts or updates an app (update pulls its images again), or cancels its move or restore. "
                    + "Private images pull only after an owner adds the registry's sign-in in Settings → Registries.",
                async Task<object?> ([Description(AppHelp)] string app,
                    [Description("start, stop, restart, update, cancel-move or cancel-restore.")] string action, CancellationToken ct) =>
                    await stacks.Act(App(app), action, actor, ct)),
            Tool(ToolTier.Change, "move_app", "Moves an app and its data to another server. It stops while the data copies.",
                async Task<object?> ([Description(AppHelp)] string app, [Description("The server to move it to.")] string node, CancellationToken ct) =>
                    await stacks.Move(App(app), new MoveStackRequest((await Node(node, ct)).Hostname), actor, ct)),
            Tool(ToolTier.Change, "run_backup", "Backs an app up now.",
                async Task<object?> ([Description(AppHelp)] string app, CancellationToken ct) => await stacks.RunBackup(App(app), actor, ct)),
            Tool(ToolTier.Change, "upgrade_app_images", "Pins an app's images to their newest versions and redeploys it.",
                async Task<object?> ([Description(AppHelp)] string app,
                    [Description("Allow major version jumps, which may need a migration.")] bool major = false, CancellationToken ct = default) =>
                    await stacks.UpgradeImages(App(app), new UpgradeImagesRequest(major), actor, ct)),
            Tool(ToolTier.Change, "check_node_updates", "Asks a server to check for OS package updates now.",
                async Task<object?> ([Description(NodeHelp)] string node, CancellationToken ct) =>
                    await requests.Act((await Node(node, ct)).Id, "check-updates", ct)),
            Tool(ToolTier.Destructive, "delete_app",
                "Deletes an app: Lucia stops it and removes it from its server. Its data directory stays on the server.",
                async Task<object?> ([Description(AppHelp)] string app, CancellationToken ct) =>
                {
                    var name = App(app);
                    await stacks.Delete(name, ct);
                    return $"Deleted {name}. Its data directory stays on the server.";
                }),
            Tool(ToolTier.Destructive, "restore_backup", "Replaces an app's data with a snapshot from list_backups. Its current data is lost.",
                async Task<object?> ([Description(AppHelp)] string app, [Description("The snapshot's id, from list_backups.")] string snapshot,
                    CancellationToken ct) => await stacks.Restore(App(app), new RestoreStackRequest(snapshot), actor, ct)),
            Tool(ToolTier.Destructive, "set_public_route",
                "Publishes one of an app's web addresses on the internet under a public name, or takes it off the internet.",
                async Task<object?> ([Description(AppHelp)] string app, [Description("The web address's host name, from get_app.")] string host,
                    [Description("The public name to publish it as; leave it out to take it off the internet.")] string? publicName = null,
                    CancellationToken ct = default) =>
                    await stacks.SaveStackPublic(App(app), new StackPublicRequest(host, publicName), actor, ct)),
            Tool(ToolTier.Destructive, "node_action", "Installs a server's OS updates, restarts it, or updates its Lucia agent.",
                async Task<object?> ([Description(NodeHelp)] string node, [Description("install-updates, restart or update-agent.")] string action,
                    CancellationToken ct) =>
                {
                    if (action is not ("install-updates" or "restart" or "update-agent"))
                        throw new AssistantException(400, "unknown_action", action == "check-updates"
                            ? "Use check_node_updates to check for updates." : "Use install-updates, restart or update-agent.");
                    return await requests.Act((await Node(node, ct)).Id, action, ct);
                }),
            Tool(ToolTier.Destructive, "run_command",
                "Runs a command or script as root on a managed server, through Lucia's agent, and returns its exit code and the end of its "
                + "output (stdout and stderr together). Nothing is interactive: stdin is empty, so pass -y and the like. Anything it starts in "
                + "the background stops when it ends; use systemctl or docker for what should keep running. Prefer the other tools when one "
                + "does the job, and read before you change. Needs canRunCommands from get_node.",
                async Task<object?> ([Description(NodeHelp)] string node,
                    [Description("The command, or a whole multi-line script.")] string command,
                    [Description("bash, sh or python3.")] string interpreter = "bash",
                    [Description("Stop it after this many seconds, 1 to 1800.")] int timeoutSeconds = 120, CancellationToken ct = default) =>
                    await RunCommand(node, command, interpreter, timeoutSeconds, ct)),
        ];
    }

    private AssistantTool Tool(string tier, string name, string description, Delegate body) =>
        new(AIFunctionFactory.Create(body, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            MarshalResult = static (result, _, _) => new ValueTask<object?>(result),
        }), tier, Redact, logger);

    /// <summary>
    /// What a destructive call puts at risk, for its approval card; the card's header already names the call. Names come
    /// from the model, so only short plain ones are repeated.
    /// </summary>
    internal static string Warning(string tool, JsonNode? input)
    {
        string? Text(string field) => input is JsonObject fields && fields[field] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
        string Name(string field, string fallback) => Text(field) is { Length: > 0 and <= 100 } text
            && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? text : fallback;
        string app = Name("app", "this app"), node = Name("node", "this server");
        return (tool, tool == "node_action" ? Text("action") : null) switch
        {
            ("delete_app", _) => $"Lucia stops {app} and removes it from its server. Its data directory stays there.",
            ("restore_backup", _) => $"The backup replaces {app}'s data. Anything changed since it was taken is lost.",
            ("set_public_route", _) when !string.IsNullOrEmpty(Text("publicName")) =>
                $"Anyone on the internet will be able to reach {Name("publicName", "this address")}.",
            ("set_public_route", _) => $"Anyone using {Name("host", "this address")} from outside your network loses access.",
            ("node_action", "install-updates") => $"Apps on {node} may restart briefly while the updates install.",
            ("node_action", "restart") => $"Every app on {node} is offline for a few minutes while it restarts.",
            ("node_action", "update-agent") => $"Lucia loses touch with {node} for about a minute while its agent updates.",
            ("run_command", _) => $"This runs the command below as root on {node}. It can change anything there.",
            _ => "This can remove data or interrupt your lab.",
        };
    }

    private async Task<object?> SaveCustom(string actor, string app, string? compose, TextEdit[]? edits, Dictionary<string, string>? setEnv,
        string[]? removeEnv, string[]? generate, string? node, string[]? require, string? address, AppRoute[]? routes, CancellationToken ct)
    {
        var name = App(app);
        var existing = await stacks.Definition(name, ct);
        if (existing?.Stack.Manifest.Template is { } template)
            throw new AssistantException(409, "catalog_app", $"{name} is the catalog app {template.Id}. Change it with install_catalog_app.");
        if (compose is not null && edits is { Length: > 0 })
            throw new AssistantException(400, "compose_and_edits", "Send the whole compose or edits to it, not both.");
        Plain(compose);
        foreach (var edit in edits ?? []) { Plain(edit.Find); Plain(edit.Replace); }
        if (setEnv is not null) foreach (var value in setEnv.Values) Plain(value);
        var next = compose ?? existing?.Stack.Compose ?? throw new AssistantException(400, "compose_required", "A new app needs its whole compose.");
        foreach (var edit in edits ?? []) next = Apply(next, edit);
        var (env, generated) = EditEnv(existing?.Env ?? "", setEnv, removeEnv, generate);
        var hostname = node is null ? null : (await Node(node, ct)).Hostname;
        var manifest = existing?.Stack.Manifest;
        // A route's public name only changes through set_public_route, which always asks the owner.
        var saved = await stacks.Save(name, new SaveStackRequest(next, env, new StackManifest(1,
            new StackPlacement(hostname ?? manifest?.Placement.Node, require ?? manifest?.Placement.Require), null, null, address,
            routes?.Select(route => new StackRoute(route.Host, route.Port, route.GrpcPort,
                manifest?.Routes?.FirstOrDefault(item => item.Host == route.Host)?.Public)).ToArray()),
            existing?.Stack.Revision ?? 0), actor, ct);
        return new { Saved = saved, Generated = generated.Length > 0 ? generated : null, Note = Delivered };
    }

    private async Task<object?> InstallCatalog(string actor, string app, string? name, string? node, Dictionary<string, string>? settings,
        string? address, CancellationToken ct)
    {
        var entry = StackCatalog.Find(App(app));
        var target = App(name ?? entry.Id);
        var existing = await stacks.Definition(target, ct);
        var template = existing?.Stack.Manifest.Template;
        if (existing is not null && template?.Id != entry.Id)
            throw new AssistantException(409, "name_taken", template is null
                ? $"{target} is already a custom app. Pick another name."
                : $"{target} is already the catalog app {template.Id}. Pick another name.");
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (template?.Settings is { } previous)
            foreach (var (key, value) in previous)
                if (entry.Fields.Any(field => field.Id == key)) merged[key] = value;
        foreach (var (key, value) in Typed(entry, settings)) merged[key] = value;
        var env = StackCatalog.ReadEnv(existing?.Env ?? "");
        var missing = entry.Fields.Where(field => field is { Kind: "secret", Optional: false } && Shown(entry, field, merged)
            && !env.ContainsKey(StackCatalog.SecretKey(field))).Select(field => field.Label).ToArray();
        if (missing.Length > 0)
            throw new AssistantException(409, "owner_secret_needed",
                $"{entry.Name} needs the owner to type {string.Join(" and ", missing)} on the Apps page in the console. Tell them; don't ask for it in chat.");
        var hostname = node is null ? null : (await Node(node, ct)).Hostname;
        var placement = existing?.Stack.Manifest.Placement;
        var saved = await stacks.Save(target, new SaveStackRequest(null, null, new StackManifest(1,
            new StackPlacement(hostname ?? placement?.Node, placement?.Require), new StackTemplate(entry.Id, entry.Version, merged),
            null, address, null), existing?.Stack.Revision ?? 0), actor, ct);
        return new { Saved = saved, Note = Delivered };
    }

    /// <summary>
    /// Saves a value the owner types on the chat's secret card into a custom app's environment. The model only learns that
    /// it was saved: the name must read as a secret, so get_app hides it and every later result is scrubbed of it.
    /// </summary>
    private async Task<object?> RequestSecret(string actor, string app, string name, string description, AIFunctionArguments args,
        CancellationToken ct)
    {
        var (session, call) = Call(args);
        var target = App(app);
        if (name is null || !EnvName().IsMatch(name))
            throw new AssistantException(400, "invalid_env_name", $"\"{Short(name ?? "")}\" isn't an environment variable name.");
        if (!IsSecret(name))
            throw new AssistantException(400, "not_a_secret_name",
                $"Lucia only hides variables named as secrets. Save it as {name}_TOKEN or {name}_PASSWORD, and pass that to the container in the compose.");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 500)
            throw new AssistantException(400, "invalid_description", "Describe the value in up to 500 characters.");
        await Custom(target, ct);
        if (await broker.AskAsync(session, call, "secret", ct) is not { } value)
            return $"The owner chose not to give {name}. Nothing was saved.";
        // The app may have changed while the owner looked for the value.
        var (stack, env) = await Custom(target, ct);
        await stacks.Save(target, new SaveStackRequest(stack.Compose, EditEnv(env, new Dictionary<string, string> { [name] = value }, null, null).Env,
            stack.Manifest, stack.Revision), actor, ct);
        return new { Saved = true, App = target, Name = name, Note = Delivered };
    }

    private async Task<(StoredStack Stack, string Env)> Custom(string name, CancellationToken ct) => await stacks.Definition(name, ct) switch
    {
        null => throw new AssistantException(404, "unknown_app", $"There's no app named {name}. Create it with save_custom_app first."),
        { Stack.Manifest.Template: { } template } => throw new AssistantException(409, "catalog_app",
            $"{name} is the catalog app {template.Id}: the owner types its secrets on the Apps page."),
        { } found => found,
    };

    /// <summary>The chat and tool call a question belongs to. Only Copilot's runtime supplies them, so questions work only in a chat.</summary>
    private static (string SessionKey, string ToolCallId) Call(AIFunctionArguments args) =>
        args.Context?.TryGetValue(typeof(GitHub.Copilot.ToolInvocation), out var value) == true
        && value is GitHub.Copilot.ToolInvocation { SessionId: { Length: > 0 } session, ToolCallId: { Length: > 0 } call }
            ? (session, call)
            : throw new AssistantException(400, "chat_only", "This tool only works in a chat.");

    /// <summary>Settings the model may set: anything but secrets, which the owner types, and Lucia's own hidden values.</summary>
    private static Dictionary<string, string> Typed(CatalogApp app, Dictionary<string, string>? settings)
    {
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings is null) return typed;
        foreach (var (key, value) in settings)
        {
            if (app.Fields.FirstOrDefault(field => field.Id == key) is { Kind: "secret" or "hidden" })
                throw new AssistantException(400, "owner_only_setting", $"Only the owner sets {Short(key)}, on the Apps page in the console.");
            typed[key] = value;
        }
        return typed;
    }

    private static bool Shown(CatalogApp app, CatalogField field, IReadOnlyDictionary<string, string> settings)
    {
        if (field.When?.Split('=', 2) is not [var id, var expected]) return true;
        return (settings.TryGetValue(id, out var value) ? value : app.Fields.FirstOrDefault(item => item.Id == id)?.Default) == expected;
    }

    private static string Apply(string text, TextEdit edit)
    {
        if (string.IsNullOrEmpty(edit.Find)) throw new AssistantException(400, "invalid_edit", "Each edit needs text to find.");
        var at = text.IndexOf(edit.Find, StringComparison.Ordinal);
        if (at < 0)
            throw new AssistantException(400, "edit_not_found", $"The compose has no \"{Short(edit.Find)}\". Read it again with get_app.");
        if (text.IndexOf(edit.Find, at + 1, StringComparison.Ordinal) >= 0)
            throw new AssistantException(400, "edit_not_unique", $"\"{Short(edit.Find)}\" occurs more than once; include more of the text around it.");
        return text[..at] + (edit.Replace ?? "") + text[(at + edit.Find.Length)..];
    }

    /// <summary>Refuses text that carries a value Lucia hid from the model, so a hidden secret is never saved over the real one.</summary>
    private static void Plain(string? text)
    {
        if (text is not null && (text.Contains("[redacted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("[hidden]", StringComparison.Ordinal) || text.Contains(Masked, StringComparison.Ordinal)))
            throw new AssistantException(400, "redacted_text",
                "That text holds a value Lucia hid from you. Change the compose with edits that leave hidden values alone, and use generate for new secrets.");
    }

    /// <summary>Sets, removes and generates environment variables, keeping the file's comments and order.</summary>
    internal static (string Env, string[] Generated) EditEnv(string env, IReadOnlyDictionary<string, string>? set,
        IReadOnlyCollection<string>? remove, IReadOnlyCollection<string>? generate)
    {
        set ??= new Dictionary<string, string>();
        remove ??= [];
        generate ??= [];
        if (set.Count == 0 && remove.Count == 0 && generate.Count == 0) return (env, []);
        var named = set.Keys.Concat(remove.Distinct()).Concat(generate.Distinct()).ToList();
        foreach (var key in named)
            if (key is null || !EnvName().IsMatch(key))
                throw new AssistantException(400, "invalid_env_name", $"\"{Short(key ?? "")}\" isn't an environment variable name.");
        if (set.Values.Any(value => value is null || value.Contains('\r') || value.Contains('\n')))
            throw new AssistantException(400, "invalid_env_value", "Each environment value is one line of text.");
        if (named.GroupBy(key => key, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new AssistantException(400, "env_conflict", $"{twice.Key} is named twice across setEnv, removeEnv and generate; pick one.");
        var current = StackCatalog.ReadEnv(env);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in set) values[key] = value;
        foreach (var key in generate) values[key] = StackCatalog.Secret(current, key);
        var removed = remove.ToHashSet(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var line in env.Length == 0 ? Array.Empty<string>() : env.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
        {
            var at = line.IndexOf('=');
            var key = at > 0 && !line.TrimStart().StartsWith('#') ? line[..at].Trim() : null;
            if (key is null) lines.Add(line);
            else if (removed.Contains(key)) continue;
            else if (!values.TryGetValue(key, out var value)) lines.Add(line);
            else if (written.Add(key)) lines.Add($"{key}={value}");
        }
        foreach (var (key, value) in values)
            if (written.Add(key)) lines.Add($"{key}={value}");
        return (lines.Count == 0 ? "" : string.Join('\n', lines) + "\n", [.. generate.Distinct()]);
    }

    private async Task<object?> RunCommand(string node, string command, string interpreter, int seconds, CancellationToken ct)
    {
        var (id, hostname) = await Node(node, ct);
        if ((await nodes.Facts(ct)).FirstOrDefault(item => item.NodeId == id)?.Status?.Features?.Contains("exec") != true)
            throw new AssistantException(409, "exec_unsupported",
                $"{hostname}'s agent can't run commands yet. Update it with node_action update-agent, then try again.");
        var job = Guid.NewGuid();
        // The node stops the command at its limit; this only bounds the wait if the node goes quiet.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, NodeRequests.MaxCommandSeconds) + 60);
        try
        {
            var result = await requests.Exec(id, job, command, interpreter, seconds, ct);
            while (result is { Success: true, Running: true } && DateTimeOffset.UtcNow < deadline)
            {
                try { result = await requests.Follow(id, job, stop: false, ct); }
                catch (HardwareOnboardingException ex) when (ex.StatusCode == 504) { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            }
            if (!result.Success) throw new AssistantException(409, "command_refused", result.Message ?? $"{hostname} refused the command.");
            return new
            {
                Node = hostname,
                result.ExitCode,
                Running = result.Running ? true : (bool?)null,
                Note = result.Running ? $"{hostname} stopped answering, so Lucia stopped waiting. The command stops by itself at its time limit."
                    : result.Message,
                Output = string.IsNullOrEmpty(result.Output) ? "(no output)" : result.Output,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await requests.Follow(id, job, stop: true, brief.Token); }
            catch (Exception ex) when (ex is HardwareOnboardingException or OperationCanceledException) { }
            throw;
        }
    }

    private async Task<(Guid Id, string Hostname)> Node(string text, CancellationToken ct)
    {
        var wanted = (text ?? "").Trim().TrimEnd('.');
        var names = await nodes.Names(ct);
        if (Guid.TryParse(wanted, out var id))
            foreach (var item in names)
                if (item.NodeId == id) return item;
        if (IPAddress.TryParse(wanted, out var ip) && ip.ToString() == wanted)
            foreach (var address in await nodes.Addresses(ct))
                if (address.Address?.ToString() == wanted)
                    foreach (var item in names)
                        if (string.Equals(item.Hostname, address.Hostname, StringComparison.OrdinalIgnoreCase)) return item;
        foreach (var candidate in new[] { wanted, wanted.Split('.')[0] })
            foreach (var item in names)
                if (string.Equals(item.Hostname, candidate, StringComparison.OrdinalIgnoreCase)) return item;
        throw Unknown(text ?? "");
    }

    private static AssistantException Unknown(string text) =>
        new(404, "unknown_node", $"No managed server is called \"{Short(text)}\". Use list_nodes to see them.");

    private async Task<JsonArray> NodeRows(CancellationToken ct)
    {
        var snapshot = await nodes.Snapshot(await ManagedNodeDns.ActiveNaming(domains, ct), ct);
        var rows = JsonSerializer.SerializeToNode(snapshot, AssistantStream.Json) as JsonArray ?? new JsonArray();
        foreach (var row in rows.OfType<JsonObject>())
        {
            row.Remove("history");
            row.Remove("taskId");
            row["canRunCommands"] = row["status"]?["features"] is JsonArray features && features.Any(feature => Text(feature) == "exec");
        }
        return rows;
    }

    private static Dictionary<string, UniFiClient> ByMac(IEnumerable<UniFiClient> clients) => clients
        .Where(client => !string.IsNullOrEmpty(client.Mac))
        .GroupBy(client => client.Mac.ToLowerInvariant()).ToDictionary(group => group.Key, group => group.First());

    private static bool Matches(string domain, string host) =>
        string.Equals(domain, host, StringComparison.OrdinalIgnoreCase)
        || domain.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(domain[1..], StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static string App(string? name) => (name ?? "").Trim().ToLowerInvariant();

    private static string Short(string text) => text.Length <= 64 ? text : text[..64] + "…";

    private async Task<string> Redact(string text, CancellationToken ct)
    {
        try { return Scrub(text, await stacks.Secrets(ct)); }
        catch (RegexMatchTimeoutException) { return "[Lucia couldn't check this output for secrets in time, so it was withheld. Ask for less.]"; }
    }

    /// <summary>Whether a variable or field name holds a secret.</summary>
    public static bool IsSecret(string key) => SecretName().IsMatch(key) || SecretFields.Contains(key);

    /// <summary>
    /// Hides every app's secret values wherever they appear, raw or JSON-escaped, then secret-looking assignments, URL
    /// credentials, tokens and whatever AGT's credential redactor finds.
    /// </summary>
    public static string Scrub(string text, IEnumerable<KeyValuePair<string, string>> env)
    {
        foreach (var value in env.Select(pair => (pair.Key, Value: Unquote(pair.Value ?? ""))).Where(pair => Strong(pair.Key, pair.Value))
            .Select(pair => pair.Value).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
        {
            text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
            var encoded = JsonEncodedText.Encode(value).Value;
            if (encoded != value) text = text.Replace(encoded, "[redacted]", StringComparison.Ordinal);
        }
        return Patterns(text);
    }

    /// <summary>Redacts secret-looking text with no list of values to go by.</summary>
    public static string Patterns(string text)
    {
        text = UrlCredentials().Replace(text, "$1[redacted]$3");
        text = Jwt().Replace(text, "[redacted]");
        text = SecretLine().Replace(text, "${key}${sep}[redacted]");
        return Redactor.Redact(text).Sanitized;
    }

    /// <summary>A tool call's arguments as the owner sees them: secret fields masked and secret-looking text redacted.</summary>
    public static JsonNode? Mask(JsonNode? node, bool secret = false) => node switch
    {
        null => null,
        JsonObject value => new JsonObject(value.Select(pair => KeyValuePair.Create(pair.Key, Mask(pair.Value, secret || IsSecret(pair.Key))))),
        JsonArray value => new JsonArray(value.Select(item => Mask(item, secret)).ToArray()),
        JsonValue value when value.TryGetValue(out string? text) => secret && text.Length > 0 ? (JsonNode?)Masked : MaskText(text),
        _ => node.DeepClone(),
    };

    private static string MaskText(string text)
    {
        try { return Patterns(text); }
        catch (RegexMatchTimeoutException) { return Masked; }
    }

    /// <summary>Values worth hiding: long random tokens, or values of secret-named variables that aren't trivially short.</summary>
    private static bool Strong(string key, string value)
    {
        if (value.Length >= 32 && Token().IsMatch(value) && !Guid.TryParse(value, out _)) return true;
        if (!IsSecret(key) || value.Length < 8) return false;
        if (value.Length >= 12) return true;
        var classes = (value.Any(char.IsLower) ? 1 : 0) + (value.Any(char.IsUpper) ? 1 : 0) + (value.Any(char.IsDigit) ? 1 : 0)
            + (value.Any(item => !char.IsLetterOrDigit(item)) ? 1 : 0);
        return classes >= 3;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;

    [GeneratedRegex(@"(?i)(pass|secret|token|private|credential|salt|cookie|api[_-]?key|[_-]key|authorization|pwd)", RegexOptions.None, 2000)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.None, 2000)]
    private static partial Regex EnvName();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{32,}\z", RegexOptions.None, 2000)]
    private static partial Regex Token();

    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.-]{0,30}://[^\s:/@""']{1,256}:)([^\s@/""']{1,256})(@)", RegexOptions.None, 2000)]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.None, 2000)]
    private static partial Regex Jwt();

    // key, then = or : with optional (escaped) quotes, then a value that isn't a ${reference}, structure, flag, number, path or URL.
    [GeneratedRegex("""(?i)\b(?<key>[\w.-]{0,40}(?:pass|secret|token|private|credential|salt|cookie|api[_-]?key|[_-]key|authorization|pwd)[\w.-]{0,40})(?<sep>\\?["']?[ \t]*[:=][ \t]*\\?["']?)(?!\$\{|[\[{]|(?:true|false|yes|no|null|none|\d{1,6})\b|/|[a-z][a-z0-9+.-]{0,30}://)(?<value>(?<=["'])[^"'\\\r\n]+|(?:(?:bearer|basic|token)[ \t]+)?[^\s"',;}&)\]\\]+)""", RegexOptions.None, 2000)]
    private static partial Regex SecretLine();
}
