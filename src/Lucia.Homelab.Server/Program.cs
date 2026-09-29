using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Telemetry;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.Nodes;
using Lucia.Homelab.Server.Packages;
using Lucia.Homelab.Server.Stacks;

if (args is ["--check-model-lease"])
{
    await ModelCatalogLeaseChecks.RunAsync();
    return;
}

if (args.FirstOrDefault() == "--bundle-model")
{
    if (args.Length != 2)
        throw new ArgumentException("Usage: Lucia.Homelab.Server --bundle-model <publish-directory>");
    await ModelBundleCommand.RunAsync(args[1]);
    return;
}

var builder = WebApplication.CreateBuilder(args);
DomainActivationConfiguration.Apply(builder);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();
InferenceMetrics.Add(builder.Logging);
builder.AddHostOutputCache();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.Services.AddApiDocumentation();
builder.AddInferenceKeyManagement();
builder.AddHostPlatform();
builder.AddHuggingFaceManagement();
builder.AddAdGuardManagement();
builder.AddUniFiManagement();
builder.AddCloudflareDomains();
builder.AddDomainOnboarding();
builder.Services.AddHostedService<DomainSupportWorker>();
builder.AddHardwareOnboarding();
builder.AddHardwareBoot();
builder.Services.AddSingleton<ManagedNodeEnrollment>();
builder.Services.AddSingleton<OwnerSshKeys>();
builder.AddStacks();
builder.Services.AddHostedService<ManagedNodeDns>();
builder.Services.AddHostedService<Lucia.Homelab.Server.Stacks.AppSsoRegistrations>();
builder.Services.AddHostedService<Lucia.Homelab.Server.Stacks.AppTelemetry>();
builder.Services.AddSingleton<ManagedNodeDhcp>();
builder.Services.AddHostedService(services => services.GetRequiredService<ManagedNodeDhcp>());
builder.AddSparkTelemetry();
builder.AddControllerRelay();
builder.AddPackageUpdates();

var app = builder.Build();
TelemetryForwarder.Destination = app.Services.GetRequiredService<StackStore>().TelemetryEndpoint;

// Configure the HTTP request pipeline.
app.UseExceptionHandler();
app.UseHostProxy();
app.UseAuthentication();
app.UseAuthorization();
app.UseHostCsrf();
app.UseDomainConnectionGuard();
app.MapHostAuthentication();
app.MapInferenceKeyManagement();
app.MapHostPlatform();
app.MapHuggingFaceManagement();
app.MapAdGuardManagement();
app.MapUniFiManagement();
app.MapCloudflareDomains();
app.MapDomainOnboarding();
app.MapHardwareOnboarding();
app.MapHardwareBoot();
app.MapManagedInstallation();
app.MapOwnerSshKeys();
app.MapStacks();
app.MapSparkTelemetry();
app.MapControllerRelay();
app.MapPackageUpdates();

app.MapDevelopmentApiDocumentation();

app.UseOutputCache();

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

var api = app.MapGroup("/api");
api.MapGet("weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.CacheOutput(p => p.Expire(TimeSpan.FromSeconds(5)))
.WithTags("Starter demo")
.WithName("GetWeatherForecast");

app.MapDefaultEndpoints();

app.MapHostWeb();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
