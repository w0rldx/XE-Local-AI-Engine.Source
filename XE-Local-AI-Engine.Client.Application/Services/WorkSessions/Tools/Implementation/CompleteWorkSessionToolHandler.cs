namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record CompleteWorkSessionRequest(string? Summary, bool? ObjectiveMet = null);

/// <summary>
///     The completion request the supervisor reads back at step end, as it is written to the event log.
///     <para>
///         <see cref="ObjectiveMet" /> is nullable rather than defaulted so that an event recorded before the argument
///         existed reads as <see langword="null" /> — absent, and therefore met — instead of as an unmet objective the
///         model never declared. Only an explicit <see langword="false" /> stands a workflow-owned node run down.
///     </para>
/// </summary>
internal sealed record WorkSessionCompletionDetail(string Summary, bool? ObjectiveMet = null);

/// <summary>
///     <c>complete_work_session</c>: the model closing the session, met or not.
///     <para>
///         It does <b>not</b> terminalize anything — it appends one event and returns, so the turn finishes cleanly and
///         whatever the model still wants to say is persisted. The supervisor reads the event back after the terminal
///         and closes the session then. Recording it as an event rather than an in-memory flag is what makes the
///         request survive a crash between the tool call and the end of the step.
///     </para>
/// </summary>
internal sealed class CompleteWorkSessionToolHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkSessionOptions> options,
    IWorkSessionEventPublisher publisher,
    ILogger<CompleteWorkSessionToolHandler> logger) : WorkSessionToolHandler<CompleteWorkSessionRequest>(scopeFactory, options, publisher, logger)
{
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

        return Exceeds(request.Summary, WorkSessionToolDefinitions.TextMaxLength)
            ? Exceeded("summary", WorkSessionToolDefinitions.TextMaxLength)
            : null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(CompleteWorkSessionRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        _ = await store.AppendEventAsync(new AppendWorkSessionEventCommand(session.Id,
                               session.Version,
                               WorkSessionEventTypes.CompletionRequested,
                               // One completion per step: a model that calls this twice in one turn records it once.
                               WorkSessionOperationId.For(session.Id, session.StepCount, "completion"),
                               Outcome: null,
                               JsonSerializer.Serialize(new WorkSessionCompletionDetail(request.Summary!, request.ObjectiveMet))),
                           cancellationToken)
                       .ConfigureAwait(false);

        // No sequence is published: the session is not finished until the supervisor closes it, and announcing a change
        // now would put the UI ahead of the truth.
        return new WorkSessionToolOutcome("The work session will close at the end of this turn. Say anything else you still need to say now.");
    }
}
