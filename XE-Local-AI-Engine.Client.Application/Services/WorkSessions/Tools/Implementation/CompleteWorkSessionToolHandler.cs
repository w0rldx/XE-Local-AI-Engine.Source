namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     <c>complete_work_session</c>: the model closing the session, met or not.
/// </summary>
/// <remarks>
///     It does NOT terminalize anything: it appends one event and returns, so the turn finishes cleanly and whatever the model still
///     wants to say is persisted, and the supervisor reads the event back after the terminal and closes the session then. Recording
///     it as an event rather than an in-memory flag is what makes the request survive a crash between the tool call and the step's end.
/// </remarks>
internal sealed class CompleteWorkSessionToolHandler : WorkSessionToolHandler<CompleteWorkSessionRequest>
{
    public CompleteWorkSessionToolHandler(IServiceScopeFactory scopeFactory,
        IOptions<WorkSessionOptions> options,
        INodeRuntimeSettings runtimeSettings,
        IWorkSessionEventPublisher publisher,
        ILogger<CompleteWorkSessionToolHandler> logger) : base(scopeFactory, options, runtimeSettings, publisher, logger)
    {
    }

    public override string ToolName => WorkSessionToolDefinitions.CompleteWorkSession.ToolName;

    public override string Description => WorkSessionToolDefinitions.CompleteWorkSession.Description;

    public override string ParameterSchema => WorkSessionToolDefinitions.CompleteWorkSession.ParameterSchema;

    protected override string ExampleArguments => WorkSessionToolDefinitions.CompleteWorkSession.ExampleArguments;

    protected override string? Validate(CompleteWorkSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Summary))
        {
            return $"{ToolName} needs a non-empty 'summary' describing what the session achieved.";
        }

        if (Exceeds(request.Summary, WorkSessionToolDefinitions.TextMaxLength))
        {
            return Exceeded("summary", WorkSessionToolDefinitions.TextMaxLength);
        }

        // An honest unmet close owes no output: it stands the node run down for a person instead of routing, and demanding
        // one would push the model to invent an answer it just said it does not have.
        return WorkSessionTurnScope.OutputSchema is { } schema && request.ObjectiveMet != false && schema.Violation(request.Output) is { } violation
            ? $"{ToolName} was refused and the session stays open: {violation} Call it again with a valid 'output'."
            : null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(CompleteWorkSessionRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand
            {
                SessionId = session.Id,
                ExpectedVersion = session.Version,
                EventType = WorkSessionEventTypes.CompletionRequested,
                // One completion per step: a model that calls this twice in one turn records it once.
                OperationId = WorkSessionOperationId.For(session.Id, session.StepCount, "completion"),
                Outcome = null,
                DetailJson = JsonSerializer.Serialize(new WorkSessionCompletionDetail(request.Summary!,
                    request.ObjectiveMet,
                    // Kept only under a contract and only when Validate checked it: without one the member is ignored, so a session outside a
                    // workflow records what it always did, and an unmet close skips validation, so what it carries is not routing data.
                    WorkSessionTurnScope.OutputSchema is null || request.ObjectiveMet == false ? null : request.Output))
            },
            cancellationToken);

        // No sequence is published: the session is not finished until the supervisor closes it, and announcing a change
        // now would put the UI ahead of the truth.
        return new WorkSessionToolOutcome
        {
            Message = "The work session will close at the end of this turn. Say anything else you still need to say now."
        };
    }
}
