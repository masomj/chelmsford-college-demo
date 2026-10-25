using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Chelmsford.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Source;

/// <summary>
/// Builds the stand-in for the college's existing database: a SQLite file using the college's own
/// table and column names, filled from the tender pack's course data with every quirk kept
/// ('NULL' text, raw HTML, _x000D_ artefacts). Only runs when the file does not exist yet.
/// </summary>
public sealed class SourceDatabaseSeeder(
    IOptions<DataStoreOptions> dataStores,
    IOptions<CollegeContentOptions> content,
    ILogger<SourceDatabaseSeeder> logger)
{
    private static readonly DemoStudent[] DemoStudents =
    [
        new("21004567", "Ellie", "Turner", "2009-05-14", "ellie.turner@students.example", 374),
        new("21004890", "Josh", "Patel", "2008-11-02", "josh.patel@students.example", 540)
    ];

    private static readonly DemoApplication[] DemoApplications =
    [
        new("APP-2027-00412", "Ruby", "Hughes", "ruby.hughes@example.com", 482)
    ];

    /// <summary>Creates the source database if it is missing. Returns true when a new file was created.</summary>
    public async Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var path = SqliteConnectionStrings.FilePath(dataStores.Value.CollegeSourceConnectionString);
        if (path is null)
        {
            logger.LogWarning("The college source database is in memory, so it cannot be seeded");
            return false;
        }

        if (File.Exists(path))
        {
            return false;
        }

        // Build into a temporary file and move it into place, so a failed seed never leaves a half-built source.
        var temporaryPath = path + ".seeding";
        File.Delete(temporaryPath);
        try
        {
            await BuildAsync(temporaryPath, cancellationToken);
            File.Move(temporaryPath, path);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }

        logger.LogInformation("Created the college source database at {Path}", path);
        return true;
    }

    private async Task BuildAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var dataFolder = Path.Combine(content.Value.ContentPath, "source-data");
        var folderExists = Directory.Exists(dataFolder);
        if (!folderExists)
        {
            logger.LogWarning(
                "No course data found at {Folder}. Creating empty course tables so the site still starts",
                dataFolder);
        }

        foreach (var (table, requiredColumns) in SourceTables.CourseTables)
        {
            var file = Path.Combine(dataFolder, table + ".json");
            if (folderExists && File.Exists(file))
            {
                var rows = await ImportJsonTableAsync(connection, transaction, table, requiredColumns, file, cancellationToken);
                logger.LogInformation("Imported {Rows} rows into {Table}", rows, table);
            }
            else
            {
                if (folderExists)
                {
                    logger.LogWarning("Missing source file {File}; creating an empty {Table} table", file, table);
                }

                await CreateTextTableAsync(connection, transaction, table, requiredColumns, cancellationToken);
            }
        }

        await CreatePeopleTablesAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> ImportJsonTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<string> requiredColumns,
        string file,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{file} must contain a JSON array of rows.");
        }

        // Column order: as first seen in the file, then any required column the file lacks.
        var columns = new List<string>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{file} contains a row that is not a JSON object.");
            }

            foreach (var property in row.EnumerateObject())
            {
                if (known.Add(property.Name))
                {
                    columns.Add(property.Name);
                }
            }
        }

        columns.AddRange(requiredColumns.Where(known.Add));
        await CreateTextTableAsync(connection, transaction, table, columns, cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            $"INSERT INTO {SourceTables.Quote(table)} ({string.Join(", ", columns.Select(SourceTables.Quote))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)))})";
        var parameters = columns
            .Select((_, i) => insert.Parameters.Add("$p" + i.ToString(CultureInfo.InvariantCulture), SqliteType.Text))
            .ToArray();

        var count = 0;
        foreach (var row in document.RootElement.EnumerateArray())
        {
            for (var i = 0; i < columns.Count; i++)
            {
                parameters[i].Value = row.TryGetProperty(columns[i], out var value) ? RawValue(value) : DBNull.Value;
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
            count++;
        }

        return count;
    }

    /// <summary>Keeps the value exactly as the college stores it: strings verbatim, numbers as written, null as NULL.</summary>
    private static object RawValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? (object)DBNull.Value,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        _ => value.GetRawText()
    };

    private static async Task CreateTextTableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IEnumerable<string> columns,
        CancellationToken cancellationToken)
    {
        var definition = string.Join(", ", columns.Select(c => SourceTables.Quote(c) + " TEXT"));
        await ExecuteAsync(connection, transaction, $"CREATE TABLE {SourceTables.Quote(table)} ({definition})", cancellationToken);
    }

    private static async Task CreatePeopleTablesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE TABLE {SourceTables.Students} (student_id TEXT PRIMARY KEY, first_name TEXT NOT NULL, last_name TEXT NOT NULL, " +
            "date_of_birth TEXT NOT NULL, email TEXT NOT NULL, course_id INTEGER NOT NULL)",
            cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE TABLE {SourceTables.Applications} (app_ref TEXT PRIMARY KEY, first_name TEXT NOT NULL, last_name TEXT NOT NULL, " +
            "email TEXT NOT NULL, course_id INTEGER NOT NULL)",
            cancellationToken);

        foreach (var student in DemoStudents)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {SourceTables.Students} (student_id, first_name, last_name, date_of_birth, email, course_id) " +
                "VALUES ($id, $first, $last, $dob, $email, $course)",
                cancellationToken,
                ("$id", student.StudentId),
                ("$first", student.FirstName),
                ("$last", student.LastName),
                ("$dob", student.DateOfBirth),
                ("$email", student.Email),
                ("$course", student.CourseId));
        }

        foreach (var application in DemoApplications)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"INSERT INTO {SourceTables.Applications} (app_ref, first_name, last_name, email, course_id) " +
                "VALUES ($ref, $first, $last, $email, $course)",
                cancellationToken,
                ("$ref", application.Reference),
                ("$first", application.FirstName),
                ("$last", application.LastName),
                ("$email", application.Email),
                ("$course", application.CourseId));
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record DemoStudent(string StudentId, string FirstName, string LastName, string DateOfBirth, string Email, int CourseId);

    private sealed record DemoApplication(string Reference, string FirstName, string LastName, string Email, int CourseId);
}
