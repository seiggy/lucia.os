namespace Lucia.Homelab.Server.Host;

public static class HostWebHosting
{
    public static void AddHostOutputCache(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("cache")))
            builder.Services.AddOutputCache();
        else
            builder.AddRedisClientBuilder("cache").WithOutputCache();
    }

    public static void MapHostWeb(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "ok" })).AllowAnonymous();
        app.UseWhen(context => !IsReservedPath(context.Request.Path), branch => branch.UseFileServer());
        app.MapFallback(async context =>
        {
            if (IsReservedPath(context.Request.Path))
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsJsonAsync(new { error = "Endpoint not found." }, context.RequestAborted);
                return;
            }
            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(index, context.RequestAborted);
        });
    }

    private static bool IsReservedPath(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/v1") || path.StartsWithSegments("/auth")
        || path.StartsWithSegments("/health") || path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/signout-callback-oidc");
}
