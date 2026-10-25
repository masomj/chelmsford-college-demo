using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chelmsford.Infrastructure.Data;

internal static class DbExceptions
{
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    /// <summary>
    /// True when a save failed because a unique index or primary key already holds the value — the
    /// signal that a concurrent request got there first. (On SQL Server this would be errors 2601/2627.)
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey };
}
