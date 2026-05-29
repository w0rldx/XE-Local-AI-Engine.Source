namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     Thin adapter between the <c>run_in_agent_home</c> tool handler and <see cref="IAgentHomeService" /> (Marker
///     I-pre, replacing the Marker B pending placeholder). It maps the §7-validated tool request onto the service's
///     prepare/run phases, renders the run result into a compact model-facing string, and maps the two policy
///     rejections raised before any provider call (unknown/invalid selected-folder id, disallowed runtime profile)
///     onto a clear rejection message. Cancellation is allowed to propagate as cancellation.
/// </summary>
internal sealed class AgentHomeToolGateway : IAgentHomeToolGateway
{
    private readonly IAgentHomeService _service;

    public AgentHomeToolGateway(IAgentHomeService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public async Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var prepared = await _service.PrepareAsync(
                new AgentHomePrepareRequest
                {
                    SelectedFolderIds = request.SelectedFolderIds ?? [],
                    RuntimeProfile = request.RuntimeProfile
                },
                cancellationToken).ConfigureAwait(false);

            var run = await _service.RunAsync(
                new AgentHomeRunRequest
                {
                    Prepared = prepared,
                    Goal = request.Goal ?? string.Empty,
                    AllowedActions = request.AllowedActions ?? []
                },
                cancellationToken).ConfigureAwait(false);

            // Report a run-relative output location, never the absolute worker-host path, so the model never sees the
            // worker content-root structure (AgentHome plan §11 spirit; the absolute path stays worker-internal). The
            // workspace summary carries aliases and counts only — never host paths (Marker F).
            return string.Create(
                CultureInfo.InvariantCulture,
                $"AgentHome run {run.RunId} {(run.Completed ? "completed" : "did not complete")} (exit code {run.ExitCode}). Run outputs: runs/{run.RunId}/.{BuildWorkspaceSummary(prepared.FolderSnapshots)}");
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
}
