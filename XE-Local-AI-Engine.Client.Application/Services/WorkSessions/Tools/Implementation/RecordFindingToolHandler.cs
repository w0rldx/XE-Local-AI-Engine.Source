namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record RecordFindingRequest(string? Kind, string? Text, string? SourceRef, string? TaskId, string? SupersedesId);

/// <summary>
///     <c>record_finding</c>: the session's durable memory of what it learned. Everything written here is re-injected
///     into a later step's state block, which is why the composer fences it as untrusted data — <c>sourceRef</c> in
///     particular invites pasting verbatim tool output.
/// </summary>
internal sealed class RecordFindingToolHandler(IServiceScopeFactory scopeFactory,
    IOptions<WorkSessionOptions> options,
    IWorkSessionEventPublisher publisher,
    ILogger<RecordFindingToolHandler> logger) : WorkSessionToolHandler<RecordFindingRequest>(scopeFactory, options, publisher, logger)
{
    public override string ToolName => WorkSessionToolDefinitions.RecordFinding.ToolName;

    public override string Description => WorkSessionToolDefinitions.RecordFinding.Description;

    public override string ParameterSchema => WorkSessionToolDefinitions.RecordFinding.ParameterSchema;

    protected override string ExampleArguments => WorkSessionToolDefinitions.RecordFinding.ExampleArguments;

    protected override string? Validate(RecordFindingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Kind) || !Enum.TryParse<AgentWorkSessionFindingKind>(request.Kind, out _))
        {
            return $"{ToolName} argument 'kind' must be one of Finding, Evidence, Decision or OpenQuestion.";
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return $"{ToolName} needs a non-empty 'text'.";
        }

        if (Exceeds(request.Text, WorkSessionToolDefinitions.TextMaxLength))
        {
            return Exceeded("text", WorkSessionToolDefinitions.TextMaxLength);
        }

        return Exceeds(request.SourceRef, WorkSessionToolDefinitions.ReferenceMaxLength)
            ? Exceeded("sourceRef", WorkSessionToolDefinitions.ReferenceMaxLength)
            : null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(RecordFindingRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        Guid? taskId = null;
        if (!string.IsNullOrWhiteSpace(request.TaskId))
        {
            if (!TryParseId(request.TaskId, out var parsedTask))
            {
                return new WorkSessionToolOutcome($"{ToolName} could not read '{request.TaskId}' as a task id. Use the ids from the work session state block.");
            }

            taskId = parsedTask;
        }

        Guid? supersedesId = null;
        if (!string.IsNullOrWhiteSpace(request.SupersedesId))
        {
            if (!TryParseId(request.SupersedesId, out var parsedSupersedes))
            {
                return new WorkSessionToolOutcome($"{ToolName} could not read '{request.SupersedesId}' as a finding id.");
            }

            supersedesId = parsedSupersedes;
        }

        var findingId = Guid.NewGuid();
        var result = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(session.Id,
                    findingId,
                    session.Version,
                    WorkSessionOperationId.For(session.Id, session.StepCount, $"finding:{findingId:N}"),
                    Enum.Parse<AgentWorkSessionFindingKind>(request.Kind!),
                    request.Text!,
                    taskId,
                    string.IsNullOrWhiteSpace(request.SourceRef) ? null : request.SourceRef,
                    supersedesId),
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkSessionToolOutcome($"Recorded a {request.Kind} on this work session.", result.Sequence, WorkSessionChangeKind.Finding);
    }
}
