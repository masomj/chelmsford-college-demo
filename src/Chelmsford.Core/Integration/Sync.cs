using Chelmsford.Core.Courses;

namespace Chelmsford.Core.Integration;

public enum SyncStatus
{
    Succeeded = 0,
    Failed = 1
}

/// <summary>A record of each sync run, so staff can see when the website last refreshed its copy.</summary>
public sealed class SyncRun
{
    public int Id { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public SyncStatus Status { get; set; }
    public int CoursesRead { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Removed { get; set; }
    public int StudentsSynced { get; set; }
    public string? Message { get; set; }
}

public interface ICourseSyncService
{
    /// <summary>Reads the source, cleans it and brings the website's copy up to date.</summary>
    Task<SyncRun> RunAsync(string trigger, CancellationToken cancellationToken = default);

    Task<SyncRun?> LastRunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Notified after each successful sync, e.g. so the CMS can create a page for every course.</summary>
public interface ICourseSyncObserver
{
    Task CoursesSyncedAsync(IReadOnlyList<SyncedCourse> activeCourses, CancellationToken cancellationToken);
}

/// <summary>Signals when the CMS schema and seed content are in place, so background sync can start.</summary>
public interface ISiteReadiness
{
    bool IsReady { get; }
    void MarkReady();
    Task WaitAsync(CancellationToken cancellationToken);
}

public sealed class SiteReadiness : ISiteReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    public void MarkReady() => _ready.TrySetResult();

    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);
}
