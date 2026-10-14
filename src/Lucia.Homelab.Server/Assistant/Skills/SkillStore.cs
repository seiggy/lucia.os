#pragma warning disable MAAI001 // The Agent Framework marks its skills API evaluation-only; it is the API this feature builds on.
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lucia.Homelab.Server.Domains;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Assistant.Skills;

/// <summary>
/// The owner's skill packages: one folder per skill under the assistant's <c>skills</c> directory in the agentskills.io layout
/// (<c>SKILL.md</c>, <c>scripts/</c>, <c>references/</c>, <c>assets/</c>), plus <c>index.json</c> for what isn't part of a package.
/// Whether a skill loads is decided by the Agent Framework's file source, not by Lucia. Every write bumps <see cref="Revision"/>,
/// so the next turn sees it.
/// </summary>
public sealed partial class SkillStore(IOptions<AssistantOptions> options, ILogger<SkillStore> logger, Func<Uri, CancellationToken, Task<byte[]>>? fetch = null, string? builtInRoot = null)
{
    public const string BuiltInSource = "built-in";
    public const int MaxSkills = 200, MaxFiles = 200, MaxFileBytes = 1024 * 1024, MaxSkillBytes = 5 * 1024 * 1024, MaxEditableBytes = 256 * 1024,
        MaxZipBytes = 5 * 1024 * 1024, MaxDomains = 50;
    private const string Manifest = "SKILL.md";
    private const int MaxDepth = 5, MaxGitHubRequests = 250;
    private static readonly string[] Folders = ["scripts", "references", "assets"];
    private static readonly string[] ResourceExtensions = [".md", ".json", ".yaml", ".yml", ".txt", ".csv", ".tmpl", ".template", ".conf", ".ini", ".toml"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Inspection> _inspections = new();
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _fetch = fetch ?? ((uri, ct) => AssistantWeb.Fetch(uri, 2 * 1024 * 1024, ct));
    private SkillIndex? _index;
    private long _revision;
    private (long Revision, Task<IReadOnlyDictionary<string, AgentSkillFrontmatter>> Loaded)? _loaded;

    private sealed record Inspection(SkillFile[] Files, string Hash, string? Problem, long Bytes, long Revision = -1);

    /// <summary>Counts every change to a skill or to what is enabled; the provider cache is keyed on it.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    public event Action? Changed;

    public string Root => Path.Combine(Path.GetFullPath(options.Value.Directory), "skills");

    private string IndexPath => Path.Combine(Root, "index.json");

    private static readonly string DefaultBuiltIn = Path.Combine(AppContext.BaseDirectory, "Assistant", "Skills", "BuiltIn");

    /// <summary>The read-only skills that ship with Lucia, in the same package layout as the owner's.</summary>
    public string BuiltInRoot => builtInRoot ?? DefaultBuiltIn;

    private readonly Lazy<IReadOnlySet<string>> _builtIn = new(() => Directory.Exists(builtInRoot ?? DefaultBuiltIn)
        ? Directory.EnumerateDirectories(builtInRoot ?? DefaultBuiltIn)
            .Select(Path.GetFileName).OfType<string>().Where(ValidName).ToHashSet(StringComparer.Ordinal)
        : new HashSet<string>());

    /// <summary>Names of the built-in skills. They are reserved: an owner's skill can't take one.</summary>
    public IReadOnlySet<string> BuiltInNames => _builtIn.Value;

    public bool IsBuiltIn(string? name) => name is not null && BuiltInNames.Contains(name);

    /// <summary>The folders the Agent Framework reads skills from: the built-in ones, then the owner's.</summary>
    public IReadOnlyList<string> SourceRoots => [.. (BuiltInNames.Count > 0 ? new[] { BuiltInRoot, Root } : [Root]).Where(Directory.Exists)];

    private string Dir(string name) => Path.Combine(IsBuiltIn(name) ? BuiltInRoot : Root, name);

    public static bool ValidName(string? name) => name is { Length: > 0 and <= 64 } && NamePattern().IsMatch(name);

    [GeneratedRegex(@"\A[a-z0-9]+(?:-[a-z0-9]+)*\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"\A(?:\*\.)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}\z")]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,100}\z")]
    private static partial Regex GitHubName();

    /// <summary>The file options the Agent Framework's file source reads skills with.</summary>
    public static AgentFileSkillsSourceOptions FileOptions() => new()
    {
        AllowedScriptExtensions = SkillLanguages.ScriptExtensions,
        AllowedResourceExtensions = ResourceExtensions,
    };

    /// <summary>The Agent Framework's source over every skill folder; the provider adds the runner, the filter and the cache.</summary>
    public AgentFileSkillsSource CreateSource(AgentFileSkillScriptRunner? runner) => new(SourceRoots, runner, FileOptions());

    // ---- reading ----

