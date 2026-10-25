using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Integration;
using Chelmsford.Infrastructure.Source;
using Chelmsford.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chelmsford.Tests;

/// <summary>End to end: JSON tender data → college source stand-in → read-only reader → website copy.</summary>
public sealed class IntegrationSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chelmsford-tests", Guid.NewGuid().ToString("N"));
    private readonly TestDatabase _database = new();
    private readonly TestClock _clock = new();
    private readonly IOptions<DataStoreOptions> _stores;
    private readonly IOptions<CollegeContentOptions> _content;

    public IntegrationSyncTests()
    {
        var dataFolder = Path.Combine(_root, "content", "source-data");
        Directory.CreateDirectory(dataFolder);
        File.WriteAllText(Path.Combine(dataFolder, "courses.json"), """
            [
              { "C_ID": 374, "C_TITLE": "Fashion Design", "C_SECTION": 48, "C_MODE": "FT", "C_SEOH1": "NULL",
                "C_FEES": "<p>&pound;150</p>_x000D_\n", "C_CONTACT": "<p>courses@example.ac.uk</p>",
                "C_STARTDATE": "2026-09-01 00:00:00", "C_DELETED": "NULL", "C_PUBLISHED": 1 },
              { "C_ID": 540, "C_TITLE": "Carpentry", "C_SECTION": 10, "C_MODE": "FT", "C_SEOH1": null,
                "C_DELETED": "NULL", "C_PUBLISHED": 1 },
              { "C_ID": "NULL", "C_TITLE": "NULL" }
            ]
            """);
        File.WriteAllText(Path.Combine(dataFolder, "course_sections.json"), """[{ "S_ID": 48, "S_TITLE": "Creative Design" }, { "S_ID": 10, "S_TITLE": "Construction" }]""");
        File.WriteAllText(Path.Combine(dataFolder, "course_levels.json"), """[{ "L_ID": 4, "L_TITLE": "Advanced (Level Three)" }, { "L_ID": "NULL", "L_TITLE": "NULL" }]""");
        File.WriteAllText(Path.Combine(dataFolder, "course_level_link.json"), """[{ "CL_ID": 1, "CL_COURSEID": 374, "CL_LEVELID": 4 }]""");
        File.WriteAllText(Path.Combine(dataFolder, "course_funding.json"), """[{ "F_ID": 1, "F_TITLE": "16-18" }]""");
        File.WriteAllText(Path.Combine(dataFolder, "course_fund_link.json"), """[{ "CF_ID": 1, "CF_COURSEID": 374, "CF_FUNDID": 1 }]""");

        var source = SqliteConnectionStrings.Resolve("Data Source=data/college-source.db;Pooling=False", _root);
        _stores = Options.Create(new DataStoreOptions { CollegeSourceConnectionString = source });
        _content = Options.Create(new CollegeContentOptions { ContentPath = Path.Combine(_root, "content") });
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the OS temp folder is cleaned up eventually.
        }
    }

    private SourceDatabaseSeeder Seeder() => new(_stores, _content, NullLogger<SourceDatabaseSeeder>.Instance);

    private SqliteCollegeSourceReader Reader() => new(_stores, NullLogger<SqliteCollegeSourceReader>.Instance);

    private async Task<SyncRun> SyncAsync(ICollegeSourceReader reader, params ICourseSyncObserver[] observers)
    {
        await using var db = _database.CreateContext();
        var service = new CourseSyncService(db, reader, new CourseMapper(new HtmlCleaner()), observers, new SyncGate(), _clock, NullLogger<CourseSyncService>.Instance);
        return await service.RunAsync("test");
    }

    [Fact]
    public async Task Seeder_keeps_raw_values_and_adds_demo_people()
    {
        Assert.True(await Seeder().EnsureCreatedAsync());
        Assert.False(await Seeder().EnsureCreatedAsync());

        var snapshot = await Reader().ReadAsync();

        Assert.Equal([374, 540], snapshot.Courses.Select(c => int.Parse(c["C_ID"]!)).Order());
        var fashion = snapshot.Courses.Single(c => c["C_ID"] == "374");
        Assert.Equal("<p>&pound;150</p>_x000D_\n", fashion["C_FEES"]);
        Assert.Equal("NULL", fashion["C_SEOH1"]);
        Assert.Null(snapshot.Courses.Single(c => c["C_ID"] == "540")["C_SEOH1"]);
        Assert.Single(snapshot.Levels);
        Assert.Equal(2, snapshot.Students.Count);
        Assert.Equal(new DateOnly(2009, 5, 14), snapshot.Students.Single(s => s.StudentId == "21004567").DateOfBirth);
        Assert.Equal("APP-2027-00412", Assert.Single(snapshot.Applications).Reference);
    }

    [Fact]
    public async Task Reader_opens_the_source_read_only()
    {
        await Seeder().EnsureCreatedAsync();
        var builder = new SqliteConnectionStringBuilder(_stores.Value.CollegeSourceConnectionString) { Mode = SqliteOpenMode.ReadOnly };

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM courses";

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Missing_source_data_still_creates_empty_tables()
    {
        Directory.Delete(Path.Combine(_root, "content"), recursive: true);

        await Seeder().EnsureCreatedAsync();
        var snapshot = await Reader().ReadAsync();

        Assert.Empty(snapshot.Courses);
        Assert.Equal(2, snapshot.Students.Count);
    }

    [Fact]
    public async Task Sync_adds_then_skips_unchanged_courses_and_people()
    {
        await Seeder().EnsureCreatedAsync();
        var observer = new RecordingObserver();

        var first = await SyncAsync(Reader(), observer);
        var second = await SyncAsync(Reader(), observer);

        Assert.Equal(SyncStatus.Succeeded, first.Status);
        Assert.Equal((2, 2, 0, 0), (first.CoursesRead, first.Added, first.Updated, first.Unchanged));
        Assert.Equal((0, 2), (second.Added, second.Unchanged));
        Assert.Equal(2, observer.Calls.Last().Count);

        await using var db = _database.CreateContext();
        var fashion = await db.SyncedCourses.SingleAsync(c => c.Id == 374);
        Assert.Equal("Level 3", fashion.LevelShort);
        Assert.Equal("<p>£150</p>", fashion.FeesHtml);
        Assert.Equal(CourseVocabulary.Funding16To18, fashion.Funding);
        Assert.Equal(2, await db.SyncedStudents.CountAsync());
        Assert.Equal(1, await db.SyncedApplications.CountAsync());
        Assert.Equal(2, await db.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task Courses_gone_from_the_source_are_flagged_removed()
    {
        await Seeder().EnsureCreatedAsync();
        await SyncAsync(Reader());
        var full = await Reader().ReadAsync();
        var reduced = new StubReader(full with { Courses = full.Courses.Where(c => c["C_ID"] == "374").ToList() });

        var run = await SyncAsync(reduced);

        Assert.Equal(1, run.Removed);
        await using var db = _database.CreateContext();
        Assert.True((await db.SyncedCourses.SingleAsync(c => c.Id == 540)).RemovedFromSource);
    }

    [Fact]
    public async Task A_failed_read_records_a_failed_run_and_keeps_the_last_good_copy()
    {
        await Seeder().EnsureCreatedAsync();
        await SyncAsync(Reader());
        var observer = new RecordingObserver();

        var run = await SyncAsync(new StubReader(null), observer);

        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.Contains("source offline", run.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Calls);
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.SyncedCourses.CountAsync(c => !c.RemovedFromSource));
        Assert.Equal(SyncStatus.Failed, (await db.SyncRuns.OrderByDescending(r => r.Id).FirstAsync()).Status);
    }

    [Fact]
    public async Task An_observer_failure_does_not_fail_the_sync()
    {
        await Seeder().EnsureCreatedAsync();

        var run = await SyncAsync(Reader(), new ThrowingObserver());

        Assert.Equal(SyncStatus.Succeeded, run.Status);
    }

    private sealed class StubReader(SourceSnapshot? snapshot) : ICollegeSourceReader
    {
        public Task<SourceSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            snapshot is null ? throw new InvalidOperationException("source offline") : Task.FromResult(snapshot);
    }

    private sealed class RecordingObserver : ICourseSyncObserver
    {
        public List<IReadOnlyList<SyncedCourse>> Calls { get; } = [];

        public Task CoursesSyncedAsync(IReadOnlyList<SyncedCourse> activeCourses, CancellationToken cancellationToken)
        {
            Calls.Add(activeCourses);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : ICourseSyncObserver
    {
        public Task CoursesSyncedAsync(IReadOnlyList<SyncedCourse> activeCourses, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("CMS unavailable");
    }
}
