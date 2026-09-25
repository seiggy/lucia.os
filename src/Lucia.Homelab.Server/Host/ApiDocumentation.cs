using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Lucia.Homelab.Server.Host;

public static class ApiDocumentation
{
    private const string SecurityScheme = "LuciaBearer";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Lucia Host API";
                document.Info.Description = "Model management, local inference, and SRE diagnostics. Authentik browser sessions use same-origin cookies and X-CSRF-TOKEN on unsafe requests. API callers use scoped Authentik access tokens or separate static machine keys.";
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SecurityScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "Lucia token, not a Hugging Face token. Owner tokens authorize host operations; inference tokens authorize only /v1. No token is embedded in this explorer."
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer((operation, context, _) =>
            {
                var authorization = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().ToArray();
                if (authorization.Length > 0)
                {
                    operation.Security = [new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SecurityScheme, context.Document)] = []
                    }];
                    var requirement = authorization.Any(item => item.Policy == "HostOwner")
                        ? "Requires a Lucia owner token." : "Accepts a Lucia owner or inference token.";
                    operation.Description = string.IsNullOrEmpty(operation.Description)
                        ? requirement : operation.Description + "\n\n" + requirement;
                }

                // TensorSharp reads these bodies directly from HttpContext, so API Explorer cannot infer them.
                var example = context.Description.RelativePath switch
                {
                    "v1/chat/completions" => """{"model":"model-id-from-v1-models","messages":[{"role":"user","content":"Hello"}],"max_tokens":128,"stream":false}""",
                    "v1/responses" when context.Description.HttpMethod == "POST" => """{"model":"model-id-from-v1-models","input":"Hello","max_output_tokens":128,"stream":false}""",
                    "v1/embeddings" => """{"model":"embedding-model-id-from-v1-models","input":"Text to embed"}""",
                    _ => null
                };
                if (example is not null)
                {
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new()
                            {
                                Schema = new OpenApiSchema { Type = JsonSchemaType.Object, AdditionalPropertiesAllowed = true },
                                Example = JsonNode.Parse(example)
                            }
                        }
                    };
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static void MapDevelopmentApiDocumentation(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return;

        app.MapOpenApi();
        app.MapScalarApiReference(options =>
        {
            options.Title = "Lucia Host API";
            options.DefaultFonts = false;
            options.Telemetry = false;
            options.PersistentAuthentication = false;
            options.HideClientButton = true;
            options.Agent = new ScalarAgentOptions { Disabled = true };
        });
        app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();
    }
}
