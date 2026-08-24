namespace XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Everything one step needs to know about its session, loaded once.
/// </summary>
internal sealed record WorkSessionState(AgentWorkSessionSnapshot Session,
    IReadOnlyList<WorkSessionTaskSnapshot> Tasks,
    IReadOnlyList<WorkSessionFindingSnapshot> Findings,
    IReadOnlyList<WorkSessionArtifactSnapshot> Artifacts,
    WorkSessionCheckpointSnapshot? LastCheckpoint);

/// <summary>
///     Builds the one message each step sends: the session's state, rebuilt from the database every time.
///     <para>
///         Rebuilding beats relying on the transcript for two reasons. A tool-only assistant turn is dropped from later
///         context entirely (the send path keeps only completed, non-empty messages), so a step that did nothing but
///         call tools would otherwise vanish. And the raw history is bounded by compaction, so anything older than the
///         synopsis is gone by construction. What the model sees is therefore current and bounded, independently of
///         what survived.
///     </para>
///     <para>
///         Every agent-authored string in the block — task titles and details, finding text and source references,
///         artifact names, the checkpoint synopsis — sits inside ONE untrusted-content fence. All of it has derived
///         provenance and may be verbatim knowledge-base or MCP output; <c>sourceRef</c> in particular invites pasting
///         tool results. It is data to reason over, not instructions to follow. The objective stays outside the fence:
///         it is the operator's own text and the one instruction in the block that IS meant to be followed.
///     </para>
/// </summary>
internal static class WorkSessionStateBlockComposer
{
    /// <summary>The prefix the frontend collapses these synthetic user turns by. Do not change it without changing that.</summary>
    public const string BlockPrefix = "[work session state";

    private const int MaxOpenTasks = 20;
    private const int MaxFindings = 15;
    private const int MaxFindingCharacters = 400;
    private const int MaxArtifacts = 10;

    private static readonly AgentWorkSessionTaskStatus[] OpenTaskStatuses =
    [
        AgentWorkSessionTaskStatus.Planned,
        AgentWorkSessionTaskStatus.Active,
        AgentWorkSessionTaskStatus.Blocked
    ];

    public static string Compose(WorkSessionState state, int step, int maxStepsPerRun)
    {
        ArgumentNullException.ThrowIfNull(state);

        var header = new StringBuilder();
        _ = header.Append(CultureInfo.InvariantCulture, $"{BlockPrefix} — step {step} of at most {maxStepsPerRun}]\n");
        _ = header.Append(CultureInfo.InvariantCulture, $"Objective: {state.Session.Objective}\n");

        var body = new StringBuilder();
        var openTasks = OpenTasks(state.Tasks);
        var currentTask = ResolveCurrentTask(state);
        _ = body.Append("Current task: ")
                .Append(currentTask is null
                    ? "(none — pick one from the open tasks, or add one with update_work_plan)"
                    : string.Create(CultureInfo.InvariantCulture, $"{currentTask.Title} [{currentTask.Status}] · id {currentTask.Id}"))
                .Append('\n');

        _ = body.Append("Open tasks:\n");
        if (openTasks.Count == 0)
        {
            _ = body.Append("  (none)\n");
        }

        foreach (var task in openTasks)
        {
            _ = body.Append(CultureInfo.InvariantCulture, $"  - {task.Title} [{task.Status}] · id {task.Id}");
            if (task.Status == AgentWorkSessionTaskStatus.Blocked && !string.IsNullOrWhiteSpace(task.BlockedReason))
            {
                _ = body.Append(CultureInfo.InvariantCulture, $" · blocked: {task.BlockedReason}");
            }

            if (!string.IsNullOrWhiteSpace(task.Detail))
            {
                _ = body.Append(CultureInfo.InvariantCulture, $"\n      {Truncate(task.Detail, MaxFindingCharacters)}");
            }

            _ = body.Append('\n');
        }

        var findings = RecentFindings(state.Findings);
        _ = body.Append("Recent findings:\n");
        if (findings.Count == 0)
        {
            _ = body.Append("  (none yet)\n");
        }

        foreach (var finding in findings)
        {
            _ = body.Append(CultureInfo.InvariantCulture, $"  - [{finding.Kind}] {Truncate(finding.Text, MaxFindingCharacters)}");
            if (!string.IsNullOrWhiteSpace(finding.SourceRef))
            {
                _ = body.Append(CultureInfo.InvariantCulture, $" (source: {Truncate(finding.SourceRef, MaxFindingCharacters)})");
            }

            _ = body.Append('\n');
        }

        var artifacts = state.Artifacts.OrderByDescending(static artifact => artifact.Sequence).Take(MaxArtifacts).ToList();
        if (artifacts.Count > 0)
        {
            _ = body.Append("Artifacts:\n");
            foreach (var artifact in artifacts)
            {
                _ = body.Append(CultureInfo.InvariantCulture, $"  - {artifact.Name} ({artifact.MediaType}, {artifact.SizeBytes} B)\n");
            }
        }

        if (state.LastCheckpoint is { Summary: { Length: > 0 } summary } checkpoint)
        {
            _ = body.Append(CultureInfo.InvariantCulture, $"Last checkpoint (step {checkpoint.Step}): {summary}\n");
        }

        var footer = "\nContinue the objective. Record what you learn with record_finding, keep the plan current with "
                     + "update_work_plan, and call complete_work_session when the objective is met.";

        return header + UntrustedContentFraming.WrapDocument(body.ToString(), []) + footer;
    }

    /// <summary>
    ///     The task the session is on: the stored pointer when it still resolves to an open task, otherwise the single
    ///     <c>Active</c> one. The fallback matters because the tool handlers move a task to <c>Active</c> without
    ///     touching the session row, which only a status transition may write.
    /// </summary>
    public static WorkSessionTaskSnapshot? ResolveCurrentTask(WorkSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Session.CurrentTaskId is { } currentTaskId
            && state.Tasks.FirstOrDefault(task => task.Id == currentTaskId) is { } pointed
            && OpenTaskStatuses.Contains(pointed.Status))
        {
            return pointed;
        }

        return state.Tasks.Where(static task => task.Status == AgentWorkSessionTaskStatus.Active)
                    .OrderBy(static task => task.Sequence)
                    .FirstOrDefault();
    }

    public static IReadOnlyList<WorkSessionTaskSnapshot> OpenTasks(IReadOnlyList<WorkSessionTaskSnapshot> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        return
        [
            .. tasks.Where(static task => OpenTaskStatuses.Contains(task.Status))
                    .OrderBy(static task => task.Sequence)
                    .Take(MaxOpenTasks)
        ];
    }

    public static IReadOnlyList<WorkSessionFindingSnapshot> RecentFindings(IReadOnlyList<WorkSessionFindingSnapshot> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return
        [
            .. findings.Where(static finding => !finding.Superseded)
                       .OrderByDescending(static finding => finding.Sequence)
                       .Take(MaxFindings)
                       .Reverse()
        ];
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : string.Concat(value.AsSpan(start: 0, maximumLength), "…");
}
