namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     run logger <see cref="IAgentHomeRunLogger" />. Appends structured JSONL records to the four
///     host-side log files under <c>runs/&lt;run-id&gt;/logs/</c>. All writes are sequential within a
///     single run (one append per call); concurrent callers for different runs each hold independent
///     instances (the run gateway constructs one per run). Raw host paths and secrets are never written — the
///     caller is responsible for supplying model-safe values under the two-root host/sandbox split.
/// </summary>
internal sealed class AgentHomeRunLogger : IAgentHomeRunLogger
{
    // Cached options: camelCase + enum-as-string + no cycles. Reused across all appends (CA1869).
    private static readonly JsonSerializerOptions LogJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeProvider _timeProvider;
    private AgentHomeRunLogContext? _context;

    public AgentHomeRunLogger(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task OpenAsync(AgentHomeRunLogContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        _context = context;

        var record = new
        {
            timestampUtc = _timeProvider.GetUtcNow(),
            eventName = "started",
            runId = context.RunId,
            nodeId = context.NodeId,
            ownerUserId = context.OwnerUserId,
            providerName = context.ProviderName
        };

        await AppendLineAsync(EventsFile(), record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendEventAsync(string eventName, string? detail = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        var ctx = RequireContext();
        cancellationToken.ThrowIfCancellationRequested();

        var record = new
        {
            timestampUtc = _timeProvider.GetUtcNow(),
            eventName,
            runId = ctx.RunId,
            nodeId = ctx.NodeId,
            ownerUserId = ctx.OwnerUserId,
            providerName = ctx.ProviderName,
            detail
        };

        await AppendLineAsync(EventsFile(), record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendCommandAsync(AgentHomeCommandLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var ctx = RequireContext();
        cancellationToken.ThrowIfCancellationRequested();

        // Wrap with correlation envelope; the record itself carries the per-command fields.
        var envelope = new
        {
            record.TimestampUtc,
            runId = ctx.RunId,
            nodeId = ctx.NodeId,
            ownerUserId = ctx.OwnerUserId,
            providerName = ctx.ProviderName,
            record.ExecutionId,
            record.Executable,
            record.Arguments,
            record.Completed,
            record.ExitCode,
            record.DurationMs,
            record.ErrorClass
        };

        await AppendLineAsync(CommandsFile(), envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AppendToolCallAsync(AgentHomeToolCallLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var ctx = RequireContext();
        cancellationToken.ThrowIfCancellationRequested();

        // Wrap with node/owner correlation; the record already carries run-id + per-call fields.
        var envelope = new
        {
            record.TimestampUtc,
            record.RunId,
            nodeId = ctx.NodeId,
            ownerUserId = ctx.OwnerUserId,
            providerName = ctx.ProviderName,
            record.ToolName,
            record.Location,
            record.ApprovalId,
            record.Status,
            record.ArgumentSummary,
            record.RedactionApplied,
            record.DurationMs,
            record.ErrorClass
        };

        await AppendLineAsync(ToolCallsFile(), envelope, cancellationToken).ConfigureAwait(false);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private AgentHomeRunLogContext RequireContext()
    {
        return _context ?? throw new InvalidOperationException("AgentHomeRunLogger.OpenAsync must be called before any append operation.");
    }

    private string EventsFile()
    {
        return Path.Combine(RequireContext().HostLogDirectory, "events.jsonl");
    }

    private string CommandsFile()
    {
        return Path.Combine(RequireContext().HostLogDirectory, "commands.jsonl");
    }

    private string ToolCallsFile()
    {
        return Path.Combine(RequireContext().HostLogDirectory, "tool-calls.jsonl");
    }

    private static async Task AppendLineAsync<T>(string filePath, T record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, LogJsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(filePath, line, cancellationToken).ConfigureAwait(false);
    }
}
