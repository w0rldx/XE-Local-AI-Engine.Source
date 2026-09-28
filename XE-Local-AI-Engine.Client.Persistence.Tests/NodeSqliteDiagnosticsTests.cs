namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     SQLite's own error log reaches the attached host loggers: the process-global hook is registered once however many
///     hosts attach, a detached logger stops receiving, and one attachment's native messages are bounded per window.
/// </summary>
/// <remarks>
///     Other tests in this process attach hosts and provoke SQLite errors concurrently, so every assertion keys on a
///     table name unique to the test and never on a total.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class NodeSqliteDiagnosticsTests
{
    [Test]
    public async Task Attach_RegistersTheNativeHookOnce_AndDeliversSqlitesOwnMessage()
    {
        var first = new CapturingLogger();
        var second = new CapturingLogger();
        using var firstAttachment = NodeSqliteDiagnostics.Attach(first, TimeProvider.System);
        using var secondAttachment = NodeSqliteDiagnostics.Attach(second, TimeProvider.System);

        AssertEx.Equal(raw.SQLITE_OK, NodeSqliteDiagnostics.NativeLogRegistrationResult,
            "Repeated attaches must leave the single native registration in place, not re-register or fail it.");

        var before = NodeSqliteDiagnostics.NativeMessageCount;
        var table = await ProvokeMissingTableErrorAsync();

        AssertEx.True(NodeSqliteDiagnostics.NativeMessageCount > before, "SQLite must have called the registered hook.");
        AssertEx.True(first.Warnings.Any(entry => entry.Contains(table, StringComparison.Ordinal)),
            "The first attached logger must receive SQLite's own message.");
        AssertEx.True(second.Warnings.Any(entry => entry.Contains(table, StringComparison.Ordinal)),
            "Every attached logger receives it, so a second host in the process does not displace the first.");
    }

    [Test]
    public async Task DisposedAttachment_StopsReceivingNativeMessages()
    {
        var logger = new CapturingLogger();
        var attachment = NodeSqliteDiagnostics.Attach(logger, TimeProvider.System);
        attachment.Dispose();
        attachment.Dispose();

        var table = await ProvokeMissingTableErrorAsync();

        AssertEx.False(logger.Warnings.Any(entry => entry.Contains(table, StringComparison.Ordinal)),
            "A host that stopped must not keep receiving the process-wide SQLite log.");
    }

    [Test]
    public void NativeMessages_AreBoundedPerWindow_WithOneSuppressionNotice()
    {
        var logger = new CapturingLogger();
        var time = new SteppedTimeProvider();
        using var attachment = new NodeSqliteDiagnostics.Attachment(logger, time);
        var message = utf8z.FromString("misuse at line 1 " + new string('x', NodeSqliteDiagnostics.MaxNativeMessageLength));

        for (var i = 0; i < NodeSqliteDiagnostics.NativeMessagesPerWindow + 5; i++)
        {
            string? text = null;
            attachment.WriteNative(21, message, ref text);
        }

        var logged = logger.Warnings.ToList();
        AssertEx.Equal(NodeSqliteDiagnostics.NativeMessagesPerWindow + 1, logged.Count,
            "A misuse storm logs the window's budget plus one suppression notice, then nothing.");
        AssertEx.True(logged[0].Contains("result code 21", StringComparison.Ordinal), "The result code must be logged.");
        AssertEx.True(logged[0].Length < NodeSqliteDiagnostics.MaxNativeMessageLength + 80,
            "SQLite's message must be cut to the bound, never logged whole.");
        AssertEx.True(logged[^1].Contains("suppressed", StringComparison.Ordinal), "The last entry must announce the suppression.");

        time.Advance(NodeSqliteDiagnostics.NativeMessageWindow);
        string? next = null;
        attachment.WriteNative(21, message, ref next);

        AssertEx.Equal(NodeSqliteDiagnostics.NativeMessagesPerWindow + 2, logger.Warnings.Count,
            "A new window must log again.");
    }

    [Test]
    [Arguments(17)]
    [Arguments(284)]
    public void RoutineNativeCodes_LogAtDebug_AndLeaveTheWarningBudgetUntouched(int resultCode)
    {
        // Live: a re-prepared statement (SQLITE_SCHEMA) and a planner autoindex hint (SQLITE_WARNING_AUTOINDEX) read as faults at Warning.
        var logger = new CapturingLogger(captureDebug: true);
        using var attachment = new NodeSqliteDiagnostics.Attachment(logger, new SteppedTimeProvider());
        var routine = utf8z.FromString("routine");
        var fault = utf8z.FromString("misuse");

        for (var i = 0; i < NodeSqliteDiagnostics.NativeMessagesPerWindow + 5; i++)
        {
            string? text = null;
            attachment.WriteNative(resultCode, routine, ref text);
        }

        string? faultText = null;
        attachment.WriteNative(21, fault, ref faultText);

        AssertEx.Equal(NodeSqliteDiagnostics.NativeMessagesPerWindow + 5, logger.Debugs.Count);
        AssertEx.True(logger.Debugs.All(entry => entry.Contains($"result code {resultCode}", StringComparison.Ordinal)));
        AssertEx.Equal(expected: 1, logger.Warnings.Count, "routine codes neither warn nor use up the window, so the real fault still logs");
        AssertEx.True(logger.Warnings.Single().Contains("result code 21", StringComparison.Ordinal));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only table name built from a Guid.")]
    private static async Task<string> ProvokeMissingTableErrorAsync()
    {
        var table = "missing_" + Guid.NewGuid().ToString("N");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};";
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        return table;
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly bool _captureDebug;

        public CapturingLogger(bool captureDebug = false)
        {
            _captureDebug = captureDebug;
        }

        public ConcurrentQueue<string> Warnings { get; } = new();

        public ConcurrentQueue<string> Debugs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel == LogLevel.Warning || (_captureDebug && logLevel == LogLevel.Debug);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
            else if (logLevel == LogLevel.Debug)
            {
                Debugs.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class SteppedTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan by) =>
            Interlocked.Add(ref _timestamp, by.Ticks);
    }
}
