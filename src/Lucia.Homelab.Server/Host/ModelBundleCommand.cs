using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Lucia.Homelab.Server.Host;

public static class ModelBundleCommand
{
    public static async Task RunAsync(string publishDirectory)
    {
        var destination = Path.GetFullPath(publishDirectory);
        if (!File.Exists(Path.Combine(destination, "Lucia.Homelab.Server.dll")))
            throw new ArgumentException("Publish the host first, then pass that publish directory to --bundle-model.");
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddOptions<HostPlatformOptions>().BindConfiguration("HostPlatform").PostConfigure(options =>
        {
            options.ModelDirectory = Path.Combine(destination, "data", "models");
            options.HuggingFaceHomeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lucia", "huggingface");
        });
        builder.Services.AddSingleton<ModelCatalog>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ModelCatalog>());
        using var host = builder.Build();
        await host.StartAsync();
        var cancellationToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        try
        {
            var catalog = host.Services.GetRequiredService<ModelCatalog>();
            var model = await catalog.DownloadAsync(ModelPresets.Bundled.Source, cancellationToken);
            while (model.State is ModelDownloadState.Queued or ModelDownloadState.Downloading)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                model = catalog.Find(model.Id)!;
            }
            if (model.State != ModelDownloadState.Ready)
                throw new InvalidOperationException($"Bundled model preparation failed: {model.Error}");
            await using (var weights = File.OpenRead(catalog.ModelPath(model.Id)))
            {
                if (weights.Length != ModelPresets.Bundled.SizeBytes
                    || !Convert.ToHexString(await SHA256.HashDataAsync(weights, cancellationToken))
                        .Equals(ModelPresets.Bundled.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installed model no longer matches the bundle's pinned SHA-256 and size.");
            }
            var folder = Path.Combine(destination, "data", "models", model.Id.ToString("N"));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var license = await http.GetStringAsync(ModelPresets.Bundled.LicenseUrl, cancellationToken);
            if (!license.Contains("Apache License", StringComparison.Ordinal) || !license.Contains("Version 2.0", StringComparison.Ordinal))
                throw new InvalidDataException("The upstream model license was not the expected Apache-2.0 license.");
            await File.WriteAllTextAsync(Path.Combine(folder, "LICENSE.model.txt"), license, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "PROVENANCE.json"),
                JsonSerializer.Serialize(ModelPresets.Bundled, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
            Console.WriteLine($"Verified model bundle prepared at {folder}. Spark inference qualification is still required before release.");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
