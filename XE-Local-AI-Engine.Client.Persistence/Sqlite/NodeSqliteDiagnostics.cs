namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using Microsoft.Extensions.Logging;
using SQLitePCL;

/// <summary>
///     Process-wide SQLite diagnostics: SQLite's own error log (<c>sqlite3_config_log</c>) and the raw-open pragma failures
///     reach the logger of every host attached in this process.
/// </summary>
/// <remarks>
///     The native hook is process-global, so it is registered once, on the first <see cref="Attach" />, and never replaced.
///     SQLite 3.42+ accepts <c>SQLITE_CONFIG_LOG</c> after initialization, so registering once the host is built still sees
///     every later message; earlier ones are counted, not logged. Each host detaches on shutdown, so a test process running
///     many hosts never logs into a disposed one. Native messages are rate-limited per attachment. Mechanism and bounds:
///     docs/wiki/08-data-and-persistence.md ("Connection pragmas").
/// </remarks>
public static class NodeSqliteDiagnostics
{
    internal const int NativeMessagesPerWindow = 20;

    // SQLite's text only, cut short: a prepare error appends the statement, which is bounded here, never logged whole.
    internal const int MaxNativeMessageLength = 256;

    private const int NotRegistered = -1;

    // SQLITE_WARNING | (1 << 8); SQLitePCL exposes no constant for it.
    private const int SqliteWarningAutoindex = 284;

    internal static readonly TimeSpan NativeMessageWindow = TimeSpan.FromMinutes(1);

    // Rooted for the process lifetime: SQLite keeps a native pointer to this delegate.
    private static readonly delegate_log NativeLogCallback = OnNativeLog;
    private static readonly Lock AttachmentsGate = new();
    private static volatile Attachment[] _attachments = [];
    private static int _nativeLogRegistrationResult = NotRegistered;
    private static long _nativeMessageCount;

    /// <summary>Forwards to every attached host logger; a no-op while none is attached.</summary>
    public static ILogger Logger { get; } = new AttachedLoggers();

    /// <summary>SQLite's result code for the one <c>sqlite3_config_log</c> call, or -1 before it ran.</summary>
    internal static int NativeLogRegistrationResult => Volatile.Read(ref _nativeLogRegistrationResult);

    /// <summary>Native messages SQLite delivered since registration, logged or not.</summary>
    internal static long NativeMessageCount => Interlocked.Read(ref _nativeMessageCount);

    /// <summary>
    ///     Routes SQLite diagnostics to <paramref name="logger" /> until the returned handle is disposed, registering the
    ///     native log hook on the first call. Safe to call repeatedly and concurrently.
    /// </summary>
    public static IDisposable Attach(ILogger logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var attachment = new Attachment(logger, timeProvider);
        lock (AttachmentsGate)
        {
            _attachments = [.. _attachments, attachment];

            // Under the gate, so a concurrent second attach returns only once the hook is in place.
            if (_nativeLogRegistrationResult == NotRegistered)
            {
                RegisterNativeLog(logger);
            }
        }

        return attachment;
    }

    private static void RegisterNativeLog(ILogger logger)
    {
        // Idempotent; Microsoft.Data.Sqlite runs the same init on its first connection, which may not have happened yet.
        Batteries_V2.Init();
        var result = raw.sqlite3_config_log(NativeLogCallback, null);
        Volatile.Write(ref _nativeLogRegistrationResult, result);
        if (result != raw.SQLITE_OK)
        {
            logger.LogWarning("Native SQLite refused the error-log hook (result code {ResultCode}); its own error messages will not be logged.", result);
        }
    }

    // Runs on whichever thread SQLite logged from, possibly under a SQLite mutex: it never calls back into SQLite and never
    // lets an exception unwind into native code. The message is decoded once, and only when some attachment will log it.
    private static void OnNativeLog(object userData, int resultCode, utf8z message)
    {
        _ = Interlocked.Increment(ref _nativeMessageCount);
        string? text = null;
        foreach (var attachment in _attachments)
        {
            try
            {
                attachment.WriteNative(resultCode, message, ref text);
            }
#pragma warning disable CA1031 // A logging failure must not unwind into SQLite; the message is lost, the database call is not.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Swallowed on purpose: see the comment on this method.
            }
        }
    }

    internal sealed class Attachment : IDisposable
    {
        private readonly Lock _windowGate = new();
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private long _windowStart;
        private int _windowCount;
        private int _disposed;

        public Attachment(ILogger logger, TimeProvider timeProvider)
        {
            _logger = logger;
            _timeProvider = timeProvider;
            _windowStart = timeProvider.GetTimestamp();
        }

        public ILogger Target => _logger;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Detach(this);
        }

        public void WriteNative(int resultCode, utf8z message, ref string? text)
        {
            // Routine, not a fault: kept out of the Warning budget so it can never crowd out a real one.
            if (IsBenign(resultCode))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    text ??= Bound(message.utf8_to_string());
                    _logger.LogDebug("Native SQLite reported result code {ResultCode}: {SqliteMessage}", resultCode, text);
                }

                return;
            }

            int count;
            lock (_windowGate)
            {
                var now = _timeProvider.GetTimestamp();
                if (_timeProvider.GetElapsedTime(_windowStart, now) >= NativeMessageWindow)
                {
                    _windowStart = now;
                    _windowCount = 0;
                }

                count = ++_windowCount;
            }

            if (count <= NativeMessagesPerWindow)
            {
                text ??= Bound(message.utf8_to_string());
                _logger.LogWarning("Native SQLite reported result code {ResultCode}: {SqliteMessage}", resultCode, text);
            }
            else if (count == NativeMessagesPerWindow + 1)
            {
                _logger.LogWarning("Native SQLite logged more than {Limit} messages within {Window}; further messages are suppressed until the window ends.",
                    NativeMessagesPerWindow,
                    NativeMessageWindow);
            }
        }

        // SQLITE_SCHEMA (17): the statement was re-prepared after a concurrent schema change and retried by SQLite itself.
        // SQLITE_WARNING_AUTOINDEX (284): the planner built a transient index, a performance hint. Everything else stays at Warning.
        internal static bool IsBenign(int resultCode) =>
            resultCode is raw.SQLITE_SCHEMA or SqliteWarningAutoindex;

        private static void Detach(Attachment attachment)
        {
            lock (AttachmentsGate)
            {
                _attachments = Array.FindAll(_attachments, candidate => !ReferenceEquals(candidate, attachment));
            }
        }

        private static string Bound(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message.Length <= MaxNativeMessageLength ? message : message[..MaxNativeMessageLength];
        }
    }

    private sealed class AttachedLoggers : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            Array.Exists(_attachments, attachment => attachment.Target.IsEnabled(logLevel));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            foreach (var attachment in _attachments)
            {
                attachment.Target.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
