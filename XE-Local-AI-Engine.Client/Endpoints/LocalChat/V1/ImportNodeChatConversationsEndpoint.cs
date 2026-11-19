namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Imports a chat export file as NEW chats, each written in one transaction by the chat writer.</summary>
/// <remarks>
///     Fresh ids, so content is encrypted under its new AAD and nothing collides with a stored row. The validator
///     refuses a bad file before any write. Each chat holds the write lock for its own transaction only, at most
///     MaxMessages rows within MaxImportBytes. A failed chat leaves nothing; the import then purges the chats it
///     committed and surfaces the error. A crash between chats keeps those; a re-import writes them under new ids.
/// </remarks>
public sealed class ImportNodeChatConversationsEndpoint : Endpoint<ImportNodeChatConversationsRequest, NodeChatImportResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly LocalChatConversationChangePublisher _changePublisher;
    private readonly TimeProvider _timeProvider;

    public ImportNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence,
        LocalChatConversationChangePublisher changePublisher,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(changePublisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _changePublisher = changePublisher;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.Import);
        Policies(NodeAuthorizationPolicies.Operator);
        // The metadata is what Kestrel enforces while reading; the filter answers a declared oversize before binding.
        Options(static builder => builder.WithMetadata(new RequestSizeLimitAttribute(NodeChatExportFile.MaxImportBytes))
                                         .AddEndpointFilter(static async (context, next) =>
                                             context.HttpContext.Request.ContentLength > NodeChatExportFile.MaxImportBytes
                                                 ? RequestBodyTooLargeProblem.Result(NodeChatExportFile.TooLargeDetail)
                                                 : await next(context)));
        Description(static builder => builder.Produces<NodeChatImportResponse>()
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge));
    }

    public override async Task HandleAsync(ImportNodeChatConversationsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var conversations = req.Document.Conversations;
        var total = conversations.Count;
        var createdIds = new List<Guid>(total);
        var imported = new List<NodeChatImportedConversationResponse>(total);
        try
        {
            await PublishProgressAsync(req.ImportId, 0, total, ImportProgressStates.Running, ct);
            foreach (var conversation in conversations)
            {
                imported.Add(await ImportAsync(conversation, createdIds, ct));
                // At most one push per percent, so a file of thousands of small chats does not flood the hub.
                if (imported.Count < total && imported.Count * 100 / total != (imported.Count - 1) * 100 / total)
                {
                    await PublishProgressAsync(req.ImportId, imported.Count, total, ImportProgressStates.Running, ct);
                }
            }
        }
        catch
        {
            // Not the request token: a cancelled import still has to undo what it wrote. One purge failing must neither
            // stop the rest nor replace the error that caused the rollback, which the rethrow below surfaces.
            foreach (var createdId in createdIds)
            {
                try
                {
                    await _chatPersistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest
                        {
                            ConversationId = createdId,
                            DeletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                            PurgeImmediately = true
                        },
                        CancellationToken.None);
                }
                catch (Exception rollbackFailure)
                {
                    Logger.LogWarning(rollbackFailure, "A failed chat import could not purge conversation {ConversationId} it had created; it stays.", createdId);
                }
            }

            await PublishProgressAsync(req.ImportId, imported.Count, total, ImportProgressStates.Failed, CancellationToken.None);
            throw;
        }

        await PublishProgressAsync(req.ImportId, total, total, ImportProgressStates.Finished, ct);
        // One list push for the whole file, not one per chat.
        await _changePublisher.PublishAsync(conversationId: null, ct);
        await Send.OkAsync(new NodeChatImportResponse
            {
                Conversations = imported
            },
            ct);
    }

    // Without an import id nobody can tell the pushes apart, so none are sent.
    private Task PublishProgressAsync(Guid? importId, int importedChats, int totalChats, string state, CancellationToken ct)
    {
        return importId is { } id
            ? _changePublisher.PublishImportProgressAsync(id, importedChats, totalChats, state, ct)
            : Task.CompletedTask;
    }

    private async Task<NodeChatImportedConversationResponse> ImportAsync(NodeChatExportConversation conversation, List<Guid> createdIds, CancellationToken ct)
    {
        var title = string.IsNullOrWhiteSpace(conversation.Title) ? null : conversation.Title.Trim();
        var conversationId = await _chatPersistence.ImportConversationAsync(new NodeChatImportConversationRequest
            {
                Title = title,
                CreatedAtUtc = conversation.CreatedAtUtc,
                Archived = conversation.Archived,
                Messages =
                [
                    .. conversation.Messages.Select(static message => new NodeChatImportMessage
                    {
                        Role = message.Role,
                        Content = message.Content,
                        CreatedAtUtc = message.CreatedAtUtc,
                        Model = message.Model
                    })
                ]
            },
            ct);
        createdIds.Add(conversationId);

        return new NodeChatImportedConversationResponse
        {
            ConversationId = conversationId,
            Title = title,
            MessageCount = conversation.Messages.Count
        };
    }
}
