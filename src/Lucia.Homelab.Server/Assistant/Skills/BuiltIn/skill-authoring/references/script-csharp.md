# C# script skeleton

Save as `scripts/check.cs`. It runs as a file-based app (`dotnet run check.cs`) on a node that has the .NET 10 SDK; the sandbox doesn't run C#. Arguments arrive as JSON in `LUCIA_SKILL_ARGS` and on stdin.

```csharp
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

static int Fail(string message, int code = 2)
{
    Console.Error.WriteLine($"Error: {message}");
    return code;
}

JsonElement args;
try { args = JsonDocument.Parse(Environment.GetEnvironmentVariable("LUCIA_SKILL_ARGS") is { Length: > 0 } text ? text : "{}").RootElement; }
catch (JsonException) { return Fail("the arguments aren't valid JSON."); }

var service = args.TryGetProperty("service", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
if (service is null || !Regex.IsMatch(service, @"^[A-Za-z0-9@_.:-]{1,80}\.service$"))
    return Fail("service must be a systemd service name such as docker.service.");

// A fixed file name and an argument list: never a command string built from input.
var start = new ProcessStartInfo("systemctl") { RedirectStandardOutput = true, RedirectStandardError = true };
start.ArgumentList.Add("status");
start.ArgumentList.Add("--no-pager");
start.ArgumentList.Add(service);
using var process = Process.Start(start)!;
if (!process.WaitForExit(20_000))
{
    process.Kill(entireProcessTree: true);
    return Fail("systemctl took too long.", 124);
}
var output = process.StandardOutput.ReadToEnd();
Console.WriteLine(output.Length <= 20_000 ? output : output[^20_000..]);
return process.ExitCode;
```

Rules: `ArgumentList` and a fixed `FileName`, never `Process.Start` with a built string or `bash -c`; validate every value with a regex or allow-list; cap output and time.
