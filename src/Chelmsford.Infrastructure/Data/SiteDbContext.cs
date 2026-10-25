using Chelmsford.Core.Access;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Events;
using Chelmsford.Core.Integration;
using Chelmsford.Core.Messaging;
using Chelmsford.Core.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Chelmsford.Infrastructure.Data;

/// <summary>
/// The website's own database: synced courses and people, sync history, payments, event bookings
/// and the email outbox.
/// </summary>
/// <remarks>
/// In production these tables live in the website's SQL Server database alongside the CMS. For the
/// demo they are kept in a separate SQLite file next to Umbraco's, so two ORMs never contend for the
/// same SQLite file lock.
/// </remarks>
public sealed class SiteDbContext(DbContextOptions<SiteDbContext> options) : DbContext(options)
{
    /// <summary>Value of <see cref="PaymentOrder.ActiveMarker"/> while an order is Pending or Paid.</summary>
    public const string ActiveOrderMarker = "active";

    internal const string AccessLogTable = "AccessLog";
    internal const string AccessLogOccurredIndex = "IX_AccessLog_OccurredUtc";

    public DbSet<SyncedCourse> SyncedCourses => Set<SyncedCourse>();
    public DbSet<SyncedStudent> SyncedStudents => Set<SyncedStudent>();
    public DbSet<SyncedApplication> SyncedApplications => Set<SyncedApplication>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<PaymentOrder> PaymentOrders => Set<PaymentOrder>();
    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<EventBooking> EventBookings => Set<EventBooking>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();
    public DbSet<AccessLogEntry> AccessLog => Set<AccessLogEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date-time type with a kind; every DateTime in this model is UTC, so mark it as such on read.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SyncedCourse>(course =>
        {
            course.HasKey(c => c.Id);
            course.Property(c => c.Id).ValueGeneratedNever();
            course.HasIndex(c => c.Slug).IsUnique();
            course.Ignore(c => c.FundingGroups);
            course.Ignore(c => c.StartLabel);
        });

        modelBuilder.Entity<SyncedStudent>().HasKey(s => s.StudentId);
        modelBuilder.Entity<SyncedApplication>().HasKey(a => a.Reference);

        modelBuilder.Entity<SyncRun>(run =>
        {
            run.HasKey(r => r.Id);
            run.HasIndex(r => r.StartedUtc);
        });

        modelBuilder.Entity<PaymentOrder>(order =>
        {
            order.HasKey(o => o.Id);
            order.Property(o => o.Id).ValueGeneratedNever();
            order.HasIndex(o => o.Reference).IsUnique();

            // One live order per subject and item. ActiveMarker is null once an order is cancelled or
            // expired, and SQLite (like SQL Server with a filtered index) treats NULLs as distinct, so
            // finished orders never collide.
            order.HasIndex(o => new { o.ItemKey, o.SubjectReference, o.ActiveMarker }).IsUnique();
            order.HasIndex(o => o.CreatedUtc);
            order.Property(o => o.SubjectKind).HasConversion<int>();
            order.Property(o => o.ItemType).HasConversion<int>();
            order.Property(o => o.PayerType).HasConversion<int>();
            order.Property(o => o.Status).HasConversion<int>();
        });

        modelBuilder.Entity<PaymentLink>(link =>
        {
            link.HasKey(l => l.Id);
            link.Property(l => l.Id).ValueGeneratedNever();
            link.HasIndex(l => l.TokenHash).IsUnique();
            link.Property(l => l.SubjectKind).HasConversion<int>();
        });

        modelBuilder.Entity<ProcessedWebhookEvent>().HasKey(e => e.EventId);

        modelBuilder.Entity<EventBooking>(booking =>
        {
            booking.HasKey(b => b.Id);
            booking.Property(b => b.Id).ValueGeneratedNever();
            booking.HasIndex(b => b.EventKey);
        });

        modelBuilder.Entity<AccessLogEntry>(entry =>
        {
            entry.ToTable(AccessLogTable);
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).ValueGeneratedNever();
            entry.Property(e => e.Kind).IsRequired().HasMaxLength(32);
            entry.HasIndex(e => e.OccurredUtc).HasDatabaseName(AccessLogOccurredIndex);
        });

        modelBuilder.Entity<OutboxEmail>(email =>
        {
            email.HasKey(e => e.Id);
            email.Property(e => e.Id).ValueGeneratedNever();
            email.HasIndex(e => e.CorrelationKey).IsUnique();
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime(),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
