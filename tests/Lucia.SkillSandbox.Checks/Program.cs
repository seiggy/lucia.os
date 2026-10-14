using System.Text.Json;
using Lucia.SkillSandbox;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

var root = Path.Combine(Directory.GetCurrentDirectory(), "tests", "Lucia.SkillSandbox.Checks", ".fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "scripts"));
File.WriteAllText(Path.Combine(root, "scripts", "a.py"), "print(LUCIA_SKILL_ARGS['name'])\nprint(2)\n");
File.WriteAllText(Path.Combine(root, "scripts", "b.js"), "console.log(LUCIA_SKILL_ARGS.name);\n");
File.WriteAllText(Path.Combine(root, "scripts", "big.py"), new string('#', SandboxRequest.MaxScriptBytes + 1));
File.WriteAllText(Path.Combine(root, "SKILL.md"), "# skill\n");
try
{
    string Req(string script, string language, string extra = "") =>
        $$"""{"script":"{{script}}","language":"{{language}}"{{extra}}}""";
    SandboxRequest? Parse(string json, out string error) => SandboxRequest.Parse(json, out error);

    // Request validation.
    var ok = Parse(Req("scripts/a.py", "python", ",\"arguments\":{\"name\":\"Zoë 😀\"},\"allowedDomains\":[\"api.example.com\"],\"timeoutSeconds\":30,\"heapSize\":\"64Mi\""), out _);
    Check(ok is { Script: "scripts/a.py", Language: "python", TimeoutSeconds: 30, HeapSize: "64Mi" } && ok.AllowedDomains.SequenceEqual(["api.example.com"]),
        "A well-formed request is accepted.");
    Check(Parse(Req("scripts/b.mjs", "javascript"), out _) is { TimeoutSeconds: 60, ArgumentsJson: "{}" }, "A request without arguments gets an empty object and the default limit.");
    Check(ok!.ArgumentsJson.All(c => c < 128) && !ok.ArgumentsJson.Contains('\'') && JsonDocument.Parse(ok.ArgumentsJson).RootElement.GetProperty("name").GetString() == "Zoë 😀",
        "Arguments travel as ASCII JSON, with no quote that could end the Python string.");
    foreach (var bad in new[]
    {
        Req("../scripts/a.py", "python"), Req("scripts/../../etc/x.py", "python"), Req("/scripts/a.py", "python"), Req("a.py", "python"),
        Req("scripts\\\\a.py", "python"), Req("other/a.py", "python"), Req("scripts/a.sh", "python"), Req("scripts/a.py", "javascript"),
        Req("scripts/a.js", "python"), Req("scripts/a.ts", "javascript"), Req("scripts/a.py", "ruby"), Req("scripts//a.py", "python"),
        Req("scripts/a.py", "python", ",\"timeoutSeconds\":0"), Req("scripts/a.py", "python", ",\"timeoutSeconds\":9999"),
        Req("scripts/a.py", "python", ",\"arguments\":[1]"), Req("scripts/a.py", "python", ",\"allowedDomains\":[\"a b\"]"),
        Req("scripts/a.py", "python", ",\"allowedDomains\":[\"-x.com\",\"y\"]"), Req("scripts/a.py", "python", ",\"allowedDomains\":\"x.com\""),
        Req("scripts/a.py", "python", ",\"heapSize\":\"2Gi\""), Req("scripts/a.py", "python", ",\"heapSize\":\"lots\""), "[]", "not json", "",
    })
        Check(Parse(bad, out var error) is null && error.Length > 0, "A bad request is refused with a reason: " + bad);
    Check(Parse(Req("scripts/a.py", "python", $",\"allowedDomains\":[{string.Join(',', Enumerable.Range(0, SandboxRequest.MaxDomains + 1).Select(i => $"\"d{i}.com\""))}]"), out _) is null,
        "Too many allowed domains are refused.");

    // Preamble: arguments reach the script as LUCIA_SKILL_ARGS without moving its line numbers.
    var py = Parse(Req("scripts/a.py", "python", ",\"arguments\":{\"name\":\"it's\"}"), out _)!;
    var preamble = SandboxMain.Preamble(py);
    Check(!preamble.Contains('\n') && preamble.Contains("LUCIA_SKILL_ARGS = _lucia_json.loads(r'") && preamble.Contains("\\u0027"), "The Python preamble is one line and quotes safely.");
    var js = Parse(Req("scripts/b.js", "javascript", ",\"arguments\":{\"n\":[1,2]}"), out _)!;
    Check(SandboxMain.Preamble(js) == "const LUCIA_SKILL_ARGS = {\"n\":[1,2]}; ", "The JavaScript preamble defines the arguments object on the first line.");
    Check((SandboxMain.Preamble(py) + "x\ny").Split('\n').Length == 2, "The preamble adds no line.");

    // Script loading.
    Check(SandboxMain.LoadScript(root, ok, out _) is { } source && source.Contains("print(2)"), "The script's source is read from the skill folder.");
    Check(SandboxMain.LoadScript(root, Parse(Req("scripts/none.py", "python"), out _)!, out var missing) is null && missing.Contains("no scripts/none.py"), "A missing script is reported.");
    Check(SandboxMain.LoadScript(root, Parse(Req("scripts/big.py", "python"), out _)!, out var big) is null && big.Contains("larger"), "An oversized script is refused.");
    var link = Path.Combine(root, "scripts", "link.py");
    try
    {
        File.CreateSymbolicLink(link, Path.Combine(root, "SKILL.md"));
        Check(SandboxMain.LoadScript(root, Parse(Req("scripts/link.py", "python"), out _)!, out var linked) is null && linked.Contains("link"), "A linked script is refused.");
    }
    catch (Exception e) when (e is UnauthorizedAccessException or IOException) { Console.WriteLine("(symlink check skipped: " + e.GetType().Name + ")"); }

    // Output bound and guest answer parsing.
    var (small, cut) = SandboxMain.Bound("héllo");
    Check(small == "héllo" && !cut, "Short output is kept.");
    var (tail, truncated) = SandboxMain.Bound(new string('a', 60_000) + "é" + "END");
    Check(truncated && tail.EndsWith("éEND", StringComparison.Ordinal) && System.Text.Encoding.UTF8.GetByteCount(tail) <= SandboxMain.MaxOutputBytes, "Long output keeps its last 48 KiB.");
    Check(SandboxMain.Bound(new string('é', 40_000)).Text.All(c => c == 'é'), "Truncation never splits a character.");
    Check(SandboxMain.Parse("{\"stdout\":\"hi\\n\",\"stderr\":\"warn\\n\",\"exit_code\":0,\"success\":true}") is { ExitCode: 0, Output: "hi\nwarn\n" }, "stderr follows stdout.");
    Check(SandboxMain.Parse("{\"stdout\":\"\",\"stderr\":\"boom\",\"exit_code\":0,\"success\":false}") is { ExitCode: 1 }, "A failed run never reads as success.");
    Check(SandboxMain.Parse("{\"stdout\":\"\",\"stderr\":\"\",\"exit_code\":7,\"success\":false}") is { ExitCode: 7 }, "The guest's exit code is kept.");
    var described = SandboxMain.Describe(new IOException("failed to open /var/lib/lucia-agent/skills/x/abc: No such file\nstack"));
    Check(!described.Contains("/var/lib") && !described.Contains("stack") && described.Contains("<path>"), "A failure message carries no host path or stack.");

    // HandleAsync with a stand-in guest: shaping, errors, timeout.
    var seen = "";
    var result = await SandboxMain.HandleAsync(Req("scripts/a.py", "python", ",\"arguments\":{\"name\":\"x\"}"), root, (_, _, code, _) =>
    {
        seen = code;
        return Task.FromResult(new GuestRun(0, "done"));
    });
    Check(result is { ExitCode: 0, Output: "done", Error: null } && seen.StartsWith("import json as _lucia_json; LUCIA_SKILL_ARGS", StringComparison.Ordinal) && seen.EndsWith("print(2)\n"),
        "The guest receives the preamble followed by the unchanged script.");
    result = await SandboxMain.HandleAsync(Req("scripts/a.py", "python"), root, (_, _, _, _) => throw new InvalidOperationException("no hypervisor at /usr/lib/x/y"));
    Check(result is { ExitCode: 1, Error: { } e1 } && !e1.Contains("/usr/lib") && e1.Contains("<path>"), "A guest crash becomes a short error.");
    result = await SandboxMain.HandleAsync(Req("scripts/a.py", "python", ",\"timeoutSeconds\":1"), root, async (_, _, _, token) =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return new GuestRun(0, "");
    });
    Check(result is { ExitCode: 124, Error: { } e2 } && e2.Contains("1-second"), "A script past its limit is stopped and reported.");
    Check((await SandboxMain.HandleAsync("{\"script\":\"../x.py\"}", root, (_, _, _, _) => Task.FromResult(new GuestRun(0, "")))).Error is { Length: > 0 }, "A bad request never reaches the guest.");
    Check((await SandboxMain.HandleAsync(null, root, (_, _, _, _) => Task.FromResult(new GuestRun(0, "")))).Error!.Contains("larger"), "An oversized request is refused.");
    Check(await SandboxMain.ReadBounded(new StringReader(new string('x', SandboxRequest.MaxRequestBytes + 1))) is null
        && await SandboxMain.ReadBounded(new StringReader("{}")) == "{}", "Stdin is read up to its limit only.");
}
finally { Directory.Delete(root, recursive: true); }

Console.WriteLine($"Lucia.SkillSandbox checks passed ({checks}).");
