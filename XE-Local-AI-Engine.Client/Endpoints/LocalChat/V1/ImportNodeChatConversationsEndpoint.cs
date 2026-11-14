namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Imports a chat export file as NEW chats, written through the ordinary chat writer.</summary>
/// <remarks>
///     Every conversation and message gets a fresh id, so the content is encrypted under its new AAD and nothing in the
///     file can collide with a stored row. The validator refuses the whole file before anything is written; a write
///     that still fails purges every chat this import created (a purge that fails too is logged) and surfaces the error.
///     With an <c>importId</c> it pushes progress on the chat hub; the response still comes once, at the end.
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
        var created = await _chatPersistence.CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = string.IsNullOrWhiteSpace(conversation.Title) ? null : conversation.Title.Trim(),
                UserId = null,
                CreatedAtUtc = conversation.CreatedAtUtc
            },
            ct);
        createdIds.Add(created.ConversationId);

        foreach (var message in conversation.Messages)
        {
            _ = await _chatPersistence.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
                {
                    ConversationId = created.ConversationId,
                    MessageId = Guid.NewGuid(),
                    Role = message.Role,
                    Content = message.Content,
                    CreatedAtUtc = message.CreatedAtUtc,
                    Model = message.Model
                },
                ct);
        }

        if (conversation.Archived)
        {
            _ = await _chatPersistence.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest
                {
                    ConversationId = created.ConversationId,
                    Archived = true,
                    UpdatedAtUtc = conversation.Messages.Count > 0 ? conversation.Messages[^1].CreatedAtUtc : conversation.CreatedAtUtc
                },
                ct);
        }

        return new NodeChatImportedConversationResponse
        {
            ConversationId = created.ConversationId,
            Title = created.Title,
            MessageCount = conversation.Messages.Count
        };
    }
}
