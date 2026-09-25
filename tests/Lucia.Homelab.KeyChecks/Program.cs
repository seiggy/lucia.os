using System.Text.Json;
using Lucia.Homelab.Server.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var root = Path.Combine(Path.GetTempPath(), "lucia-key-checks-" + Guid.NewGuid().ToString("N"));
var clock = new ManualClock();
var options = Options.Create(new InferenceKeyOptions { Directory = root });
var count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    count++;
}
InferenceApiKeyStore Store() => new(options, clock, NullLogger<InferenceApiKeyStore>.Instance);
try
{
    CreatedInferenceKey permanent;
    CreatedInferenceKey expiring;
    using (var store = Store())
    {
        await store.StartAsync(default);
        permanent = await store.Create(new("Synthetic integration"), "synthetic-owner", default);
        expiring = await store.Create(new("Short-lived integration", clock.Now.AddHours(1)), "synthetic-owner", default);
        Check(permanent.Key.ExpiresAt is null, "Default key unexpectedly expires.");
        Check(store.Authenticates(permanent.Secret) && store.Match(permanent.Secret) == permanent.Key.Id,
            "Issued inference key did not match its own identity.");
        Check(!store.Authenticates(permanent.Secret + "x") && !store.Authenticates("wrong"),
            "A malformed key authenticated.");
        Check(!JsonSerializer.Serialize(store.List()).Contains(permanent.Secret, StringComparison.Ordinal),
            "The public key list exposed the secret.");
        Check(!File.ReadAllText(Path.Combine(root, "keys.json")).Contains(permanent.Secret, StringComparison.Ordinal),
            "The key store persisted a recoverable secret.");
        if (!OperatingSystem.IsWindows())
            Check((File.GetUnixFileMode(Path.Combine(root, "keys.json")) &
                (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
                "Key metadata storage is not private.");
        using var competing = Store();
        try
        {
            await competing.StartAsync(default);
            throw new InvalidOperationException("Two stores acquired the same directory.");
        }
        catch (IOException) { count++; }
        clock.Now = clock.Now.AddHours(1);
        Check(!store.Authenticates(expiring.Secret) && store.Authenticates(permanent.Secret), "Expiry boundary is incorrect.");
        foreach (var request in new[] { new CreateInferenceKeyRequest(""), new("bad\nname"), new("Expired", clock.Now.AddMinutes(-1)) })
        {
            try
            {
                await store.Create(request, "owner", default);
                throw new InvalidOperationException("Invalid key request was accepted.");
            }
            catch (ArgumentException) { count++; }
        }
        await store.StopAsync(default);
    }
    using (var restarted = Store())
    {
        await restarted.StartAsync(default);
        Check(restarted.Authenticates(permanent.Secret) && !restarted.Authenticates(expiring.Secret), "Restart lost key validity or expiry.");
        await restarted.Revoke(permanent.Key.Id, default);
        Check(!restarted.Authenticates(permanent.Secret), "Revocation did not take effect.");
    }
    using (var restarted = Store())
    {
        await restarted.StartAsync(default);
        Check(!restarted.Authenticates(permanent.Secret), "Revoked key was resurrected after restart.");
    }
    File.Delete(Path.Combine(root, "keys.json"));
    using (var missing = Store())
    {
        try
        {
            await missing.StartAsync(default);
            throw new InvalidOperationException("Missing initialized storage silently reset keys.");
        }
        catch (InvalidDataException) { count++; }
    }
    Console.WriteLine($"Inference key lifecycle checks passed ({count} assertions). No live credentials used.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
