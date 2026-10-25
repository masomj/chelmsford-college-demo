using Chelmsford.Core.Access;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Events;
using Chelmsford.Core.Integration;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Access;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Courses;
using Chelmsford.Infrastructure.Data;
using Chelmsford.Infrastructure.Events;
using Chelmsford.Infrastructure.Integration;
using Chelmsford.Infrastructure.Messaging;
using Chelmsford.Infrastructure.Payments;
using Chelmsford.Infrastructure.Source;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IStripeClient = Stripe.IStripeClient;
using StripeClient = Stripe.StripeClient;

namespace Chelmsford.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the site database, the integration layer (source reader, sync and its background
    /// schedule), course search, payments and event bookings. The web project supplies
    /// <see cref="IPayableItemCatalogue"/> and any <see cref="ICourseSyncObserver"/>s.
    /// </summary>
    /// <param name="contentRootPath">Relative SQLite and content paths resolve against this folder.</param>
    public static IServiceCollection AddChelmsfordInfrastructure(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var siteData = SqliteConnectionStrings.Resolve(
            configuration.GetConnectionString(DataStoreOptions.SiteDataName) is { Length: > 0 } site ? site : DataStoreOptions.DefaultSiteData,
            contentRootPath);
        var collegeSource = SqliteConnectionStrings.Resolve(
            configuration.GetConnectionString(DataStoreOptions.CollegeSourceName) is { Length: > 0 } source ? source : DataStoreOptions.DefaultCollegeSource,
            contentRootPath);

        services.Configure<DataStoreOptions>(o =>
        {
            o.SiteDataConnectionString = siteData;
            o.CollegeSourceConnectionString = collegeSource;
        });
        services.AddOptions<CollegeContentOptions>()
            .Bind(configuration.GetSection(CollegeContentOptions.SectionName))
            .PostConfigure(o => o.ContentPath = ResolvePath(o.ContentPath, contentRootPath));
        services.AddOptions<IntegrationOptions>().Bind(configuration.GetSection(IntegrationOptions.SectionName));
        services.AddOptions<PaymentOptions>().Bind(configuration.GetSection(PaymentOptions.SectionName));
        services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName));
        services.AddOptions<AccessOptions>().Bind(configuration.GetSection(AccessOptions.SectionName));
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddDbContext<SiteDbContext>(o => o.UseSqlite(siteData));

        // Integration layer
        services.TryAddSingleton<ISiteReadiness, SiteReadiness>();
        services.AddSingleton<IHtmlCleaner, HtmlCleaner>();
        services.AddSingleton<ICourseMapper, CourseMapper>();
        services.AddSingleton<ICollegeSourceReader, SqliteCollegeSourceReader>();
        services.AddSingleton<SourceDatabaseSeeder>();
        services.AddSingleton<SyncGate>();
        services.AddScoped<ICourseSyncService, CourseSyncService>();
        services.AddHostedService<SyncBackgroundService>();

        // Courses and events
        services.AddScoped<ICourseSearchService, CourseSearchService>();
        services.AddScoped<IEventBookingService, EventBookingService>();

        // Access tracking and the outbox sender (sends nothing unless Smtp:Host is set)
        services.AddSingleton<AccessLogRetentionGate>();
        services.AddScoped<IAccessLog, AccessLogService>();
        services.AddSingleton<OutboxRetryState>();
        services.AddSingleton<IOutboxMailSender, SmtpOutboxMailSender>();
        services.AddScoped<OutboxProcessor>();
        services.AddHostedService<OutboxDispatcher>();

        // Payments
        services.AddSingleton<IGuestLookupLimiter, GuestLookupLimiter>();
        services.AddSingleton<IOrderReferenceGenerator, OrderReferenceGenerator>();
        services.AddSingleton<IReceiptEmailComposer, ReceiptEmailComposer>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IPaymentLinkService, PaymentLinkService>();
        services.AddScoped<IPaymentWebhookHandler, StripeWebhookHandler>();
        AddPaymentGateway(services, configuration);

        return services;
    }

    /// <summary>Creates the site database and, if it doesn't exist yet, the college source stand-in.</summary>
    public static async Task InitialiseChelmsfordDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Resolving the (singleton) gateway now means a refused live key is reported at start-up, not on the first payment.
        var gateway = services.GetRequiredService<IPaymentGateway>();
        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DependencyInjection))
            .LogInformation("Payment gateway: {Gateway} (test mode: {IsTestMode})", gateway.Name, gateway.IsTestMode);

        await services.GetRequiredService<SourceDatabaseSeeder>().EnsureCreatedAsync(cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SiteDbContext>();
        if (await db.Database.EnsureCreatedAsync(cancellationToken))
        {
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(DependencyInjection))
                .LogInformation("Created the site database");
        }

        await EnsureAccessLogTableAsync(db, cancellationToken);
    }

    /// <summary>
    /// <c>EnsureCreated</c> never adds tables to a database that already exists, so a site database created before
    /// access tracking was added gets its table here. Safe to run every start.
    /// </summary>
    internal static async Task EnsureAccessLogTableAsync(SiteDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        await db.Database.ExecuteSqlRawAsync(
            """
             CREATE TABLE IF NOT EXISTS "AccessLog" (
                 "Id" TEXT NOT NULL CONSTRAINT "PK_AccessLog" PRIMARY KEY,
                 "OccurredUtc" TEXT NOT NULL,
                 "Kind" TEXT NOT NULL,
                 "UserName" TEXT NULL,
                 "Path" TEXT NULL,
                 "IpPrefix" TEXT NULL,
                 "UserAgent" TEXT NULL)
             """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_AccessLog_OccurredUtc" ON "AccessLog" ("OccurredUtc")""",
            cancellationToken);
    }

    private static void AddPaymentGateway(IServiceCollection services, IConfiguration configuration)
    {
        var secretKey = configuration.GetSection(StripeOptions.SectionName)[nameof(StripeOptions.SecretKey)];
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            services.AddSingleton<IPaymentGateway, DemoPaymentGateway>();
            return;
        }

        // An empty or unparseable value (an unset environment variable in a compose file, say) counts as "not allowed".
        var allowLive = bool.TryParse(configuration.GetSection(PaymentOptions.SectionName)[nameof(PaymentOptions.AllowLiveKeys)], out var allowed) && allowed;
        if (IsLiveKey(secretKey) && !allowLive)
        {
            // This is a tender demo: real card payments must be switched on deliberately, never by a pasted key.
            services.AddSingleton<IPaymentGateway>(sp =>
            {
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DependencyInjection)).LogError(
                    "A live Stripe secret key (sk_live_...) is configured but {Setting} is not true. Refusing to use it; the demo checkout is being used instead, so no real money can move.",
                    $"{PaymentOptions.SectionName}:{nameof(PaymentOptions.AllowLiveKeys)}");
                return new DemoPaymentGateway();
            });
            return;
        }

        services.AddSingleton<IStripeClient>(sp => new StripeClient(sp.GetRequiredService<IOptions<StripeOptions>>().Value.SecretKey));
        services.AddSingleton<IPaymentGateway, StripePaymentGateway>();
    }

    internal static bool IsLiveKey(string? secretKey) =>
        secretKey?.Trim().StartsWith("sk_live_", StringComparison.Ordinal) == true;

    private static string ResolvePath(string path, string contentRootPath) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(contentRootPath, string.IsNullOrWhiteSpace(path) ? "college-content" : path));
}
