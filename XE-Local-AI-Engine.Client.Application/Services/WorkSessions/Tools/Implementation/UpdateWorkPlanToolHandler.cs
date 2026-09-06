namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Tools.Implementation;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One plan operation as the model wrote it. <c>name</c>, <c>text</c> and <c>summary</c> are aliases for
///     <c>title</c> and exist because a small model reliably reaches for one of them instead: before they were
///     accepted, the unknown key failed the WHOLE batch at deserialization and the retry spent the step's entire
///     provider-call budget guessing.
/// </summary>
internal sealed record WorkPlanOperationRequest(
    string? Op,
    string? TaskId,
    string? Title,
    string? Name,
    string? Text,
    string? Summary,
    string? Detail,
    string? Status,
    string? BlockedReason,
    string? ParentTaskId)
{
    /// <summary>The title under whichever key it arrived, trimmed; null when none of them carried anything.</summary>
    public string? EffectiveTitle => Trimmed(Title) ?? Trimmed(Name) ?? Trimmed(Text) ?? Trimmed(Summary);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record UpdateWorkPlanRequest(IReadOnlyList<WorkPlanOperationRequest>? Operations);

/// <summary>
///     <c>update_work_plan</c>: the model's only way to change the session's task list. The whole batch commits in one
///     store transaction, so a partially-applied plan is not a state the session can be observed in.
/// </summary>
internal sealed class UpdateWorkPlanToolHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkSessionOptions> options,
    IWorkSessionEventPublisher publisher,
    ILogger<UpdateWorkPlanToolHandler> logger) : WorkSessionToolHandler<UpdateWorkPlanRequest>(scopeFactory, options, publisher, logger)
{
    /// <summary>
    ///     How many added-task ids one result names. Deliberately under
    ///     <see cref="WorkSessionToolDefinitions.MaxPlanOperations" /> so a maximal batch of adds cannot spend the
    ///     step's context echoing itself; anything past it reads back off the next state block.
    /// </summary>
    private const int MaxListedAddedTasks = 10;

    public override string ToolName => WorkSessionToolDefinitions.UpdateWorkPlan.ToolName;

    public override string Description => WorkSessionToolDefinitions.UpdateWorkPlan.Description;

    public override string ParameterSchema => WorkSessionToolDefinitions.UpdateWorkPlan.ParameterSchema;

    protected override string ExampleArguments => WorkSessionToolDefinitions.UpdateWorkPlan.ExampleArguments;

    protected override string? Validate(UpdateWorkPlanRequest request)
    {
        if (request.Operations is not { Count: > 0 })
        {
            return $"{ToolName} needs at least one operation.";
        }

        if (request.Operations.Count > WorkSessionToolDefinitions.MaxPlanOperations)
        {
            return $"{ToolName} accepts at most {WorkSessionToolDefinitions.MaxPlanOperations} operations in one call.";
        }

        foreach (var operation in request.Operations)
        {
            if (ValidateOperation(operation) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    protected override async Task<WorkSessionToolOutcome> ExecuteCoreAsync(UpdateWorkPlanRequest request,
        AgentWorkSessionSnapshot session,
        IAgentWorkSessionStore store,
        CancellationToken cancellationToken)
    {
        var operations = request.Operations!;
        var batchDigest = BatchDigest(operations);
        var changes = new List<WorkPlanTaskChange>(operations.Count);
        // Indexed, not foreach: an add's id is derived from its position in the batch, so two identical adds in one
        // call stay two tasks.
        for (var index = 0; index < operations.Count; index++)
        {
            if (TryMap(operations[index], index, batchDigest, session, out var change, out var error))
            {
                changes.Add(change);
                continue;
            }

            return new WorkSessionToolOutcome(error);
        }

        var result = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(session.Id,
                                        session.Version,
                                        // One operation id per batch content, so the same batch replayed after a lost response commits once.
                                        WorkSessionOperationId.For(session.Id, session.StepCount, DescribeBatch(changes)),
                                        AgentWorkSessionTaskOrigin.Agent,
                                        changes),
                                    cancellationToken)
                                .ConfigureAwait(false);

        return new WorkSessionToolOutcome(Describe(changes), result.Sequence, WorkSessionChangeKind.Task);
    }

    /// <summary>
    ///     The sentence handed back to the model. An 'add' mints its id here, so naming those ids is the only way a
    ///     one-step session can move a task it just added: the state block for the step was composed before the task
    ///     existed, and a model with no id for 'update' cannot mark that task Blocked — the signal a workflow node
    ///     reads to stand the step down for a human never fires (live finding P3).
    /// </summary>
    private static string Describe(IReadOnlyList<WorkPlanTaskChange> changes)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Recorded {changes.Count} work-plan change(s)."));
        var added = changes.Where(static change => change.Operation == WorkPlanTaskOperation.Add).ToList();
        if (added.Count == 0)
        {
            return text.ToString();
        }

        _ = text.Append(CultureInfo.InvariantCulture, $" Added {added.Count} task(s): ");
        for (var index = 0; index < added.Count && index < MaxListedAddedTasks; index++)
        {
            if (index > 0)
            {
                _ = text.Append(", ");
            }

            // The id in the exact spelling the state block prints and TryParseId reads back.
            _ = text.Append(CultureInfo.InvariantCulture, $"\"{added[index].Title}\" = {added[index].TaskId}");
        }

        if (added.Count > MaxListedAddedTasks)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $", +{added.Count - MaxListedAddedTasks} more");
        }

        return text.Append(". Use these ids for update, complete or drop in this step.").ToString();
    }

    private string? ValidateOperation(WorkPlanOperationRequest operation)
    {
        if (!TryParseOperation(operation.Op, out var parsed))
        {
            return $"{ToolName} operation 'op' must be one of add, update, complete or drop.";
        }

        if (Exceeds(operation.EffectiveTitle, WorkSessionToolDefinitions.TitleMaxLength))
        {
            return Exceeded("title", WorkSessionToolDefinitions.TitleMaxLength);
        }

        if (Exceeds(operation.Detail, WorkSessionToolDefinitions.TextMaxLength))
        {
            return Exceeded("detail", WorkSessionToolDefinitions.TextMaxLength);
        }

        if (Exceeds(operation.BlockedReason, WorkSessionToolDefinitions.ReferenceMaxLength))
        {
            return Exceeded("blockedReason", WorkSessionToolDefinitions.ReferenceMaxLength);
        }

        if (operation.Status is not null && !Enum.TryParse<AgentWorkSessionTaskStatus>(operation.Status, out _))
        {
            return $"{ToolName} operation 'status' must be one of Planned, Active, Blocked, Done or Dropped.";
        }

        if (parsed == WorkPlanTaskOperation.Add)
        {
            return operation.EffectiveTitle is null
                ? $"{ToolName} operation 'add' needs a title. Example: {ExampleArguments}"
                : null;
        }

        return string.IsNullOrWhiteSpace(operation.TaskId)
            ? $"{ToolName} operation '{operation.Op}' needs the taskId of an existing task."
            : null;
    }

    private bool TryMap(WorkPlanOperationRequest operation,
        int index,
        string batchDigest,
        AgentWorkSessionSnapshot session,
        out WorkPlanTaskChange change,
        out string error)
    {
        change = null!;
        _ = TryParseOperation(operation.Op, out var parsed);

        Guid taskId;
        if (parsed == WorkPlanTaskOperation.Add)
        {
            // The node mints the id: a model-supplied one is either a collision or a forgery attempt, and the state
            // block hands the real one back on the very next step anyway.
            //
            // Derived rather than random, because the id lands inside DescribeBatch's idempotency key: a Guid.NewGuid()
            // made the key of a retried batch differ from the original's, the store's query-first dedupe saw a new
            // operation, and the retry added the task a SECOND time.
            //
            // The WHOLE batch's digest, not this operation's own content: a second call in the same step that repeats
            // an earlier add — an ordinary small-model habit — would otherwise re-mint that add's id, and the store
            // refuses an id it already holds, rolling back the genuinely new operations beside it. Position separates
            // two identical adds inside one batch; the digest separates one batch from every other batch in the step.
            taskId = WorkSessionOperationId.For(session.Id, session.StepCount, string.Create(CultureInfo.InvariantCulture, $"add:{index}:{batchDigest}"));
        }
        else if (!TryParseId(operation.TaskId, out taskId))
        {
            error = $"{ToolName} could not read '{operation.TaskId}' as a task id. Use the ids from the work session state block.";
            return false;
        }

        Guid? parentTaskId = null;
        if (!string.IsNullOrWhiteSpace(operation.ParentTaskId))
        {
            if (!TryParseId(operation.ParentTaskId, out var parent))
            {
                error = $"{ToolName} could not read '{operation.ParentTaskId}' as a parent task id.";
                return false;
            }

            parentTaskId = parent;
        }

        AgentWorkSessionTaskStatus? status = operation.Status is null
            ? null
            : Enum.Parse<AgentWorkSessionTaskStatus>(operation.Status);

        change = new WorkPlanTaskChange(taskId,
            parsed,
            parentTaskId,
            operation.EffectiveTitle,
            string.IsNullOrWhiteSpace(operation.Detail) ? null : operation.Detail,
            status,
            string.IsNullOrWhiteSpace(operation.BlockedReason) ? null : operation.BlockedReason);
        error = string.Empty;
        return true;
    }

    private static bool TryParseOperation(string? op, out WorkPlanTaskOperation parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(op) && Enum.TryParse(op, ignoreCase: true, out parsed);
    }

    // What the batch asked for, in order, as the material every add's id is derived from. JSON rather than a
    // colon-joined string because both the title and the detail are free model text: separated by a bare colon,
    // "Step 2" + "verify the pin" and "Step 2:verify the pin" render alike, and two genuinely different adds would
    // mint one id. Fields are normalized the way TryMap maps them — the title alias resolved, blank read as absent —
    // so a retry that differs only in how it spelt an empty field still lands on the same ids.
    private static string BatchDigest(IReadOnlyList<WorkPlanOperationRequest> operations) =>
        JsonSerializer.Serialize(operations.Select(static operation => new[]
        {
            operation.Op,
            Normalized(operation.TaskId),
            operation.EffectiveTitle,
            Normalized(operation.Detail),
            operation.Status,
            Normalized(operation.BlockedReason),
            Normalized(operation.ParentTaskId)
        }));

    private static string? Normalized(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // The batch's identity for idempotency: the operations it carries, in order. Two genuinely different batches in one
    // step therefore get different ids, while a replay of the same one collapses.
    private static string DescribeBatch(IReadOnlyList<WorkPlanTaskChange> changes) =>
        "plan:" + string.Join('|', changes.Select(static change => string.Create(CultureInfo.InvariantCulture, $"{change.Operation}:{change.TaskId:N}")));
}
