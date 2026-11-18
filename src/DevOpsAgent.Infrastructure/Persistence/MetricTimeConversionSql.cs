namespace DevOpsAgent.Infrastructure.Persistence;

/// <summary>
/// Rewrites metric timestamps between EF's old text form ("yyyy-MM-dd HH:mm:ss.fffffff+00:00") and UtcMillisecondsConverter's
/// integer ticks (T7.R7). Used by the migration; the Up step only touches rows still holding text.
/// </summary>
public static class MetricTimeConversionSql
{
    private const string EpochTicks = "621355968000000000"; // DateTime ticks at 1970-01-01T00:00:00Z

    /// <summary>Text to integer ticks, truncated to milliseconds. SQLite reads the "+HH:MM" suffix and converts to UTC.</summary>
    public static string Up(string table, string column) =>
        $"UPDATE \"{table}\" SET \"{column}\" = (CAST(ROUND((julianday(\"{column}\") - 2440587.5) * 86400000.0) AS INTEGER) * 10000) + {EpochTicks} "
        + $"WHERE typeof(\"{column}\") = 'text';";

    /// <summary>Integer ticks back to the text form EF used before, always in UTC.</summary>
    public static string Down(string table, string column) =>
        $"UPDATE \"{table}\" SET \"{column}\" = strftime('%Y-%m-%d %H:%M:%S', (\"{column}\" - {EpochTicks}) / 10000000, 'unixepoch') "
        + $"|| '.' || printf('%03d', ((\"{column}\" - {EpochTicks}) / 10000) % 1000) || '0000+00:00' "
        + $"WHERE typeof(\"{column}\") = 'integer';";
}
