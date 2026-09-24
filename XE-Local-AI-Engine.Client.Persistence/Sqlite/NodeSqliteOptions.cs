namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

/// <summary>
///     Connection-time pragmas applied to every node SQLite connection, bound from the <c>NodeSqlite</c> configuration
///     section.
/// </summary>
/// <remarks>
///     Covers the chat/identity EF contexts, the raw-ADO persistence helpers and — transitively, since WAL is a
///     file-level property — the shared Quartz job store. The defaults suit a single-file desktop database with several
///     concurrent in-process writers. Foreign keys are enforced: the connection string says <c>Foreign Keys=True</c>
///     and <see cref="NodeSqlitePragmas" /> emits <c>PRAGMA foreign_keys=ON</c>, so the declared cascades never rest on
///     the native build's default. Per-pragma rationale: docs/wiki/08-data-and-persistence.md ("Connection pragmas").
/// </remarks>
public sealed class NodeSqliteOptions
{
    public const string Section = "NodeSqlite";

    internal const int MaxBusyTimeoutMilliseconds = 120_000;

    /// <summary>When true (default), connections are switched to WAL journaling and <c>synchronous=NORMAL</c>.</summary>
    public bool EnableWriteAheadLog { get; init; } = true;

    /// <summary>Native <c>PRAGMA busy_timeout</c> in milliseconds applied to every connection. Default 5000.</summary>
    public int BusyTimeoutMilliseconds { get; init; } = 5000;

    /// <summary>The <c>synchronous</c> level applied when WAL is enabled. Default <see cref="NodeSqliteSynchronousMode.Normal" />.</summary>
    public NodeSqliteSynchronousMode Synchronous { get; init; } = NodeSqliteSynchronousMode.Normal;

    /// <summary>Projects the bound options to the immutable settings the pragma applier consumes, clamping out-of-range values.</summary>
    public NodeSqlitePragmaSettings ToSettings()
    {
        var busyTimeout = Math.Clamp(BusyTimeoutMilliseconds, min: 0, MaxBusyTimeoutMilliseconds);
        return new NodeSqlitePragmaSettings
        {
            EnableWriteAheadLog = EnableWriteAheadLog,
            BusyTimeoutMilliseconds = busyTimeout,
            Synchronous = Synchronous
        };
    }
}

/// <summary>SQLite <c>synchronous</c> levels (the subset that pairs with WAL). Values match the pragma's integer codes.</summary>
public enum NodeSqliteSynchronousMode
{
    /// <summary><c>synchronous=OFF</c> (0) — fastest, least durable. Not recommended.</summary>
    Off = 0,

    /// <summary><c>synchronous=NORMAL</c> (1) — the standard WAL pairing.</summary>
    Normal = 1,

    /// <summary><c>synchronous=FULL</c> (2) — maximum durability, slower commits.</summary>
    Full = 2
}

/// <summary>
///     Immutable, effective pragma settings the applier executes. A reference type so a single instance can be swapped
///     atomically into the process-wide default used by the static raw-open helpers.
/// </summary>
public sealed class NodeSqlitePragmaSettings
{
    public required bool EnableWriteAheadLog { get; init; }

    public required int BusyTimeoutMilliseconds { get; init; }

    public required NodeSqliteSynchronousMode Synchronous { get; init; }

    /// <summary>The built-in defaults, used until the composition root supplies configured values.</summary>
    public static NodeSqlitePragmaSettings Default { get; } = new()
    {
        EnableWriteAheadLog = true,
        BusyTimeoutMilliseconds = 5000,
        Synchronous = NodeSqliteSynchronousMode.Normal
    };
}
