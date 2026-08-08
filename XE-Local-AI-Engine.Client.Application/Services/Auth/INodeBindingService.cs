namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Models.NodeBinding;

public interface INodeBindingService : IAsyncDisposable
{
    Task<NodeBindingSession> StartBindingAsync(CancellationToken cancellationToken = default);

    Task<PollNodeBindingResponse> PollUntilTerminalAsync(NodeBindingSession session, CancellationToken cancellationToken = default);

    Task CancelAsync();
}