    public async Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken ct)
    {
        var loaded = await LoadedAsync(ct);
        var owned = Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>().Where(name => ValidName(name) && !IsBuiltIn(name)).Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        return [.. BuiltInNames.Order(StringComparer.Ordinal).Concat(owned.Take(MaxSkills)).Select(name => Summarize(name, loaded))];
    }

    public async Task<SkillDetail> GetAsync(string name, CancellationToken ct)
    {
        Require(name);
        var summary = Summarize(name, await LoadedAsync(ct));
        var markdown = ReadMarkdown(name, out var raw);
        return new(summary, markdown?.Settings().AllowedDomains ?? [], markdown?.Text("license"), markdown?.Text("compatibility"),
            markdown?.Body ?? raw ?? "", Inspect(name).Files)
        {
            Network = markdown?.Settings().Network ?? SkillNetwork.None,
            ParametersSchema = SchemasOf(name),
        };
    }

    public async Task<SkillSummary?> FindAsync(string name, CancellationToken ct) =>
        ValidName(name) && Directory.Exists(Dir(name)) ? Summarize(name, await LoadedAsync(ct)) : null;

    /// <summary>Names of the skills that are enabled and that the Agent Framework can load: the ones the model may be offered.</summary>
    public async Task<IReadOnlyList<string>> OfferedAsync(CancellationToken ct) =>
        [.. (await ListAsync(ct)).Where(skill => skill.Enabled && skill.Problem is null).Select(skill => skill.Name)];

    /// <summary>The skill's Lucia settings from its frontmatter; the defaults when it can't be read.</summary>
    public SkillSettings SettingsOf(string name) =>
        ValidName(name) && Directory.Exists(Dir(name)) ? ReadMarkdown(name, out _)?.Settings() ?? SkillSettings.Default : SkillSettings.Default;

    /// <summary>
    /// The schema of the <c>arguments</c> each of the skill's scripts takes, by script path, as the model is told: what the skill declares,
    /// or the default for its execution mode.
    /// </summary>
    public IReadOnlyDictionary<string, JsonObject> SchemasOf(string name)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (!ValidName(name) || !Directory.Exists(Dir(name))) return result;
        var markdown = ReadMarkdown(name, out _);
        var execution = markdown?.Settings().Execution ?? SkillExecution.Node;
        var declared = SkillSchemas.Read(markdown, out _);
        foreach (var file in Inspect(name).Files.Where(file => file.Kind == "script" && SkillLanguages.IsScript(file.Path)))
            result[file.Path] = SkillSchemas.For(execution, declared, file.Path);
        return result;
    }

    /// <summary>The schema for one script of a skill; the execution mode's default when the skill declares none.</summary>
    public JsonObject SchemaOf(string name, string script)
    {
        var markdown = ValidName(name) && Directory.Exists(Dir(name)) ? ReadMarkdown(name, out _) : null;
        return SkillSchemas.For(markdown?.Settings().Execution ?? SkillExecution.Node, SkillSchemas.Read(markdown, out _), script);
    }

    /// <summary>The skill's content hash, or null when there is no such skill.</summary>
    public string? HashOf(string name) => ValidName(name) && Directory.Exists(Dir(name)) ? Inspect(name).Hash : null;

    /// <summary>The skill's folder and the files to ship to a node: SKILL.md and everything Lucia manages.</summary>
    public (string Folder, IReadOnlyList<string> Paths) PackList(string name)
    {
        Require(name);
        return (Dir(name), [Manifest, .. Inspect(name).Files.Select(file => file.Path)]);
    }

    public SkillFileContent ReadFile(string name, string? path)
    {
        Require(name);
        var relative = Normalize(path);
        var file = Inspect(name).Files.FirstOrDefault(item => item.Path == relative) ?? throw FileNotFound();
        var content = file.Editable ? File.ReadAllText(Resolve(name, relative), Encoding.UTF8) : "";
        return new(file.Path, content, file.Language, file.Size, file.Editable);
    }

    // ---- writing ----

    public async Task<SkillDetail> CreateAsync(SkillCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (!ValidName(name)) throw Invalid("Name a skill with lowercase letters, digits and single hyphens, up to 64 characters.");
        RequireUnreserved(name);
        var settings = Settings(request.Execution, request.Tier, request.AllowedDomains, request.Network);
        var description = Description(request.Description);
        var body = request.Body is { Length: > 0 } text ? Limit(text) : $"# {name}\n\nSay when to use this skill and what to do, step by step.\n";
        await Locked(async () =>
        {
            if (Directory.Exists(Dir(name))) throw new AssistantException(409, "skill_exists", $"There is already a skill named {name}.");
            RequireRoom();
            Directory.CreateDirectory(Root);
            DomainOnboardingStore.EnsureDirectory(Dir(name));
            File.WriteAllText(Path.Combine(Dir(name), Manifest), SkillMarkdown.Write(name, description, settings, body, null), new UTF8Encoding(false));
            await Touch(name, entry => new(false, "created", DateTimeOffset.UtcNow, ""), ct);
        }, ct);
        return await GetAsync(name, ct);
    }

    public async Task<SkillDetail> UpdateAsync(string name, SkillUpdateRequest request, CancellationToken ct)
    {
            RequireOwned(name);
        var settings = Settings(request.Execution, request.Tier, request.AllowedDomains, request.Network ?? SettingsOf(name).Network);
        var description = Description(request.Description);
        var body = Limit(request.Body ?? "");
        await Locked(async () =>
        {
            if (settings.Execution == SkillExecution.Sandbox) RequireSandboxLanguages(Inspect(name).Files.Select(file => file.Path));
            var previous = ReadMarkdown(name, out _);
            WriteText(Path.Combine(Dir(name), Manifest), SkillMarkdown.Write(name, description, settings, body, previous, OwnerEdit(name)));
            await Touch(name, entry => entry, ct);
        }, ct);
        return await GetAsync(name, ct);
    }

    public async Task<SkillSummary> SetEnabledAsync(string name, bool enabled, CancellationToken ct, string? hash = null)
    {
            RequireOwned(name);
        if (enabled && Summarize(name, await LoadedAsync(ct)).Problem is { } problem)
            throw new AssistantException(409, "skill_invalid", "This skill can't be enabled: " + problem);
        await Locked(async () =>
        {
            // Enabling is the owner's review: after it the assistant can never change this skill again.
            var reviewed = enabled && AssistantAuthored(name);
            // The owner enables the version they read: a draft that changed since then must be reviewed again.
            if (reviewed && (hash is null || !string.Equals(Inspect(name).Hash, hash, StringComparison.OrdinalIgnoreCase)))
                throw new AssistantException(409, "skill_changed", hash is null ? "Send the hash of the version you reviewed to enable this skill." : "The skill changed since you reviewed it. Review it again.");
            if (reviewed) MarkOwnerEdit(name);
            await Touch(name, entry => entry with { Enabled = enabled }, ct, rehash: reviewed);
        }, ct);
        return Summarize(name, await LoadedAsync(ct));
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
            RequireOwned(name);
        await Locked(async () =>
        {
            DomainOnboardingStore.RejectLinks(Dir(name));
            Directory.Delete(Dir(name), recursive: true);
            (await Index(ct)).Skills.Remove(name);
            await SaveIndex(ct);
        }, ct);
    }

    public async Task<SkillFile> WriteFileAsync(string name, string? path, string? content, CancellationToken ct)
    {
            RequireOwned(name);
        var relative = Normalize(path);
        if (content is null) throw Invalid("Send the file's content.");
        var bytes = new UTF8Encoding(false).GetBytes(content);
        if (bytes.Length > MaxFileBytes) throw new AssistantException(413, "file_too_large", "Skill files are at most 1 MiB.");
        await Locked(async () =>
        {
            var current = Inspect(name);
            var existing = current.Files.FirstOrDefault(file => file.Path == relative);
            if (existing is null && current.Files.Length >= MaxFiles) throw new AssistantException(409, "too_many_files", $"A skill holds at most {MaxFiles} files.");
            if (current.Bytes - (existing?.Size ?? 0) + bytes.Length > MaxSkillBytes)
                throw new AssistantException(413, "skill_too_large", "A skill is at most 5 MiB.");
            if (SettingsOf(name).Execution == SkillExecution.Sandbox) RequireSandboxLanguages([relative]);
            WriteBytes(Resolve(name, relative), bytes);
            MarkOwnerEdit(name);
            await Touch(name, entry => entry, ct);
        }, ct);
        return Inspect(name).Files.First(file => file.Path == relative);
    }

    public async Task DeleteFileAsync(string name, string? path, CancellationToken ct)
    {
            RequireOwned(name);
        var relative = Normalize(path);
        await Locked(async () =>
        {
            if (Inspect(name).Files.All(file => file.Path != relative)) throw FileNotFound();
            File.Delete(Resolve(name, relative));
            MarkOwnerEdit(name);
            await Touch(name, entry => entry, ct);
        }, ct);
    }

    public async Task<SkillFile> RenameFileAsync(string name, string? from, string? to, CancellationToken ct)
    {
            RequireOwned(name);
        var source = Normalize(from);
        var target = Normalize(to);
        await Locked(async () =>
        {
            var files = Inspect(name).Files;
            if (files.All(file => file.Path != source)) throw FileNotFound();
            if (files.Any(file => file.Path == target)) throw new AssistantException(409, "file_exists", "A file with that name already exists.");
            if (SettingsOf(name).Execution == SkillExecution.Sandbox) RequireSandboxLanguages([target]);
            var destination = Resolve(name, target);
            DomainOnboardingStore.EnsureDirectory(Path.GetDirectoryName(destination)!);
            File.Move(Resolve(name, source), destination);
            MarkOwnerEdit(name);
            await Touch(name, entry => entry, ct);
        }, ct);
        return Inspect(name).Files.First(file => file.Path == target);
    }

    /// <summary>Copies any skill, built-in or not, to a new disabled skill of the owner's under <paramref name="newName"/>.</summary>
    public async Task<SkillDetail> DuplicateAsync(string name, string? newName, CancellationToken ct)
    {
        Require(name);
        var target = newName?.Trim() ?? "";
        if (!ValidName(target)) throw Invalid("Name the copy with lowercase letters, digits and single hyphens, up to 64 characters.");
        RequireUnreserved(target);
        var (folder, paths) = PackList(name);
        var files = new List<(string Path, byte[] Data)>();
        foreach (var path in paths)
        {
            var data = await File.ReadAllBytesAsync(Resolve(name, path), ct);
            if (path == Manifest)
            {
                var text = Encoding.UTF8.GetString(data);
                var markdown = SkillMarkdown.Parse(text);
                if (markdown is null) throw Invalid("That skill's SKILL.md can't be read, so it can't be copied.");
                data = new UTF8Encoding(false).GetBytes(SkillMarkdown.Write(target, markdown.Text("description") ?? "", markdown.Settings(), markdown.Body, markdown, OwnerCopy));
            }
            files.Add((path, data));
        }
        return await InstallAsync(files, "copy of " + name, ct);
    }

    // ---- import and export ----

    public async Task<SkillDetail> ImportZipAsync(Stream zip, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await zip.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxZipBytes) throw new AssistantException(413, "zip_too_large", "Skill zips are at most 5 MiB.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        var files = new List<(string Path, byte[] Data)>();
        try
        {
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
            var entries = archive.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            foreach (var entry in entries)
                if (Unsafe(entry.FullName) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new AssistantException(400, "invalid_path", $"The zip holds a path Lucia won't unpack: {Shown(entry.FullName)}.");
            var root = ZipRoot(entries.Select(entry => entry.FullName));
            long total = 0;
            foreach (var entry in entries)
            {
                if (!entry.FullName.StartsWith(root, StringComparison.Ordinal)) continue;
                var relative = entry.FullName[root.Length..];
                if (!Accepted(relative, out var normalized)) continue;
                if (entry.Length > MaxFileBytes) throw new AssistantException(413, "file_too_large", $"{normalized} is over 1 MiB.");
                if (files.Count >= MaxFiles + 1) throw new AssistantException(409, "too_many_files", $"A skill holds at most {MaxFiles} files.");
                await using var input = entry.Open();
                using var data = new MemoryStream();
                var piece = new byte[81920];
                int got;
                while ((got = await input.ReadAsync(piece, ct)) > 0)
                {
                    total += got;
                    if (data.Length + got > MaxFileBytes || total > MaxSkillBytes)
                        throw new AssistantException(413, "skill_too_large", "The zip unpacks to more than skills allow.");
                    data.Write(piece, 0, got);
                }
                files.Add((normalized, data.ToArray()));
            }
        }
        catch (InvalidDataException) { throw new AssistantException(400, "invalid_zip", "That isn't a readable zip file."); }
        return await InstallAsync(files, "zip", ct);
    }

    public async Task ExportAsync(string name, Stream output, CancellationToken ct)
    {
        Require(name);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var path in new[] { Manifest }.Concat(Inspect(name).Files.Select(file => file.Path)))
        {
            var entry = archive.CreateEntry(name + "/" + path, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await target.WriteAsync(await File.ReadAllBytesAsync(Resolve(name, path), ct), ct);
        }
    }

    /// <summary>
    /// Imports a skill folder from a public GitHub repository through the contents API. The owner must have allowed github.com
    /// among the assistant's sites; every request goes to api.github.com over the assistant's checked connection.
    /// </summary>
    public async Task<SkillDetail> ImportGitHubAsync(string? url, IReadOnlyCollection<string> allowedSites, CancellationToken ct)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var page) || page.Scheme != "https" || page.Host is not ("github.com" or "www.github.com"))
            throw new AssistantException(400, "invalid_url", "Give a github.com folder address, such as https://github.com/owner/repo/tree/main/skills/name.");
        if (!allowedSites.Contains("github.com", StringComparer.OrdinalIgnoreCase))
            throw new AssistantException(403, "site_not_allowed", "github.com isn't on the assistant's allowed sites. Add it in the assistant's settings first.");
        var parts = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        string? reference = null;
        var folder = new List<string>();
        if (parts.Length >= 4 && parts[2] == "tree") (reference, folder) = (parts[3], [.. parts[4..]]);
        else if (parts.Length != 2) throw new AssistantException(400, "invalid_url", "Give a repository or a folder in it: github.com/owner/repo/tree/ref/path.");
        var owner = parts[0];
        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        if (!GitHubName().IsMatch(owner) || !GitHubName().IsMatch(repository) || reference is { } r && (r.Length is 0 or > 200 || r.Contains("..") || r.Any(char.IsControl))
            || folder.Any(segment => !GitHubName().IsMatch(segment) || segment is "." or ".."))
            throw new AssistantException(400, "invalid_url", "That address has characters Lucia won't fetch.");

        var requests = 0;
        async Task<JsonElement> Get(IReadOnlyList<string> path)
        {
            if (++requests > MaxGitHubRequests) throw new AssistantException(413, "skill_too_large", "That folder holds more files than skills allow.");
            var address = $"https://api.github.com/repos/{owner}/{repository}/contents"
                + (path.Count > 0 ? "/" + string.Join('/', path.Select(Uri.EscapeDataString)) : "")
                + (reference is null ? "" : "?ref=" + Uri.EscapeDataString(reference));
            try
            {
                using var document = JsonDocument.Parse(await _fetch(new Uri(address), ct));
                return document.RootElement.Clone();
            }
            catch (JsonException) { throw new AssistantException(502, "github_error", "GitHub's answer wasn't readable."); }
        }

        var files = new List<(string Path, byte[] Data)>();
        long total = 0;
        async Task Walk(List<string> path, string relative, int depth)
        {
            var listing = await Get(path);
            if (listing.ValueKind != JsonValueKind.Array) throw new AssistantException(400, "invalid_url", "That address is a file. Give the skill's folder.");
            foreach (var item in listing.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { } entry || Text(item, "type") is not { } type || !SegmentPattern().IsMatch(entry)) continue;
                var child = relative.Length == 0 ? entry : relative + "/" + entry;
                var childPath = new List<string>(path) { entry };
                if (type == "dir" && depth < MaxDepth && (relative.Length > 0 || Folders.Contains(entry))) await Walk(childPath, child, depth + 1);
                else if (type == "file" && Accepted(child, out var normalized) && (relative.Length > 0 || entry == Manifest))
                {
                    var file = await Get(childPath);
                    if (Text(file, "encoding") != "base64" || Text(file, "content") is not { } encoded) continue;
                    byte[] data;
                    try { data = Convert.FromBase64String(encoded.ReplaceLineEndings("")); }
                    catch (FormatException) { throw new AssistantException(502, "github_error", "GitHub's answer wasn't readable."); }
                    total += data.Length;
                    if (data.Length > MaxFileBytes || total > MaxSkillBytes || files.Count > MaxFiles)
                        throw new AssistantException(413, "skill_too_large", "That folder is larger than skills allow.");
                    files.Add((normalized, data));
                }
            }
        }

        await Walk(folder, "", 0);
        return await InstallAsync(files, page.AbsoluteUri, ct);
    }

    /// <summary>Checks a package's files, then installs it as a new disabled skill named in its SKILL.md.</summary>
    private async Task<SkillDetail> InstallAsync(List<(string Path, byte[] Data)> files, string source, CancellationToken ct)
    {
        var manifest = files.FirstOrDefault(file => file.Path == Manifest);
        if (manifest.Data is null) throw new AssistantException(400, "invalid_skill", "The package has no SKILL.md at its top.");
        var markdown = SkillMarkdown.Parse(Encoding.UTF8.GetString(manifest.Data));
        var name = markdown?.Text("name")?.Trim() ?? "";
        if (!ValidName(name)) throw new AssistantException(400, "invalid_skill", "SKILL.md needs frontmatter with a name of lowercase letters, digits and hyphens.");
        RequireUnreserved(name);
        if (files.Count - 1 > MaxFiles || files.Sum(file => (long)file.Data.Length) > MaxSkillBytes)
            throw new AssistantException(413, "skill_too_large", "That package is larger than skills allow.");
        if (markdown!.Settings().Execution == SkillExecution.Sandbox) RequireSandboxLanguages(files.Select(file => file.Path));
        SkillSchemas.Read(markdown, out var schemaProblem);
        if (schemaProblem is not null) throw Invalid(schemaProblem);
        await Locked(async () =>
        {
            if (Directory.Exists(Dir(name))) throw new AssistantException(409, "skill_exists", $"There is already a skill named {name}.");
            RequireRoom();
            DomainOnboardingStore.EnsureDirectory(Root);
            var stage = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var (path, data) in files)
                {
                    var target = Path.Combine(stage, path.Replace('/', Path.DirectorySeparatorChar));
                    DomainOnboardingStore.EnsureDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, data);
                }
                Directory.Move(stage, Dir(name));
            }
            finally
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
            await Touch(name, entry => new(false, source, DateTimeOffset.UtcNow, ""), ct);
        }, ct);
        return await GetAsync(name, ct);
    }

    // ---- paths ----

    /// <summary>
    /// A skill-relative path the API accepts: forward slashes, no links or climbing, a folder Lucia manages and an extension it
    /// knows. Scripts live under scripts/; a script's extension is refused under references/ and assets/.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (path is null || Unsafe(path)) throw new AssistantException(400, "invalid_path", "That isn't a path inside the skill.");
        if (!Accepted(path, out var normalized) || normalized == Manifest)
            throw new AssistantException(400, "invalid_path",
                "Files go in scripts/, references/ or assets/, with a known extension: .js .mjs .ts .mts .py .sh .cs for scripts, and text files such as .md .json .yaml .txt elsewhere.");
        return normalized;
    }

    /// <summary>Whether a path would escape the skill: absolute, climbing, with a backslash, a drive or control characters.</summary>
    internal static bool Unsafe(string path)
    {
        if (path.Length is 0 or > 400 || path[0] is '/' or '\\' || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) return true;
        return path.Split('/').Any(segment => segment.Length == 0 || segment is ".." or ".");
    }

    /// <summary>Whether a path is one Lucia keeps: SKILL.md, or a file of a known extension in a managed folder. Others are skipped on import.</summary>
    private static bool Accepted(string relative, out string normalized)
    {
        normalized = relative;
        var segments = relative.Split('/');
        if (relative == Manifest) return true;
        if (segments.Length is < 2 or > MaxDepth || !Folders.Contains(segments[0]) || segments.Any(segment => !SegmentPattern().IsMatch(segment))) return false;
        var extension = Path.GetExtension(relative).ToLowerInvariant();
        var language = SkillLanguages.Language(relative);
        if (language is null) return false;
        if (segments[0] != "scripts" && SkillLanguages.IsScript(relative)) return false;
        return segments[0] == "scripts" ? language is not null : ResourceExtensions.Contains(extension);
    }

    /// <summary>The folder a zip's skill sits in: nothing when SKILL.md is at the top, or the one top folder that holds it.</summary>
    private static string ZipRoot(IEnumerable<string> names)
    {
        var all = names.Where(name => !name.StartsWith("__MACOSX/", StringComparison.Ordinal)).ToArray();
        if (all.Contains(Manifest)) return "";
        var tops = all.Where(name => name.EndsWith("/" + Manifest, StringComparison.Ordinal) && name.Count(c => c == '/') == 1).ToArray();
        return tops.Length == 1 ? tops[0][..^Manifest.Length] : throw new AssistantException(400, "invalid_skill", "Put SKILL.md at the top of the zip, or in one folder.");
    }

    private string Resolve(string name, string relative)
    {
        var folder = Path.GetFullPath(Dir(name));
        var path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new AssistantException(400, "invalid_path", "That isn't a path inside the skill.");
        try { DomainOnboardingStore.RejectLinks(path); }
        catch (IOException) { throw new AssistantException(400, "invalid_path", "Links aren't allowed inside a skill."); }
        return path;
    }

    private static string Shown(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---- validation ----

    private static SkillSettings Settings(string? execution, string? tier, string[]? domains, string? network = null)
    {
        if (!SkillExecution.Valid(execution)) throw Invalid("Run scripts on the node, the Spark or in the sandbox.");
        if (tier is not (ToolTier.Read or ToolTier.Change or ToolTier.Destructive)) throw Invalid("A skill's tier is read, change or destructive.");
        var names = (domains ?? []).Select(domain => domain?.Trim().ToLowerInvariant() ?? "").Where(domain => domain.Length > 0).Distinct().ToArray();
        if (names.Length > MaxDomains || names.Any(domain => !DomainPattern().IsMatch(domain)))
            throw new AssistantException(400, "invalid_domain", "Allowed domains are host names such as api.example.com or *.example.com.");
        if (network is not (null or "" or SkillNetwork.None or SkillNetwork.Bridge)) throw Invalid("A skill's network is none or bridge.");
        return new(execution!, tier, execution == SkillExecution.Sandbox ? names : [], execution == SkillExecution.Spark && network == SkillNetwork.Bridge ? SkillNetwork.Bridge : SkillNetwork.None);
    }

    private static string Description(string? text)
    {
        var description = string.Join(' ', (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return description.Length is > 0 and <= 1024 ? description : throw Invalid("Describe the skill in 1 to 1,024 characters.");
    }

    private static string Limit(string body) =>
        body.Length <= MaxEditableBytes ? body : throw new AssistantException(413, "file_too_large", "SKILL.md is at most 256 KiB.");

    private static void RequireSandboxLanguages(IEnumerable<string> paths)
    {
        var offending = paths.Where(path => path.StartsWith("scripts/", StringComparison.Ordinal) && SkillLanguages.IsScript(path) && !SkillLanguages.SandboxCanRun(path)).ToArray();
        if (offending.Length > 0)
            throw new AssistantException(409, "sandbox_languages",
                "Sandboxed skills run JavaScript and Python only. Change or remove: " + string.Join(", ", offending.Take(10)) + (offending.Length > 10 ? ", …" : "."));
    }

    private void RequireRoom()
    {
        if (Directory.Exists(Root) && Directory.EnumerateDirectories(Root).Count(folder => ValidName(Path.GetFileName(folder))) >= MaxSkills)
            throw new AssistantException(409, "too_many_skills", $"Lucia keeps at most {MaxSkills} skills.");
    }

    private void Require(string name)
    {
        if (!ValidName(name) || !Directory.Exists(Dir(name)))
            throw new AssistantException(404, "skill_not_found", "That skill doesn't exist.");
    }

    /// <summary>Like <see cref="Require"/>, for a change: built-in skills are read-only.</summary>
    private void RequireOwned(string name)
    {
        if (IsBuiltIn(name)) throw new AssistantException(409, "skill_builtin", "Built-in skills are read-only. Duplicate it to make your own copy.");
        Require(name);
    }

    private void RequireUnreserved(string name)
    {
        if (IsBuiltIn(name)) throw new AssistantException(409, "skill_reserved", $"{name} is the name of a built-in skill. Choose another name.");
    }

    private static AssistantException Invalid(string message) => new(400, "invalid_skill", message);

    private static AssistantException FileNotFound() => new(404, "file_not_found", "That file doesn't exist in the skill.");

    // ---- state ----

    private SkillSummary Summarize(string name, IReadOnlyDictionary<string, AgentSkillFrontmatter> loaded)
    {
        var inspection = Inspect(name);
        var markdown = ReadMarkdown(name, out _);
        var settings = markdown?.Settings() ?? SkillSettings.Default;
        var entry = EntryFor(name);
        var description = loaded.TryGetValue(name, out var frontmatter) ? frontmatter.Description : markdown?.Text("description") ?? "";
        var problem = inspection.Problem ?? (loaded.ContainsKey(name) ? null : Explain(name, markdown));
        if (problem is null) SkillSchemas.Read(markdown, out problem);
        var author = IsBuiltIn(name) ? SkillAuthors.Lucia : entry.Source == SkillAuthors.Assistant ? SkillAuthors.Assistant : SkillAuthors.Owner;
        var meta = markdown?.Metadata() ?? [];
        var drafted = author == SkillAuthors.Assistant;
        return new(name, description, settings.Execution, settings.Tier, entry.Enabled, inspection.Hash, entry.Updated, entry.Source,
            inspection.Files.Count(file => file.Kind == "script" && SkillLanguages.IsScript(file.Path)), problem, IsBuiltIn(name),
            author, drafted && !entry.Enabled, drafted ? Clip(meta.GetValueOrDefault(SkillAuthors.RationaleKey)?.ToString()) : null,
            drafted ? Split(meta.GetValueOrDefault(SkillAuthors.SourceChatsKey)?.ToString()) : [], drafted ? Warnings(meta.GetValueOrDefault(SkillAuthors.WarningsKey)?.ToString()) : []);
    }

    private static string Explain(string name, SkillMarkdown? markdown)
    {
        if (markdown is null) return "SKILL.md needs YAML frontmatter between two --- lines, with a name and a description.";
        if (markdown.Text("name") != name) return "The name in SKILL.md must match the skill's folder name.";
        if (string.IsNullOrWhiteSpace(markdown.Text("description"))) return "SKILL.md needs a description.";
        return "The Agent Framework couldn't load this skill. Check the SKILL.md frontmatter.";
    }

    private SkillMarkdown? ReadMarkdown(string name, out string? raw)
    {
        raw = null;
        try
        {
            var path = Path.Combine(Dir(name), Manifest);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes || IsLink(new FileInfo(path))) return null;
            raw = File.ReadAllText(path, Encoding.UTF8);
            return SkillMarkdown.Parse(raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private Inspection Inspect(string name)
    {
        // While a writer holds the gate the files are changing and the revision hasn't moved: build fresh and never cache.
        if (_gate.CurrentCount == 0) return Build(name);
        // An inspection is only trusted for the revision it was built at, so a reader racing a save can't leave a stale hash behind.
        var revision = Revision;
        if (_inspections.TryGetValue(name, out var cached) && cached.Revision == revision) return cached;
        var built = Build(name) with { Revision = revision };
        if (Revision == revision) _inspections[name] = built;
        return built;
    }

    private Inspection Build(string name)
    {
        var folder = Dir(name);
        var files = new List<(string Path, string Absolute, long Size)>();
        try
        {
            if (IsLink(new DirectoryInfo(folder))) return Broken("The skill's folder is a link, which Lucia doesn't follow.");
            var manifest = new FileInfo(Path.Combine(folder, Manifest));
            if (!manifest.Exists) return Broken("SKILL.md is missing.");
            if (IsLink(manifest)) return Broken("SKILL.md is a link, which Lucia doesn't follow.");
            files.Add((Manifest, manifest.FullName, manifest.Length));
            foreach (var top in Folders)
                if (Directory.Exists(Path.Combine(folder, top)) && Collect(new DirectoryInfo(Path.Combine(folder, top)), top, 1, files) is { } link)
                    return Broken($"{link} is a link, which Lucia doesn't follow.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("A skill folder could not be read ({ErrorType}).", e.GetType().Name);
            return Broken("The skill's files couldn't be read.");
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var bytes = files.Sum(file => file.Size);
        var problem = files.Count - 1 > MaxFiles ? $"The skill has more than {MaxFiles} files."
            : bytes > MaxSkillBytes ? "The skill is larger than 5 MiB." : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, absolute, size) in files)
        {
            if (size > MaxFileBytes) problem ??= $"{path} is larger than 1 MiB.";
            hash.AppendData(Encoding.UTF8.GetBytes(path));
            hash.AppendData([0]);
            hash.AppendData(BitConverter.GetBytes(size));
            if (size <= MaxFileBytes) hash.AppendData(File.ReadAllBytes(absolute));
        }
        var listed = files.Where(file => file.Path != Manifest).Select(file => new SkillFile(file.Path, Kind(file.Path), file.Size, SkillLanguages.Language(file.Path),
            file.Size <= MaxEditableBytes && SkillLanguages.Language(file.Path) is not null)).ToArray();
        return new(listed, Convert.ToHexStringLower(hash.GetHashAndReset()), problem, bytes);

        static Inspection Broken(string problem) => new([], new string('0', 64), problem, 0);
    }

    /// <summary>Collects the files Lucia manages under a folder; returns the first link it meets, which makes the skill unusable.</summary>
    private static string? Collect(DirectoryInfo directory, string relative, int depth, List<(string Path, string Absolute, long Size)> into)
    {
        if (IsLink(directory)) return relative;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var path = relative + "/" + entry.Name;
            if (IsLink(entry)) return path;
            if (entry is DirectoryInfo child)
            {
                if (depth < MaxDepth && Collect(child, path, depth + 1, into) is { } link) return link;
            }
            else if (entry is FileInfo file && Accepted(path, out _)) into.Add((path, file.FullName, file.Length));
        }
        return null;
    }

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null || (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);

    private static string Kind(string path) => path.StartsWith("scripts/", StringComparison.Ordinal) ? "script"
        : path.StartsWith("references/", StringComparison.Ordinal) ? "reference" : "asset";

    /// <summary>The skills the Agent Framework's file source can load, by folder name; recomputed after every change.</summary>
    private Task<IReadOnlyDictionary<string, AgentSkillFrontmatter>> LoadedAsync(CancellationToken ct)
    {
        var revision = Revision;
        if (_loaded is { } cached && cached.Revision == revision) return cached.Loaded;
        var task = LoadAsync(CancellationToken.None);
        _loaded = (revision, task);
        return task;
    }

    private async Task<IReadOnlyDictionary<string, AgentSkillFrontmatter>> LoadAsync(CancellationToken ct)
    {
        var loaded = new Dictionary<string, AgentSkillFrontmatter>(StringComparer.Ordinal);
        if (!Directory.Exists(Root) && BuiltInNames.Count == 0) return loaded;
        try
        {
            foreach (var skill in await CreateSource(null).GetSkillsAsync(new AgentSkillsSourceContext(SkillAgent.Instance, null), ct))
            {
                var folder = skill is AgentFileSkill file ? Path.GetFileName(file.Path.TrimEnd('/', '\\')) : skill.Frontmatter.Name;
                if (folder == skill.Frontmatter.Name) loaded[folder] = skill.Frontmatter;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning("Reading the skills failed ({ErrorType}).", e.GetType().Name);
        }
        return loaded;
    }

    private SkillIndexEntry EntryFor(string name)
    {
        if (IsBuiltIn(name)) return new(true, BuiltInSource, File.GetLastWriteTimeUtc(Path.Combine(Dir(name), Manifest)), "");
        var index = ReadIndex();
        return index.Skills.TryGetValue(name, out var entry) ? entry
            : new(false, null, Directory.GetLastWriteTimeUtc(Dir(name)), "");
    }

    private SkillIndex ReadIndex()
    {
        if (_index is { } cached) return cached;
        try
        {
            if (File.Exists(IndexPath) && new FileInfo(IndexPath).Length <= 4 * 1024 * 1024)
            {
                DomainOnboardingStore.RejectLinks(IndexPath);
                _index = JsonSerializer.Deserialize<SkillIndex>(File.ReadAllBytes(IndexPath), AssistantStream.Json);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("The skills index could not be read ({ErrorType}); starting empty.", e.GetType().Name);
        }
        return _index = _index is { Skills: not null } ? _index : new(new(StringComparer.Ordinal));
    }

    private Task<SkillIndex> Index(CancellationToken ct) => Task.FromResult(ReadIndex());

    private Task SaveIndex(CancellationToken ct) =>
        DomainOnboardingStore.WriteJson(IndexPath, ReadIndex(), ct, AssistantStream.Json);

    /// <summary>Records a change to a skill in the index: its new hash and update time (unless <paramref name="rehash"/> is false).</summary>
    private async Task Touch(string name, Func<SkillIndexEntry, SkillIndexEntry> change, CancellationToken ct, bool rehash = true)
    {
        _inspections.TryRemove(name, out _);
        var index = ReadIndex();
        var entry = index.Skills.TryGetValue(name, out var current) ? current : new(false, null, DateTimeOffset.UtcNow, "");
        entry = change(entry);
        if (rehash) entry = entry with { Hash = Inspect(name).Hash, Updated = DateTimeOffset.UtcNow };
        index.Skills[name] = entry;
        await SaveIndex(ct);
    }

    private async Task Locked(Func<Task> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await change();
        }
        finally
        {
            // Even a failed change may have written some files, so the revision moves either way.
            Interlocked.Increment(ref _revision);
            _inspections.Clear();
            _gate.Release();
        }
        Changed?.Invoke();
    }

    private static void WriteText(string path, string text) => WriteBytes(path, new UTF8Encoding(false).GetBytes(text));

    private static void WriteBytes(string path, byte[] data)
    {
        DomainOnboardingStore.EnsureDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
