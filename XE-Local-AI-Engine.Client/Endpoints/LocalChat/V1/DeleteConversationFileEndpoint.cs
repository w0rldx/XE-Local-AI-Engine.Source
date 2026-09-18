namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     FastEndpoints handler removing one uploaded attachment (DELETE): drops the metadata row plus the on-disk
///     encrypted bytes and cached Markdown. Returns 204 when removed, 404 when no such file exists for the conversation.
/// </summary>
public sealed class DeleteConversationFileEndpoint : Endpoint<DeleteConversationUploadRequest>
{
    private readonly IConversationUploadedFileStore _fileStore;

    public DeleteConversationFileEndpoint(IConversationUploadedFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        _fileStore = fileStore;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalChat.ConversationUploadById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteConversationUploadRequest req, CancellationToken ct)
    {
        var removed = await _fileStore.DeleteAsync(req.ConversationId, req.FileId, ct);
        if (!removed)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
