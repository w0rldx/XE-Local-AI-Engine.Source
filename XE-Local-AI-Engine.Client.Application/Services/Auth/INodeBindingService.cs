namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Models.NodeBinding;

/// <summary>
///     Application service for i node binding behavior.
/// </summary>
public interface INodeBindingService : IAsyncDisposable
{
    Task<NodeBindingSession> StartBindingAsync(CancellationToken cancellationToken = default);

    Task<PollNodeBindingResponse> PollUntilTerminalAsync(NodeBindingSession session, CancellationToken cancellationToken = default);

    Task CancelAsync();
}
