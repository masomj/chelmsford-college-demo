using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Chelmsford.Web.Services;

/// <summary>A POST endpoint that is limited per client IP address, with how many requests a minute it allows.</summary>
public sealed record RateLimitRule(string Name, string Path, int PermitsPerMinute);

/// <summary>
/// The public POST endpoints that are rate limited, and the friendly page shown when a limit is hit.
/// Matching is by path so it works for every route, including Umbraco's.
/// </summary>
public static class RateLimitRules
{
    public static readonly IReadOnlyList<RateLimitRule> All =
    [
        new("sync", "/how-the-data-gets-here/sync", 3),
        new("demo-link", "/pay/link/demo", 10),
        new("book", "/events/book", 10),
        new("find-student", "/pay/find-student", 20)
    ];

    public static RateLimitRule? Match(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        var path = (request.Path.Value ?? string.Empty).TrimEnd('/');
        return All.FirstOrDefault(rule => string.Equals(rule.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "text/html; charset=utf-8";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        await response.WriteAsync(Page, cancellationToken);
    }

    private const string Page = """
        <!doctype html>
        <html lang="en-GB">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Too many attempts | Chelmsford College</title>
        <link rel="stylesheet" href="/css/site.css">
        </head>
        <body>
        <a class="skip-link" href="#main-content">Skip to main content</a>
        <main id="main-content" class="wrap pay-layout" tabindex="-1">
        <h1>Too many attempts</h1>
        <p class="lead">You have done that a lot in a short time, so we have paused it for a minute. Nothing has been lost or charged.</p>
        <p>Wait a minute, then go back and try again.</p>
        <p><a class="btn" href="/">Go to the homepage</a></p>
        </main>
        </body>
        </html>
        """;
}
