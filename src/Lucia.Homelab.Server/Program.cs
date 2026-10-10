using Lucia.Homelab.Server.Host;
using Lucia.Homelab.Server.Assistant;
using Lucia.Homelab.Server.Boot;
using Lucia.Homelab.Server.Onboarding;
using Lucia.Homelab.Server.Telemetry;
using Lucia.Homelab.Server.Domains;
using Lucia.Homelab.Server.LabMap;
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
builder.Services.AddSingleton<PeopleDirectory>();
builder.AddStacks();
builder.Services.AddSingleton<Lucia.Homelab.Server.Host.AdGuardFleet>();
builder.Services.AddHostedService(services => services.GetRequiredService<Lucia.Homelab.Server.Host.AdGuardFleet>());
builder.Services.AddHostedService<ManagedNodeDns>();
builder.Services.AddHostedService<Lucia.Homelab.Server.Stacks.AppSsoRegistrations>();
builder.Services.AddHostedService<Lucia.Homelab.Server.Stacks.AppTelemetry>();
builder.Services.AddSingleton<ManagedNodeDhcp>();
builder.Services.AddHostedService(services => services.GetRequiredService<ManagedNodeDhcp>());
builder.AddSparkTelemetry();
builder.AddControllerRelay();
builder.AddPackageUpdates();
builder.AddAssistant();
builder.AddLabMap();

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
app.MapPeopleDirectory();
app.MapStacks();
app.MapSparkTelemetry();
app.MapControllerRelay();
app.MapPackageUpdates();
app.MapAssistant();
app.MapLabMap();

app.MapDevelopmentApiDocumentation();

app.UseOutputCache();

app.MapDefaultEndpoints();

app.MapHostWeb();

app.Run();