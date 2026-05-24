namespace XE_Local_AI_Engine.Client.Hubs;

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Manager;

[Authorize(AuthenticationSchemes = LocalOperatorAuthorization.AuthenticationType, Policy = LocalOperatorAuthorization.OperatorPolicy)]
public sealed class RuntimeManagerHub(IHostAgentManagerService managerService) : Hub
{
    private const int DefaultTailLines = 200;
    private const int MaximumTailLines = 2_000;

    public IAsyncEnumerable<RuntimeLogLineResponse> StreamLogs(
        RuntimeLogsRequest request,
        CancellationToken cancellationToken)
    {
        var (containerName, tailLines, follow) = NormalizeRequest(request);
        return StreamLogsCore(containerName, tailLines, follow, cancellationToken);
    }

    private static (string ContainerName, int TailLines, bool Follow) NormalizeRequest(RuntimeLogsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var containerName = request.ContainerName?.Trim();
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new HubException("Container name is required.");
        }

        var tailLines = request.TailLines ?? DefaultTailLines;
        if (tailLines is < 0 or > MaximumTailLines)
        {
            throw new HubException($"Tail lines must be between 0 and {MaximumTailLines}.");
        }

        return (containerName, tailLines, request.Follow);
    }

    private async IAsyncEnumerable<RuntimeLogLineResponse> StreamLogsCore(
        string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in managerService.StreamLogsAsync(containerName,
                           tailLines,
                           follow,
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return line.ToResponse();
        }
    }
}
