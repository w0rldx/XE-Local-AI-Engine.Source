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
            // worker content-root structure (AgentHome plan §11 spirit; the absolute path stays worker-internal).
            return string.Create(
                CultureInfo.InvariantCulture,
                $"AgentHome run {run.RunId} {(run.Completed ? "completed" : "did not complete")} (exit code {run.ExitCode}). Run outputs: runs/{run.RunId}/.");
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
}
