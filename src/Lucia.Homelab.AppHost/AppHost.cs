var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
var hostApiKey = builder.AddParameter("host-api-key", secret: true);

var server = builder.AddProject<Projects.Lucia_Homelab_Server>("server")
    .WithEnvironment("HostPlatform__ApiKey", hostApiKey)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithEndpoint("https", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:huggingface-token"]))
    server.WithEnvironment("HostPlatform__HuggingFaceToken", builder.AddParameter("huggingface-token", secret: true));

var webfrontend = builder.AddViteApp("webfrontend", "../frontend")
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "0.0.0.0")
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_LUCIA_API_URL", server.GetEndpoint("http"))
    .WithReference(server)
    .WaitFor(server);

if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:inference-api-key"]))
{
    var inferenceApiKey = builder.AddParameter("inference-api-key", secret: true);
    server.WithEnvironment("HostPlatform__InferenceApiKey", inferenceApiKey);
    webfrontend.WithEnvironment("LUCIA_PLAYGROUND_API_KEY", inferenceApiKey);
}

server.PublishWithContainerFiles(webfrontend, "wwwroot");

builder.Build().Run();
