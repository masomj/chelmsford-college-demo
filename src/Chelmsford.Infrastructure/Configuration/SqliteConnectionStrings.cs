using Microsoft.Data.Sqlite;

namespace Chelmsford.Infrastructure.Configuration;

/// <summary>Helpers for SQLite connection strings whose file paths are relative to the app's content root.</summary>
public static class SqliteConnectionStrings
{
    /// <summary>
    /// Makes a relative <c>Data Source</c> absolute against <paramref name="contentRootPath"/> and, when
    /// <paramref name="createDirectory"/> is set, creates the folder so SQLite can create the file.
    /// </summary>
    public static string Resolve(string connectionString, string contentRootPath, bool createDirectory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (IsInMemory(builder))
        {
            return builder.ToString();
        }

        var path = Path.IsPathRooted(builder.DataSource)
            ? builder.DataSource
            : Path.GetFullPath(Path.Combine(contentRootPath, builder.DataSource));
        builder.DataSource = path;

        if (createDirectory && Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        return builder.ToString();
    }

    /// <summary>The database file path, or null for an in-memory database.</summary>
    public static string? FilePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return IsInMemory(builder) ? null : builder.DataSource;
    }

    private static bool IsInMemory(SqliteConnectionStringBuilder builder) =>
        builder.Mode == SqliteOpenMode.Memory
        || string.IsNullOrWhiteSpace(builder.DataSource)
        || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase);
}
