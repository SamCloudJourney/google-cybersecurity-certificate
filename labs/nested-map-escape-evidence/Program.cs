using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(5060));
var app = builder.Build();

var baseDir = Path.Combine(AppContext.BaseDirectory, "evidence-root");
var contentRoot = Path.Combine(baseDir, "files");
var adminRoot = Path.Combine(contentRoot, "admin");
Directory.CreateDirectory(adminRoot);

var protectedBytes = "PROTECTED_IN_ROOT_4E91"u8.ToArray();
var protectedPath = Path.Combine(adminRoot, "secret.txt");
File.WriteAllBytes(protectedPath, protectedBytes);

var outsideBytes = "OUTSIDE_PROVIDER_ROOT_B8C2"u8.ToArray();
var outsidePath = Path.Combine(baseDir, "outside-secret.txt");
File.WriteAllBytes(outsidePath, outsideBytes);

Console.WriteLine($"PROTECTED_FILE={protectedPath}");
Console.WriteLine($"OUTSIDE_FILE={outsidePath}");

app.Use(async (ctx, next) =>
{
    Console.WriteLine($"TOP raw={ctx.Features.Get<IHttpRequestFeature>()?.RawTarget} pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
    await next();
});

app.Map("/api", api =>
{
    api.Map("/admin", admin =>
    {
        admin.Use(async (ctx, next) =>
        {
            Console.WriteLine($"AUTH_GUARD_HIT raw={ctx.Features.Get<IHttpRequestFeature>()?.RawTarget} pathBase={ctx.Request.PathBase.Value} path={ctx.Request.Path.Value}");
            if (ctx.Request.Headers["X-Admin-Key"] != "research-only")
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("AUTH_REQUIRED");
                return;
            }
            await next();
        });

        admin.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(adminRoot),
        });
    });

    api.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(contentRoot),
        ServeUnknownFileTypes = true,
    });

    api.Run(async ctx =>
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync("API_NOT_FOUND");
    });
});

await app.RunAsync();
