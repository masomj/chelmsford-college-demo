namespace Chelmsford.Infrastructure.Configuration;

/// <summary>Where the college's tender-pack content lives (bound from the "College" section).</summary>
public sealed class CollegeContentOptions
{
    public const string SectionName = "College";

    /// <summary>Folder holding <c>source-data/*.json</c> and images. Relative paths resolve against the content root.</summary>
    public string ContentPath { get; set; } = "college-content";
}

/// <summary>Resolved SQLite connection strings for the site database and the college source stand-in.</summary>
public sealed class DataStoreOptions
{
    public const string SiteDataName = "SiteData";
    public const string CollegeSourceName = "CollegeSource";
    public const string DefaultSiteData = "Data Source=data/site.db";
    public const string DefaultCollegeSource = "Data Source=data/college-source.db";

    public string SiteDataConnectionString { get; set; } = DefaultSiteData;
    public string CollegeSourceConnectionString { get; set; } = DefaultCollegeSource;
}

/// <summary>Integration layer settings (bound from the "Integration" section).</summary>
public sealed class IntegrationOptions
{
    public const string SectionName = "Integration";

    /// <summary>How often the website's copy is refreshed from the college's tables.</summary>
    public int SyncIntervalMinutes { get; set; } = 5;
}

/// <summary>Payment rules (bound from the "Payments" section).</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>How long a pending order holds its place (and is reused on refresh) after it was created.</summary>
    public int PendingOrderMinutes { get; set; } = 30;

    /// <summary>Failed guest lookups allowed per client within <see cref="GuestLookupWindowMinutes"/>.</summary>
    public int GuestLookupMaxFailures { get; set; } = 5;

    public int GuestLookupWindowMinutes { get; set; } = 15;

    /// <summary>Failed lookups allowed per student ID within the window, counted across every client.</summary>
    public int GuestLookupMaxFailuresPerStudent { get; set; } = 10;

    /// <summary>Who is told when a payment needs refunding (for example a duplicate payment).</summary>
    public string FinanceEmail { get; set; } = DefaultFinanceEmail;

    public const string DefaultFinanceEmail = "finance@example.ac.uk";

    /// <summary>
    /// Live Stripe keys (<c>sk_live_</c>) are refused unless this is true, so a demo or staging site can't take real money by accident.
    /// </summary>
    public bool AllowLiveKeys { get; set; }
}

/// <summary>Stripe keys (bound from "Payments:Stripe"). With no secret key the built-in demo checkout is used.</summary>
public sealed class StripeOptions
{
    public const string SectionName = "Payments:Stripe";

    public string? SecretKey { get; set; }
    public string? PublishableKey { get; set; }
    public string? WebhookSecret { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey);
}

/// <summary>Access tracking (bound from the "Access" section).</summary>
public sealed class AccessOptions
{
    public const string SectionName = "Access";

    /// <summary>
    /// Who is emailed when a user first views the site each UK day, and on every CMS login. Leave empty to turn
    /// the notifications off.
    /// </summary>
    public string? NotifyEmail { get; set; }

    /// <summary>Access log entries older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// SMTP settings for the outbox sender (bound from the "Smtp" section). With no <see cref="Host"/> nothing is sent
/// and emails stay queued in the outbox.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
    public string FromName { get; set; } = "Chelmsford College demo";
    public bool EnableSsl { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}