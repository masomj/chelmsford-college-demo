using System.Threading.Channels;
using Chelmsford.Core.Access;

namespace Chelmsford.Web.Services;

/// <summary>
/// Hands page views from the request pipeline to a background writer, so recording a visit never slows a page down
/// and a busy moment (or a broken database) can't back up requests. The queue is bounded; when it is full new
/// views are dropped rather than waiting.
/// </summary>
public sealed class PageViewQueue
{
    private readonly Channel<AccessLogEntry> _channel = Channel.CreateBounded<AccessLogEntry>(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryEnqueue(AccessLogEntry entry) => _channel.Writer.TryWrite(entry);

    public IAsyncEnumerable<AccessLogEntry> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>Writes queued page views to the access log. A failure to record is logged, never thrown.</summary>
public sealed class PageViewRecorder(PageViewQueue queue, IServiceScopeFactory scopes, ILogger<PageViewRecorder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IAccessLog>().RecordAsync(entry, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not record a page view");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>Decides which requests count as page views and queues them.</summary>
public static class PageViewTracking
{
    /// <summary>Header set by the reverse proxy to the signed-in panel user. Without it the visit is recorded as "local".</summary>
    public const string UserHeader = "X-Remote-User";

    private static readonly string[] SkippedPrefixes = ["/umbraco", "/healthz", "/reports", "/css/", "/js/", "/fonts/", "/media/", "/college-assets/", "/_"];

    public static IApplicationBuilder UsePageViewTracking(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            await next();

            try
            {
                Track(context);
            }
            catch (Exception ex)
            {
                // Tracking must never break a page.
                context.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("Chelmsford.Web.PageViewTracking")
                    .LogWarning(ex, "Could not queue a page view");
            }
        });

    private static void Track(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsGet(request.Method) || response.StatusCode != StatusCodes.Status200OK)
        {
            return;
        }

        if (response.ContentType is not { } contentType || !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var path = request.Path.Value ?? "/";
        if (SkippedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var user = request.Headers[UserHeader].ToString().Trim();
        var queue = context.RequestServices.GetService<PageViewQueue>();
        queue?.TryEnqueue(new AccessLogEntry
        {
            Kind = AccessKinds.PageView,
            OccurredUtc = DateTime.UtcNow,
            UserName = string.IsNullOrEmpty(user) ? "local" : user,
            Path = Redact(path),
            IpPrefix = IpAnonymiser.Prefix(context.Connection.RemoteIpAddress),
            UserAgent = request.Headers.UserAgent.ToString()
        });
    }

    /// <summary>
    /// Payment addresses carry an order reference or a payment-link token. Those are not for a report, so
    /// "/pay/link/abc123" is kept as "/pay/link/…".
    /// </summary>
    internal static string Redact(string path)
    {
        if (!path.StartsWith("/pay/", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"/{parts[0]}/{parts[1]}/…" : path;
    }
}
