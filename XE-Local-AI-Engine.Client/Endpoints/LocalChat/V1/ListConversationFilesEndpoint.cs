namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     FastEndpoints handler listing the file attachments for a conversation (GET). Returns metadata only — never the
///     raw bytes or extracted text.
/// </summary>
public sealed class ListConversationFilesEndpoint : Endpoint<ListConversationUploadsRequest, ListConversationUploadsResponse>
{
    private readonly IConversationUploadedFileStore _fileStore;

    public ListConversationFilesEndpoint(IConversationUploadedFileStore fileStore)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        _fileStore = fileStore;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.ConversationUploads);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListConversationUploadsRequest req, CancellationToken ct)
    {
        var files = await _fileStore.ListAsync(req.ConversationId, ct);

        await Send.OkAsync(new ListConversationUploadsResponse
            {
                Items = files.Select(static file => file.ToResponse()).ToArray()
            },
            ct);
    }
}
