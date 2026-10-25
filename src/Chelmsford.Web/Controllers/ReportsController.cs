using System.Globalization;
using System.Text;
using Chelmsford.Core.Access;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Chelmsford.Web.Controllers;

/// <summary>
/// Who has looked at the demo: a daily summary, recent CMS sign-ins and the latest page views. Protected by HTTP Basic
/// sign-in checked here (<c>Reports:UserName</c> and <c>Reports:Password</c>); with no password set it returns 404.
/// </summary>
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ReportsController(IAccessLog accessLog, ReportsAuthenticator auth) : Controller
{
    private const int SummaryDays = 30;
    private const int RecentRows = 200;

    [HttpGet("/reports/access")]
    public async Task<IActionResult> Access(CancellationToken cancellationToken)
    {
        if (Guard() is { } refused)
        {
            return refused;
        }

        Response.Headers.CacheControl = "no-store";
        var model = new AccessReportViewModel
        {
            Title = "Who looked at the demo | Chelmsford College",
            Days = SummaryDays,
            Summary = await accessLog.DailySummaryAsync(SummaryDays, cancellationToken),
            CmsEvents = await CmsEventsAsync(cancellationToken),
            PageViews = await accessLog.RecentAsync(RecentRows, AccessKinds.PageView, cancellationToken),
            NoIndex = true,
            Focused = true
        };
        return View("~/Views/Reports/Access.cshtml", model);
    }

    [HttpGet("/reports/access.csv")]
    public async Task<IActionResult> AccessCsv(CancellationToken cancellationToken)
    {
        if (Guard() is { } refused)
        {
            return refused;
        }

        Response.Headers.CacheControl = "no-store";
        var rows = await accessLog.RecentAsync(500, null, cancellationToken);
        var csv = new StringBuilder("When (UK time),Kind,User,Page,IP (shortened),Browser\r\n");
        foreach (var row in rows)
        {
            csv.Append(Field(UkTime.FromUtc(row.OccurredUtc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))).Append(',')
                .Append(Field(row.Kind)).Append(',')
                .Append(Field(row.UserName)).Append(',')
                .Append(Field(row.Path)).Append(',')
                .Append(Field(row.IpPrefix)).Append(',')
                .Append(Field(row.UserAgent)).Append("\r\n");
        }

        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv; charset=utf-8", "access-log.csv");
    }

    private async Task<IReadOnlyList<AccessLogEntry>> CmsEventsAsync(CancellationToken cancellationToken)
    {
        var all = new List<AccessLogEntry>();
        foreach (var kind in new[] { AccessKinds.CmsLogin, AccessKinds.CmsLoginFailed, AccessKinds.CmsLogout })
        {
            all.AddRange(await accessLog.RecentAsync(50, kind, cancellationToken));
        }

        return all.OrderByDescending(e => e.OccurredUtc).Take(50).ToList();
    }

    private IActionResult? Guard()
    {
        switch (auth.Check(Request))
        {
            case ReportsAuthResult.Allowed:
                return null;
            case ReportsAuthResult.Disabled:
                return SiteResults.NotFoundView(this);
            case ReportsAuthResult.TooManyAttempts:
                Response.Headers.RetryAfter = "900";
                return StatusCode(StatusCodes.Status429TooManyRequests, "Too many wrong attempts. Please wait 15 minutes and try again.");
            default:
                Response.Headers.WWWAuthenticate = "Basic realm=\"Demo access reports\", charset=\"UTF-8\"";
                return StatusCode(StatusCodes.Status401Unauthorized, "Sign in to see the access reports.");
        }
    }

    /// <summary>Quotes a CSV field, and stops spreadsheets running text that starts like a formula.</summary>
    private static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0], StringComparison.Ordinal))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
