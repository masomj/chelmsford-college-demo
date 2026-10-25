using Chelmsford.Infrastructure;
using Chelmsford.Web.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Chelmsford.Core.Integration;
using Chelmsford.Web.Services;
using System.Threading.RateLimiting;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Microsoft.Extensions.FileProviders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#if DEBUG
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
#endif

var paths = CollegePaths.From(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(paths);

// Integration layer, site database, course search, payments and bookings (Chelmsford.Infrastructure).
// The CMS-backed IPayableItemCatalogue and ICourseSyncObserver are registered by ChelmsfordComposer.
builder.Services.AddChelmsfordInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);

// Keys live on the data volume so antiforgery tokens and the payment cookie survive a redeploy.
Directory.CreateDirectory(paths.KeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("Chelmsford.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysPath));

// The site runs behind Caddy, which terminates TLS.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Limits on the few POST endpoints that anyone on the internet can call: per client IP, fixed window.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var rule = RateLimitRules.Match(context.Request);
        if (rule is null)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{rule.Name}:{client}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rule.PermitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
    options.OnRejected = RateLimitRules.WriteRejectionAsync;
});

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();

await app.BootUmbracoAsync();
await app.Services.InitialiseChelmsfordDataAsync();

app.UseForwardedHeaders();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

    // Health check for the container and Caddy. Answers before Umbraco, anonymously. It is healthy only
    // when Umbraco is fully running and the CMS schema, seed content and first sync are done.
    if (context.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
    {
        var level = context.RequestServices.GetRequiredService<IRuntimeState>().Level;
        var ready = context.RequestServices.GetRequiredService<ISiteReadiness>().IsReady;
        context.Response.ContentType = "text/plain";
        context.Response.Headers.CacheControl = "no-store";
        if (level == RuntimeLevel.Run && ready)
        {
            await context.Response.WriteAsync("ok");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync(level == RuntimeLevel.Run ? "Run (starting up)" : level.ToString());
        }

        return;
    }

    await next();
});

// The college's logos and photos come from the tender pack, mounted outside the app (never in git).
if (Directory.Exists(paths.ImagesPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(paths.ImagesPath),
        RequestPath = "/college-assets",
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=604800"
    });
}
else
{
    app.Logger.LogWarning("College images not found at {Path}; the site will use a text wordmark and tinted blocks", paths.ImagesPath);
}

// Page views for the access reports: queued after the response, written by a background service.
app.UsePageViewTracking();

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
        u.EndpointRouteBuilder.MapControllers();
    });

await app.RunAsync();
