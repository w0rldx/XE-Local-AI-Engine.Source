namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using Microsoft.Data.Sqlite;

/// <summary>
///     Classifies and records node SQLite write-contention failures (SQLITE_BUSY / SQLITE_LOCKED) that outlived the
///     connection's <c>busy_timeout</c>. Centralized so every observation increments <see cref="NodeMetrics.SqliteBusyTotal" />
///     with the same bounded dimensions (never any SQL text). Used by the EF command interceptor and the raw chat-write
///     boundary.
/// </summary>
public static class NodeSqliteContention
{
    // Primary result codes: SQLITE_BUSY = 5, SQLITE_LOCKED = 6.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    /// <summary>
    ///     Returns the contention code ("busy" / "locked") when <paramref name="exception" /> (or an inner exception) is a
    ///     SQLITE_BUSY / SQLITE_LOCKED <see cref="SqliteException" />, otherwise null.
    /// </summary>
    public static string? Classify(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite.SqliteErrorCode switch
                {
                    SqliteBusy => "busy",
                    SqliteLocked => "locked",
                    _ => null
                };
            }
        }

        return null;
    }

    /// <summary>
    ///     Records a contention failure if <paramref name="exception" /> is SQLITE_BUSY / SQLITE_LOCKED. No-op otherwise,
    ///     so callers can hand it any failure. <paramref name="path" /> is a bounded dimension (e.g. "ef", "raw").
    /// </summary>
    public static void Record(string path, Exception? exception, ILogger? logger = null)
    {
        var code = Classify(exception);
        if (code is null)
        {
            return;
        }

        NodeMetrics.SqliteBusyTotal.Add(1,
            new KeyValuePair<string, object?>("code", code),
            new KeyValuePair<string, object?>("path", path));

        logger?.LogWarning("Node SQLite write contention ({Code}) surfaced on the {Path} path after the busy timeout.", code, path);
    }
}
