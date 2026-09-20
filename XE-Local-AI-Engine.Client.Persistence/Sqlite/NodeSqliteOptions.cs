namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

/// <summary>
///     Connection-time pragmas applied to every node SQLite connection (chat/identity EF contexts, the raw-ADO
///     persistence helpers, and — transitively, since WAL is a file-level property — the shared Quartz job store).
///     Bound from the <c>NodeSqlite</c> configuration section.
/// </summary>
/// <remarks>
///     Defaults are chosen for a single-file desktop database with several concurrent in-process writers (per-conversation
///     chat writes, KB ingestion, memory extraction, the scheduler):
///     <list type="bullet">
///         <item>
///             <b>WAL</b> lets readers run without blocking the single writer, which is the dominant contention pattern
///             here (frequent reads racing occasional writes). It is a persistent database property, so enabling it once
///             covers every connection to the file, including Quartz's.
///         </item>
///         <item>
///             <b>busy_timeout = 5000 ms</b> makes a writer that meets a held write lock wait-and-retry inside SQLite for
///             up to five seconds instead of failing instantly with <c>SQLITE_BUSY</c>. Five seconds comfortably covers a
///             checkpoint or a large encrypted batch write while still surfacing a genuine deadlock/stall rather than
///             hanging a request indefinitely.
///         </item>
///         <item>
///             <b>synchronous = NORMAL</b> is the standard WAL pairing: durable across application crashes, and on OS/power
///             loss it can only lose transactions committed since the last checkpoint (never corrupt the database). That
///             trade is appropriate for a local chat database and is the SQLite-recommended default under WAL.
///         </item>
///     </list>
///     Foreign-key enforcement is ON, and is emitted explicitly. It was long believed to be off here, because the node
///     builds a bare <c>Data Source=</c> connection string and <c>SqliteConnectionStringBuilder.ForeignKeys</c> defaults
///     to null, so Microsoft.Data.Sqlite sends no pragma of its own. The bundled <c>e_sqlite3</c> is compiled with
///     <c>DEFAULT_FOREIGN_KEYS</c>, so SQLite's own default is on and every declared <c>ON DELETE CASCADE</c> has always
///     fired. Resting referential integrity on a native build's compile flag is not a decision anyone made, so the
///     connection string now says <c>Foreign Keys=True</c> and <see cref="NodeSqlitePragmas" /> emits
///     <c>PRAGMA foreign_keys=ON</c> on every open — the latter covers connection strings this process does not build
///     itself (the Aspire dev integration's, or an operator-supplied one).
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
        return new NodeSqlitePragmaSettings { EnableWriteAheadLog = EnableWriteAheadLog, BusyTimeoutMilliseconds = busyTimeout, Synchronous = Synchronous };
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
    public static NodeSqlitePragmaSettings Default { get; } = new() { EnableWriteAheadLog = true, BusyTimeoutMilliseconds = 5000, Synchronous = NodeSqliteSynchronousMode.Normal };
}
