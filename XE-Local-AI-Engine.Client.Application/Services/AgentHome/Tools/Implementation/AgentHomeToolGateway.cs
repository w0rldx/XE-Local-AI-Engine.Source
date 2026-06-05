namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thin adapter between the <c>run_in_agent_home</c> tool handler and <see cref="IAgentHomeService" />. It maps
///     the validated tool request onto the service's
///     prepare/run phases, renders the run result into a compact model-facing string, and maps the two policy
///     rejections raised before any provider call (unknown/invalid selected-folder id, disallowed runtime profile)
///     onto a clear rejection message. Cancellation is allowed to propagate as cancellation.
/// </summary>
internal sealed class AgentHomeToolGateway : IAgentHomeToolGateway
{
    private readonly int _commandTimeoutSeconds;
    private readonly IAgentHomeService _service;

    public AgentHomeToolGateway(IAgentHomeService service, IOptions<AgentHomeOptions> options)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        ArgumentNullException.ThrowIfNull(options);
        _commandTimeoutSeconds = options.Value.CommandTimeoutSeconds;
    }

    public async Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Single lifecycle entry (AgentHome gateway): the service resolves identity once, acquires the run-level single-flight
            // guard, then runs Prepare + Run under it. The gateway no longer calls Prepare and Run separately.
            var run = await _service.RunLifecycleAsync(new AgentHomeRunLifecycleRequest
                {
                    SelectedFolderIds = request.SelectedFolderIds ?? [],
                    RuntimeProfile = request.RuntimeProfile,
                    Goal = request.Goal ?? string.Empty,
                    AllowedActions = request.AllowedActions ?? []
                },
                cancellationToken).ConfigureAwait(false);

            // Report a run-relative output location, never the absolute worker-host path, so the model never sees the
            // worker content-root structure. The
            // workspace summary carries aliases and counts only — never host paths (workspace copy).
            return string.Create(CultureInfo.InvariantCulture,
                $"AgentHome run {run.RunId} {DescribeOutcome(run)} (exit code {run.ExitCode}). Run outputs: runs/{run.RunId}/.{BuildWorkspaceSummary(run.FolderSnapshots)}{BuildPatchSummary(run.Patch)}");
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

    private string DescribeOutcome(AgentHomeRunResult run)
    {
        if (run.TimedOut)
        {
            return string.Create(CultureInfo.InvariantCulture, $"did not complete (timed out after {_commandTimeoutSeconds}s)");
        }

        return run.Completed ? "completed" : "did not complete";
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

        if (patch.Blocked)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $" Patch: {patch.ChangedFileCount} file(s) changed; patch over size budget (not written), see {patch.ChangedFilesRelativePath}.");
        }

        if (patch.ChangedFileCount == 0)
        {
            return " Patch: no file changes.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $" Patch: {patch.ChangedFileCount} file(s) changed -> {patch.PatchRelativePath}.");
    }
}
