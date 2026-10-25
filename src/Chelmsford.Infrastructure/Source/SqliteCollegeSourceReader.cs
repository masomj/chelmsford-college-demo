using System.Globalization;
using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Source;

/// <summary>
/// Reads the college's existing tables. The connection is opened with <c>Mode=ReadOnly</c>, so the
/// integration layer cannot write to the source even by mistake.
/// </summary>
public sealed class SqliteCollegeSourceReader(
    IOptions<DataStoreOptions> dataStores,
    ILogger<SqliteCollegeSourceReader> logger) : ICollegeSourceReader
{
    public async Task<SourceSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(dataStores.Value.CollegeSourceConnectionString)
        {
            Mode = SqliteOpenMode.ReadOnly
        };

        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);

        var courses = (await ReadRowsAsync(connection, SourceTables.Courses, cancellationToken))
            .Where(row => SourceTables.ParseId(Get(row, "C_ID")) is not null)
            .Select(row => new SourceCourse(row))
            .ToList();

        var sections = (await ReadRowsAsync(connection, SourceTables.Sections, cancellationToken))
            .Select(row => (Id: SourceTables.ParseId(Get(row, "S_ID")), Title: Get(row, "S_TITLE")))
            .Where(s => s.Id is not null)
            .Select(s => new SourceSection(s.Id!.Value, s.Title))
            .ToList();

        var levels = (await ReadRowsAsync(connection, SourceTables.Levels, cancellationToken))
            .Select(row => (Id: SourceTables.ParseId(Get(row, "L_ID")), Title: Get(row, "L_TITLE")))
            .Where(l => l.Id is not null)
            .Select(l => new SourceLevel(l.Id!.Value, l.Title))
            .ToList();

        var funding = (await ReadRowsAsync(connection, SourceTables.Funding, cancellationToken))
            .Select(row => (Id: SourceTables.ParseId(Get(row, "F_ID")), Title: Get(row, "F_TITLE")))
            .Where(f => f.Id is not null)
            .Select(f => new SourceFunding(f.Id!.Value, f.Title))
            .ToList();

        var levelLinks = Links(await ReadRowsAsync(connection, SourceTables.LevelLinks, cancellationToken), "CL_COURSEID", "CL_LEVELID");
        var fundingLinks = Links(await ReadRowsAsync(connection, SourceTables.FundingLinks, cancellationToken), "CF_COURSEID", "CF_FUNDID");

        var students = await ReadStudentsAsync(connection, cancellationToken);
        var applications = await ReadApplicationsAsync(connection, cancellationToken);

        logger.LogDebug(
            "Read {Courses} courses, {Students} students and {Applications} applications from the college source",
            courses.Count,
            students.Count,
            applications.Count);

        return new SourceSnapshot(courses, sections, levels, levelLinks, funding, fundingLinks, students, applications);
    }

    private static string? Get(IReadOnlyDictionary<string, string?> row, string column) =>
        row.TryGetValue(column, out var value) ? value : null;

    private static List<SourceLink> Links(IEnumerable<IReadOnlyDictionary<string, string?>> rows, string courseColumn, string targetColumn) =>
        rows.Select(row => (Course: SourceTables.ParseId(Get(row, courseColumn)), Target: SourceTables.ParseId(Get(row, targetColumn))))
            .Where(link => link.Course is not null && link.Target is not null)
            .Select(link => new SourceLink(link.Course!.Value, link.Target!.Value))
            .ToList();

    /// <summary>Reads every column of every row as raw text, keyed by the college's column names.</summary>
    private static async Task<List<IReadOnlyDictionary<string, string?>>> ReadRowsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {SourceTables.Quote(table)}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<IReadOnlyDictionary<string, string?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, string?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken)
                    ? null
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static async Task<List<SourceStudent>> ReadStudentsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT student_id, first_name, last_name, date_of_birth, email, course_id FROM {SourceTables.Students}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var students = new List<SourceStudent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!DateOnly.TryParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOfBirth))
            {
                continue;
            }

            students.Add(new SourceStudent(
                reader.GetString(0).Trim(),
                reader.GetString(1).Trim(),
                reader.GetString(2).Trim(),
                dateOfBirth,
                reader.GetString(4).Trim(),
                reader.GetInt32(5)));
        }

        return students;
    }

    private static async Task<List<SourceApplication>> ReadApplicationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT app_ref, first_name, last_name, email, course_id FROM {SourceTables.Applications}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applications = new List<SourceApplication>();
        while (await reader.ReadAsync(cancellationToken))
        {
            applications.Add(new SourceApplication(
                reader.GetString(0).Trim(),
                reader.GetString(1).Trim(),
                reader.GetString(2).Trim(),
                reader.GetString(3).Trim(),
                reader.GetInt32(4)));
        }

        return applications;
    }
}
