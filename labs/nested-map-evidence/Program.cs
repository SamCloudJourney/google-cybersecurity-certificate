using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;

var scenario = Environment.GetEnvironmentVariable("SCENARIO") ?? "map";
var normalize = string.Equals(Environment.GetEnvironmentVariable("NORMALIZE_BACKSLASH"), "1", StringComparison.Ordinal);
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5040;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenLocalhost(port, lo =>
    {
        lo.Protocols = HttpProtocols.Http1;
    });
});

var app = builder.Build();

var contentRoot = Path.Combine(AppContext.BaseDirectory, "files");
var protectedDir = Path.Combine(contentRoot, "admin");
Directory.CreateDirectory(protectedDir);
var secretPath = Path.Combine(protectedDir, "secret.txt");
var secretBytes = "TOP_SECRET_7B4F"u8.ToArray();
File.WriteAllBytes(secretPath, secretBytes);
var secretSha256 = Convert.ToHexString(SHA256.HashData(secretBytes));

app.Use(async (ctx, next) =>
{
    var raw = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;
    Console.WriteLine($"TOP scenario={scenario} normalize={normalize} raw={raw} pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
    await next();
});

if (normalize)
{
    app.Use(async (ctx, next) =>
    {
        var value = ctx.Request.Path.Value;
        if (!string.IsNullOrEmpty(value) && value.Contains('\\'))
        {
            ctx.Request.Path = new PathString(value.Replace('\\', '/'));
            Console.WriteLine($"NORMALIZED path={ctx.Request.Path.Value}");
        }
        await next();
    });
}

void ConfigureProtectedBranch(IApplicationBuilder branch)
{
    branch.Use(async (ctx, next) =>
    {
        Console.WriteLine($"AUTH_GUARD_HIT scenario={scenario} raw={ctx.Features.Get<IHttpRequestFeature>()?.RawTarget} pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
        if (ctx.Request.Headers["X-Admin-Key"] != "research-only")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("AUTH_REQUIRED");
            return;
        }

        await next();
    });

    branch.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(protectedDir),
        ServeUnknownFileTypes = false,
    });

    branch.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("ADMIN_NOT_FOUND");
    });
}

if (scenario == "map")
{
    app.Map("/api", api =>
    {
        api.Use(async (ctx, next) =>
        {
            Console.WriteLine($"API_MAP_ENTER raw={ctx.Features.Get<IHttpRequestFeature>()?.RawTarget} pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
            await next();
        });

        api.Map("/admin", ConfigureProtectedBranch);

        api.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(contentRoot),
            ServeUnknownFileTypes = false,
        });

        api.Run(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("API_NOT_FOUND");
        });
    });
}
else if (scenario == "pathbase")
{
    app.UsePathBase("/api");

    app.Use(async (ctx, next) =>
    {
        Console.WriteLine($"PATHBASE_AFTER pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
        await next();
    });

    app.Map("/admin", ConfigureProtectedBranch);

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(contentRoot),
        ServeUnknownFileTypes = false,
    });

    app.Run(async ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("PATHBASE_NOT_FOUND");
    });
}
else
{
    throw new InvalidOperationException($"Unknown SCENARIO '{scenario}'.");
}

Console.WriteLine($"EVIDENCE_SECRET physical={secretPath} sha256={secretSha256}");
await app.RunAsync();
