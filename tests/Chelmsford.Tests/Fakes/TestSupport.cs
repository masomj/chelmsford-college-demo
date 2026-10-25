using Chelmsford.Core.Courses;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chelmsford.Tests.Fakes;

/// <summary>
/// A SQLite in-memory site database. The connection stays open for the test's lifetime, so every
/// context created from it shares the same data, as separate requests would in the real site.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SiteDbContext> _options;

    public TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<SiteDbContext>().UseSqlite(_connection).Options;
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public SiteDbContext CreateContext() => new(_options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>A clock the test can move forward.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public TestClock() : this(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now = Now.Add(by);
}

public sealed class FakeCatalogue(params PayableItem[] items) : IPayableItemCatalogue
{
    public Task<IReadOnlyList<PayableItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PayableItem>>(items);

    public Task<PayableItem?> GetAsync(Guid key, CancellationToken cancellationToken = default) =>
        Task.FromResult(items.FirstOrDefault(i => i.Key == key));
}

public sealed class FakeGateway : IPaymentGateway
{
    public string Name => "Fake";

    public bool IsTestMode => true;

    public GatewayPaymentState NextState { get; set; } = GatewayPaymentState.Pending;

    public int CheckoutsCreated { get; private set; }

    /// <summary>Sessions the service asked the provider to close, in order.</summary>
    public List<string> ExpiredSessions { get; } = [];

    public Task<GatewayCheckout> CreateCheckoutAsync(PaymentOrder order, CheckoutUrls urls, CancellationToken cancellationToken = default)
    {
        CheckoutsCreated++;
        return Task.FromResult(new GatewayCheckout("sess_" + order.Reference, "https://checkout.example/" + order.Reference));
    }

    public Task ExpireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ExpiredSessions.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<GatewayPaymentStatus> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayPaymentStatus(NextState, NextState == GatewayPaymentState.Paid ? "pi_" + sessionId : null));
}

public static class TestCourses
{
    public static SyncedCourse Course(
        int id,
        string title,
        string subject,
        int levelOrder,
        string levelShort,
        string mode = CourseVocabulary.FullTime,
        string funding = CourseVocabulary.Funding16To18,
        bool published = true,
        bool removed = false,
        string? qualification = null) => new()
    {
        Id = id,
        Title = title,
        Heading = title,
        Slug = $"course-{id}",
        Subject = subject,
        LevelOrder = levelOrder,
        LevelShort = levelShort,
        LevelLong = levelShort,
        StudyMode = mode,
        Campus = CampusMapping.ForSubject(subject),
        Funding = funding,
        QualificationText = qualification,
        Published = published,
        RemovedFromSource = removed,
        SourceHash = "hash",
        SyncedUtc = DateTime.UtcNow
    };
}
