using System.Globalization;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chelmsford.Infrastructure.Integration;

/// <summary>Ensures only one sync runs at a time across every scope in the process.</summary>
public sealed class SyncGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}

/// <summary>
/// Brings the website's copy of the college's data up to date. A failed run leaves the existing copy
/// untouched, so the site keeps serving the last good data while the source is unavailable.
/// </summary>
public sealed class CourseSyncService(
    SiteDbContext db,
    ICollegeSourceReader sourceReader,
    ICourseMapper mapper,
    IEnumerable<ICourseSyncObserver> observers,
    SyncGate gate,
    TimeProvider clock,
    ILogger<CourseSyncService> logger) : ICourseSyncService
{
    public async Task<SyncRun> RunAsync(string trigger, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        using var _ = await gate.EnterAsync(cancellationToken);
        var run = new SyncRun { Trigger = trigger, StartedUtc = clock.GetUtcNow().UtcDateTime };

        IReadOnlyList<SyncedCourse> activeCourses;
        try
        {
            var snapshot = await sourceReader.ReadAsync(cancellationToken);
            var mapped = mapper.MapAll(snapshot);
            run.CoursesRead = mapped.Count;

            activeCourses = await ApplyAsync(run, snapshot, mapped, cancellationToken);

            run.Status = SyncStatus.Succeeded;
            run.Message = string.Create(
                CultureInfo.InvariantCulture,
                $"{run.CoursesRead} courses read: {run.Added} added, {run.Updated} updated, {run.Unchanged} unchanged, {run.Removed} removed.");
            run.FinishedUtc = clock.GetUtcNow().UtcDateTime;
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Course sync ({Trigger}) failed. The website keeps serving its last synced copy", trigger);
            return await RecordFailureAsync(run, ex, cancellationToken);
        }

        logger.LogInformation(
            "Course sync ({Trigger}) succeeded: {Read} read, {Added} added, {Updated} updated, {Unchanged} unchanged, {Removed} removed, {Students} people",
            trigger,
            run.CoursesRead,
            run.Added,
            run.Updated,
            run.Unchanged,
            run.Removed,
            run.StudentsSynced);

        await NotifyObserversAsync(activeCourses, cancellationToken);
        return run;
    }

    public async Task<SyncRun?> LastRunAsync(CancellationToken cancellationToken = default) =>
        await db.SyncRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedUtc)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Writes the new copy in one transaction and returns the active courses.</summary>
    private async Task<IReadOnlyList<SyncedCourse>> ApplyAsync(
        SyncRun run,
        SourceSnapshot snapshot,
        IReadOnlyList<SyncedCourse> mapped,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existing = await db.SyncedCourses.ToDictionaryAsync(c => c.Id, cancellationToken);
        var incomingIds = mapped.Select(c => c.Id).ToHashSet();

        // Courses that have left the source keep their slug, so a new course never takes over an old URL.
        var reservedSlugs = existing.Values.Where(c => !incomingIds.Contains(c.Id)).Select(c => c.Slug).ToHashSet(StringComparer.Ordinal);
        foreach (var course in mapped.Where(c => reservedSlugs.Contains(c.Slug)))
        {
            course.Slug = $"{course.Slug}-{course.Id.ToString(CultureInfo.InvariantCulture)}";
        }

        // Slugs that move between courses are parked first, so the unique index never sees a transient clash.
        var slugChanges = mapped
            .Where(c => existing.TryGetValue(c.Id, out var current) && current.Slug != c.Slug)
            .ToList();
        if (slugChanges.Count > 0)
        {
            foreach (var course in slugChanges)
            {
                existing[course.Id].Slug = $"~sync-{course.Id.ToString(CultureInfo.InvariantCulture)}";
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var course in mapped)
        {
            course.SyncedUtc = now;
            if (!existing.TryGetValue(course.Id, out var current))
            {
                db.SyncedCourses.Add(course);
                run.Added++;
            }
            else if (current.SourceHash != course.SourceHash || current.RemovedFromSource || current.Slug != course.Slug)
            {
                db.Entry(current).CurrentValues.SetValues(course);
                current.RemovedFromSource = false;
                run.Updated++;
            }
            else
            {
                run.Unchanged++;
            }
        }

        foreach (var removed in existing.Values.Where(c => !incomingIds.Contains(c.Id) && !c.RemovedFromSource))
        {
            removed.RemovedFromSource = true;
            removed.SyncedUtc = now;
            run.Removed++;
        }

        await db.SyncedStudents.ExecuteDeleteAsync(cancellationToken);
        await db.SyncedApplications.ExecuteDeleteAsync(cancellationToken);
        db.SyncedStudents.AddRange(snapshot.Students
            .GroupBy(s => s.StudentId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(s => new SyncedStudent
            {
                StudentId = s.StudentId,
                FirstName = s.FirstName,
                LastName = s.LastName,
                DateOfBirth = s.DateOfBirth,
                Email = s.Email,
                CourseId = s.CourseId
            }));
        db.SyncedApplications.AddRange(snapshot.Applications
            .GroupBy(a => a.Reference, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(a => new SyncedApplication
            {
                Reference = a.Reference,
                FirstName = a.FirstName,
                LastName = a.LastName,
                Email = a.Email,
                CourseId = a.CourseId
            }));
        run.StudentsSynced = snapshot.Students.Count;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await db.SyncedCourses.AsNoTracking()
            .Where(c => c.Published && !c.RemovedFromSource)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<SyncRun> RecordFailureAsync(SyncRun attempted, Exception exception, CancellationToken cancellationToken)
    {
        // Throw away anything half-applied in this context; the transaction has already rolled back.
        db.ChangeTracker.Clear();

        var failed = new SyncRun
        {
            Trigger = attempted.Trigger,
            StartedUtc = attempted.StartedUtc,
            FinishedUtc = clock.GetUtcNow().UtcDateTime,
            Status = SyncStatus.Failed,
            CoursesRead = attempted.CoursesRead,
            Message = $"Sync failed: {exception.GetType().Name}: {exception.Message}"
        };

        try
        {
            db.SyncRuns.Add(failed);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not record the failed sync run");
        }

        return failed;
    }

    private async Task NotifyObserversAsync(IReadOnlyList<SyncedCourse> activeCourses, CancellationToken cancellationToken)
    {
        foreach (var observer in observers)
        {
            try
            {
                await observer.CoursesSyncedAsync(activeCourses, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sync observer {Observer} failed; the sync itself succeeded", observer.GetType().Name);
            }
        }
    }
}
