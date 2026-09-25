using System.Text.Json;

namespace Lucia.Homelab.Server.Host;

internal static class ModelCatalogLeaseChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lucia-lease-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var first = CreateHost(root);
            await first.StartAsync();
            var model = new LocalModel(Guid.NewGuid(),
                new ModelDownloadRequest("huggingface", "checks/lease", "model.gguf", ModelKind.Chat, Pro: true),
                ModelDownloadState.Downloading, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var directory = Path.Combine(root, model.Id.ToString("N"));
            Directory.CreateDirectory(directory);
            var manifest = Path.Combine(directory, "model.json");
            var original = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(manifest, original);

            await ExpectContentionAsync(root);
            Check(await File.ReadAllTextAsync(manifest) == original, "A rejected instance modified the manifest.");
            await first.StopAsync();
            await ExpectContentionAsync(root);
            first.Dispose();

            using (var next = CreateHost(root))
            {
                await next.StartAsync();
                Check(next.Services.GetRequiredService<ModelCatalog>().Find(model.Id)?.State == ModelDownloadState.Interrupted,
                    "Lease release did not allow the next instance to recover the manifest.");
                await next.StopAsync();
            }
            var lease = Path.Combine(root, ModelCatalog.LeaseFileName);
            Check(File.Exists(lease), "Disposal deleted the stable lease file.");
            Check(File.Exists(manifest), "Disposal deleted model data.");
            File.Delete(lease);
            var target = Path.Combine(root, "missing-link-target");
            try { File.CreateSymbolicLink(lease, target); }
            catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
            {
                Console.WriteLine("Symlink check skipped: Windows requires symbolic-link creation privilege.");
                Console.WriteLine("Model catalog lease checks passed: contention before manifest access, retention through StopAsync, release on disposal, data preservation.");
                return;
            }
            using (var linked = CreateHost(root))
            {
                try
                {
                    await linked.StartAsync();
                    throw new InvalidOperationException("A symbolic-link lease was accepted.");
                }
                catch (InvalidDataException exception) when (exception.Message.Contains("symbolic link", StringComparison.Ordinal))
                {
                    Check(!File.Exists(target), "Lease acquisition followed a dangling symbolic link.");
                }
            }
            Console.WriteLine("Model catalog lease checks passed: contention before manifest access, retention through StopAsync, release on disposal, symlink rejection, data preservation.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IHost CreateHost(string root)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostPlatformOptions>(options => options.ModelDirectory = root);
        builder.Services.AddSingleton<ModelCatalog>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ModelCatalog>());
        return builder.Build();
    }

    private static async Task ExpectContentionAsync(string root)
    {
        using var second = CreateHost(root);
        try
        {
            await second.StartAsync();
            throw new InvalidOperationException("A second model catalog acquired an active lease.");
        }
        catch (IOException exception) when (exception.Message.StartsWith("Another host holds the model catalog lease", StringComparison.Ordinal)) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
