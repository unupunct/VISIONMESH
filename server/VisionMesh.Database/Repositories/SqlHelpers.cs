using System.Data;
using Microsoft.Data.Sqlite;

namespace VisionMesh.Database.Repositories;

/// <summary>
/// Small reader helpers. Timestamps are stored as ISO-8601 round-trip strings so the
/// database stays human-readable and timezone-unambiguous.
/// </summary>
internal static class SqlHelpers
{
    public static string GetString(this IDataRecord r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    public static string? GetStringOrNull(this IDataRecord r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static int GetInt(this IDataRecord r, int i) => r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    public static long GetLong(this IDataRecord r, int i) => r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i));
    public static bool GetBool(this IDataRecord r, int i) => !r.IsDBNull(i) && Convert.ToInt64(r.GetValue(i)) != 0;
    public static double? GetDoubleOrNull(this IDataRecord r, int i) => r.IsDBNull(i) ? null : Convert.ToDouble(r.GetValue(i));

    public static DateTimeOffset GetTimestamp(this IDataRecord r, int i)
        => r.IsDBNull(i) ? default : DateTimeOffset.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? GetTimestampOrNull(this IDataRecord r, int i)
        => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), null, System.Globalization.DateTimeStyles.RoundtripKind);

    public static string ToDb(this DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    public static object ToDbOrNull(this DateTimeOffset? value) => value.HasValue ? value.Value.ToUniversalTime().ToString("O") : DBNull.Value;
    public static object OrNull(this string? value) => string.IsNullOrEmpty(value) ? DBNull.Value : value;
    public static object OrNull(this double? value) => value.HasValue ? value.Value : DBNull.Value;

    public static SqliteCommand Command(this SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
