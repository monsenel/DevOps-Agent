using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DevOpsAgent.Infrastructure.Persistence;

/// <summary>
/// Stores a timestamp as UTC ticks truncated to whole milliseconds (T7.R7). SQLite cannot translate or ORDER BY a
/// DateTimeOffset, but an integer compares and sorts correctly in SQL, so the metric time filters run in the database.
/// Values are read back as UTC. Sub-millisecond precision is not kept, which metric samples do not need.
/// </summary>
public sealed class UtcMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    value => ToStored(value),
    stored => new DateTimeOffset(stored, TimeSpan.Zero))
{
    private const long TicksPerMillisecond = TimeSpan.TicksPerMillisecond;

    public static long ToStored(DateTimeOffset value) => value.ToUniversalTime().UtcTicks / TicksPerMillisecond * TicksPerMillisecond;
}
