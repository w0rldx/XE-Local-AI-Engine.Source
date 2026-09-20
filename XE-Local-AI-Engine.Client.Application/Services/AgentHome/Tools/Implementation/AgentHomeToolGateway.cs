namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thin adapter between the <c>run_in_agent_home</c> tool handler and <see cref="IAgentHomeService" />.
/// </summary>
/// <remarks>
///     It maps the validated tool request onto the service's prepare/run phases, renders the run result into a
///     compact model-facing string, and turns the two policy rejections raised before any provider call — unknown or
///     invalid selected-folder id, disallowed runtime profile — into a clear rejection message. Cancellation is
///     allowed to propagate as cancellation.
/// </remarks>
internal sealed class AgentHomeToolGateway : IAgentHomeToolGateway
{
    /// <summary>
    ///     Fixed text, never composed from what the run produced: the outer model has the node's other tools, so
    ///     echoed workspace bytes would arrive as steering text. It says instead that re-invoking the tool reveals
    ///     nothing more.
    /// </summary>
    private const string RunIsFinalNotice =
        " This run has ended and the summary above is the whole of what the node recorded; calling run_in_agent_home"
        + " again repeats the work rather than revealing more. Read runs/<run-id>/logs/ and the patch for the detail.";

    private readonly IAgentHomeService _service;
    private readonly INodeRuntimeSettings _runtimeSettings;

