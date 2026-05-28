namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class LocalChatHub(
    INodeChatStreamService streamService,
    INodeChatRegenerationService regenerationService,
    IInvocationResumeRegistry resumeRegistry) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return streamService.SendMessageAsync(request, cancellationToken);
    }

    /// <summary>
    ///     Regenerates an assistant turn as a SIBLING VARIANT (Phase 5.2) and streams the run like a normal send:
    ///     assistant-queued/streaming/delta/completed. Mints the variant placeholder, drives it through the shared
    ///     runner/pump, and persists INTO that placeholder — never overwriting the original. Throws for an
    ///     Origin=Remote (view-only) conversation or an unknown conversation/message.
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> RegenerateMessage(Guid conversationId,
        Guid originalMessageId,
        string? reasoningEffort,
        bool useLocalTools,
        IReadOnlyDictionary<Guid, Guid>? selectedPath,
        CancellationToken cancellationToken)
    {
        return regenerationService.RegenerateAsync(conversationId, originalMessageId, reasoningEffort, useLocalTools, selectedPath, cancellationToken);
    }

    /// <summary>
    ///     Re-attaches to a still-running invocation after the client reconnects with a NEW connection id. The
    ///     first event replays the content accumulated so far, then live deltas and the terminal event follow in
    ///     order. Throws when the invocation is unknown or already terminal — the client then re-fetches the
    ///     persisted conversation instead (Phase 2.2).
    /// </summary>
    public IAsyncEnumerable<ChatStreamEvent> ResumeMessage(Guid invocationId,
        CancellationToken cancellationToken)
    {
        return resumeRegistry.ResumeAsync(invocationId, cancellationToken);
    }
}
