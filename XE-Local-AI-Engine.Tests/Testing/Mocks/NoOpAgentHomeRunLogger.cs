namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     An <see cref="IAgentHomeRunLogger" /> that records what it was asked to write and puts nothing on disk, for the
///     tests whose subject is the thing being logged rather than the logging. The real file-writing logger and its
///     redaction contract are graded by <c>AgentHomeRunLoggerTests</c>.
/// </summary>
internal sealed class NoOpAgentHomeRunLogger : IAgentHomeRunLogger
{
    /// <summary>The command records appended, in order — enough to assert WHO ran what without a temp directory.</summary>
    public List<AgentHomeCommandLogRecord> Commands { get; } = [];

    public Task OpenAsync(AgentHomeRunLogContext context, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(string eventName, string? detail = null, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AppendCommandAsync(AgentHomeCommandLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Commands.Add(record);
        return Task.CompletedTask;
    }

    public Task AppendToolCallAsync(AgentHomeToolCallLogRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