    public AgentHomeToolGateway(IAgentHomeService service, INodeRuntimeSettings runtimeSettings)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
    }

    public async Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // One lifecycle entry: the service resolves identity once, takes the run-level single-flight guard, then runs
            // Prepare + Run under it. ConversationId comes from the ambient run context, never the model args, so it cannot be forged.
            var run = await _service.RunLifecycleAsync(new AgentHomeRunLifecycleRequest
                {
                    SelectedFolderIds = request.SelectedFolderIds ?? [],
                    RuntimeProfile = request.RuntimeProfile,
                    ConversationId = AgentRunConversationContext.Current,
                    Goal = request.Goal ?? string.Empty,
                    AllowedActions = request.AllowedActions ?? []
                },
                cancellationToken);

            // Report a run-relative output location, never the absolute worker-host path, so the model never sees the
            // worker content-root structure. The workspace summary carries aliases and counts only, never host paths.
            var commandTimeoutSeconds = await _runtimeSettings.GetAgentHomeCommandTimeoutSecondsAsync(cancellationToken);

            // The header goes FIRST and is built from node data only — see BuildHeader for why that ordering is the
            // control rather than a convenience.
            return string.Create(CultureInfo.InvariantCulture,
                $"{BuildHeader(run)}\nAgentHome run {run.RunId} {DescribeOutcome(run, commandTimeoutSeconds)}. Run outputs: runs/{run.RunId}/.{BuildWorkspaceSummary(run.FolderSnapshots)}{BuildGoalSummary(run.GoalOutcome)}{BuildPatchSummary(run.Patch)}{BuildSandboxNotice(run.SandboxProviderName, run.GoalOutcome)}{RunIsFinalNotice}");
        }
        catch (AgentHomeBusyException)
        {
            return "run_in_agent_home rejected: an AgentHome run is already in progress for this node.";
        }
        catch (SelectedFolderValidationException exception)
        {
            return $"run_in_agent_home rejected: {exception.Message}";
        }
        catch (AgentHomeRequestRejectedException exception)
        {
            return $"run_in_agent_home rejected: {exception.Message}";
        }
    }

    private static string DescribeOutcome(AgentHomeRunResult run, int commandTimeoutSeconds)
    {
        if (run.GoalOutcome is { } goal)
        {
            return goal.Status switch
            {
                AgentHomeGoalStatus.NotRun => "did not execute the goal",
                AgentHomeGoalStatus.TimeBudgetExceeded => "was cut off by the whole-run time budget",
                AgentHomeGoalStatus.ToolCallBudgetExceeded => "was cut off by the tool-call budget",
                AgentHomeGoalStatus.Failed => "did not complete (the run failed part-way)",
                _ => "completed"
            };
        }

        // No goal outcome at all: the run never reached the executor. Fall back to the lifecycle's own flags.
        if (run.TimedOut)
        {
            return string.Create(CultureInfo.InvariantCulture, $"did not complete (timed out after {commandTimeoutSeconds}s)");
        }

        return run.Completed ? "completed" : "did not complete";
    }

    /// <summary>
    ///     The machine-readable first line of the tool result, and a CONTRACT: read start-anchored as the whole of
    ///     line one, in this shape and field order —
    ///     <c>[agent-home run=&lt;runId&gt; outcome=&lt;Token&gt; patch=&lt;exported|none&gt;]</c> then <c>\n</c>.
    /// </summary>
    /// <remarks>
    ///     Position is the defence: the prose after it embeds model-authored text before the run's genuine patch
    ///     path, so a reader recovering the run id by scanning could be handed another run's patch. Every field
    ///     here is node-derived and precedes any model-authored byte. <c>patch=exported</c> means the patch was
    ///     written; one over <see cref="AgentHomeOptions.MaxPatchBytes" /> reports <c>none</c>. A rejection creates
    ///     no run, so it carries no header, and a missing header means "no run".
    /// </remarks>
    private static string BuildHeader(AgentHomeRunResult run)
    {
        var patch = run.Patch.PatchRelativePath is { Length: > 0 } ? "exported" : "none";
        return string.Create(CultureInfo.InvariantCulture, $"[agent-home run={run.RunId} outcome={OutcomeToken(run)} patch={patch}]");
    }

    /// <summary>
    ///     The stop reason of <see cref="DescribeOutcome" /> as ONE token from a closed set, and the <c>outcome</c>
    ///     field of <see cref="BuildHeader" />: <see cref="AgentHomeGoalStatus" /> plus <c>TimedOut</c> for a run
    ///     that never reached the executor. Nothing here is derived from what the run produced, so the set cannot
    ///     grow behind the node's back.
    /// </summary>
    private static string OutcomeToken(AgentHomeRunResult run)
    {
        if (run.GoalOutcome is { } goal)
        {
            // Spelled out rather than ToString()'d: the token is a CONTRACT with whoever reads this result, so a
            // rename breaks the build and a NEW status throws rather than reporting itself as a completed run.
            return goal.Status switch
            {
                AgentHomeGoalStatus.NotRun => nameof(AgentHomeGoalStatus.NotRun),
                AgentHomeGoalStatus.Completed => nameof(AgentHomeGoalStatus.Completed),
                AgentHomeGoalStatus.ToolCallBudgetExceeded => nameof(AgentHomeGoalStatus.ToolCallBudgetExceeded),
                AgentHomeGoalStatus.TimeBudgetExceeded => nameof(AgentHomeGoalStatus.TimeBudgetExceeded),
                AgentHomeGoalStatus.Failed => nameof(AgentHomeGoalStatus.Failed),
                _ => throw new UnreachableException($"Unknown AgentHome goal status '{goal.Status}'.")
            };
        }

        if (run.TimedOut)
        {
            return "TimedOut";
        }

        return run.Completed ? nameof(AgentHomeGoalStatus.Completed) : nameof(AgentHomeGoalStatus.Failed);
    }

    /// <summary>
    ///     What the run actually did, in the model's own result.
    /// </summary>
    /// <remarks>
    ///     The honesty clause runs in BOTH directions: a run whose goal never executed must SAY so, and a run that
    ///     did execute must not be described as if it had not. Without it a fixed liveness probe renders as a bare
    ///     "completed (exit code 0)" and the model is left to reason its way to the truth unaided.
    /// </remarks>
    private static string BuildGoalSummary(AgentHomeGoalOutcome? goal)
    {
        if (goal is null)
        {
            return string.Empty;
        }

        if (!goal.Executed)
        {
            return $" NOTE: the goal was NOT executed — {goal.NotRunReason}";
        }

        // A granted action that produced no tool is reported BEFORE the work summary: a model that asked for
        // run_commands and reads "0 commands" would otherwise conclude it chose not to run any.
        var withheld = goal.CommandsUnavailableReason is { Length: > 0 } reason
            ? $" NOTE: {reason}"
            : string.Empty;

        var summary = new StringBuilder();
        _ = summary.Append(string.Create(CultureInfo.InvariantCulture, $" Work: {goal.ToolCallCount} tool call(s)"));

        if (goal.RefusedCallCount > 0)
        {
            _ = summary.Append(string.Create(CultureInfo.InvariantCulture, $" ({goal.RefusedCallCount} refused)"));
        }

        // A COUNT, never the names: WrittenFiles holds paths the MODEL chose, and the outer model has the node's
        // other tools, so a path here would be steering text. changed-files.json on disk keeps the names.
        _ = summary.Append(string.Create(CultureInfo.InvariantCulture, $", {goal.WrittenFiles.Count} file(s) written"));

        // The loop's own wall clock, off the same TimeProvider the MaxRunSeconds budget is kept on.
        _ = summary.Append(string.Create(CultureInfo.InvariantCulture, $", {goal.Elapsed.TotalSeconds:F1}s elapsed"));

        if (goal.Commands.Count > 0)
        {
            // The aggregate before the list: a model scanning for "did any of this fail?" gets the answer without
            // having to total the per-command exits itself.
            var failed = goal.Commands.Count(static command => command.Completed && command.ExitCode != 0);
            var incomplete = goal.Commands.Count(static command => !command.Completed);
            // The `commands:` label itself is kept verbatim — a reader parsing this result defensively keys on it.
            _ = summary.Append(string.Create(CultureInfo.InvariantCulture,
                           $", commands: {goal.Commands.Count} run ({failed} non-zero exit, {incomplete} did not complete): "))
                       .Append(string.Join("; ",
                           goal.Commands.Select(static command => command.Completed
                               ? string.Create(CultureInfo.InvariantCulture, $"{SingleLine(command.Executable)} exit {command.ExitCode}")
                               : string.Create(CultureInfo.InvariantCulture, $"{SingleLine(command.Executable)} did not complete"))));
        }

        _ = summary.Append('.').Append(withheld);

        if (goal.Status == AgentHomeGoalStatus.TimeBudgetExceeded || goal.Status == AgentHomeGoalStatus.ToolCallBudgetExceeded)
        {
            _ = summary.Append(" NOTE: a budget cut this run off, so the work below may be incomplete.");
        }
        else if (goal.Status == AgentHomeGoalStatus.Failed)
        {
            _ = summary.Append(" NOTE: the run failed part-way, so the work below may be incomplete.");
        }

        return summary.ToString();
    }

    /// <summary>
    ///     Flattens the model-authored <c>run_command</c> executable onto one line. <see cref="BuildHeader" />'s
    ///     contract is start-anchored, so a newline cannot forge the header, but it could produce a line that merely
    ///     LOOKS like one to a reader that scans lines.
    /// </summary>
    private static string SingleLine(string value)
    {
        return value.ReplaceLineEndings(" ");
    }

    /// <summary>
    ///     The honesty clause for the no-op sandbox backend.
    /// </summary>
    /// <remarks>
    ///     <c>fake</c> answers every command it was not scripted for with exit 0 and empty output, so a run it served renders as
    ///     "completed" with no file changes, indistinguishable from a real run whose goal produced nothing. It is also what a
    ///     Development node resolves when <c>AgentHome:Sandbox:Provider</c> is unset, the default a first live round hits. Saying so
    ///     in the model-facing result stops the model reporting work it never did, and it must be said whether or not the goal loop
    ///     ran: on this backend a loop that "ran" still executed nothing.
    /// </remarks>
    private static string BuildSandboxNotice(string sandboxProviderName, AgentHomeGoalOutcome? goal)
    {
        if (!string.Equals(sandboxProviderName, FakeSandboxRuntimeProvider.Name, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var executed = goal?.Executed == true;
        return (executed
                   ? " NOTE: nothing was really executed — this node's AgentHome sandbox backend is 'fake', which runs nothing."
                     + " Any command the run reports produced no real work and any file it reports writing went nowhere."
                   : " NOTE: nothing was executed — this node's AgentHome sandbox backend is 'fake', which runs nothing.")
               + " Set AgentHome:Sandbox:Provider=process and restart the node to execute for real.";
    }

    private static string BuildWorkspaceSummary(IReadOnlyList<SelectedFolderSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return string.Empty;
        }

        return " Workspace: " + string.Join("; ", snapshots.Select(DescribeFolder)) + ".";
    }

    private static string DescribeFolder(SelectedFolderSnapshot snapshot)
    {
        return snapshot.Status == SelectedFolderCopyStatus.BlockedQuota
            ? string.Create(CultureInfo.InvariantCulture, $"{snapshot.Alias} blocked (over size budget)")
            : string.Create(CultureInfo.InvariantCulture, $"{snapshot.Alias} copied {snapshot.CopiedFileCount} file(s), excluded {snapshot.ExcludedFileCount}");
    }

    private static string BuildPatchSummary(AgentHomePatchExport patch)
    {
        if (patch.Failed)
        {
            return " Patch: export failed.";
        }

        // A SIZE, never the content: the node's own byte count says whether the run made a one-line edit or rewrote
        // a tree, which is what the model re-invokes the tool to find out. No patch text crosses into this result.
        if (patch.Blocked)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $" Patch: {patch.ChangedFileCount} file(s) changed, {patch.PatchBytes} byte(s); patch over size budget (not written), see {patch.ChangedFilesRelativePath}.");
        }

        if (patch.ChangedFileCount == 0)
        {
            return " Patch: no file changes.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $" Patch: {patch.ChangedFileCount} file(s) changed, {patch.PatchBytes} byte(s) exported -> {patch.PatchRelativePath}.");
    }
}
