namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.AspNetCore.Mvc;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Imports a chat export file as NEW chats, written through the ordinary chat writer.</summary>
/// <remarks>
///     Every conversation and message gets a fresh id, so the content is encrypted under its new AAD and nothing in the
///     file can collide with a stored row. The validator refuses the whole file before anything is written; a write
///     that still fails purges every chat this import created (a purge that fails too is logged) and surfaces the error.
/// </remarks>
public sealed class ImportNodeChatConversationsEndpoint : Endpoint<NodeChatExportDocument, NodeChatImportResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public ImportNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
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

    public override async Task HandleAsync(NodeChatExportDocument req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var createdIds = new List<Guid>(req.Conversations.Count);
        var imported = new List<NodeChatImportedConversationResponse>(req.Conversations.Count);
        try
        {
            foreach (var conversation in req.Conversations)
            {
                imported.Add(await ImportAsync(conversation, createdIds, ct));
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

            throw;
        }

        await Send.OkAsync(new NodeChatImportResponse
            {
                Conversations = imported
            },
            ct);
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
