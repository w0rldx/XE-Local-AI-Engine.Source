namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>Exports one chat, or every chat with archived ones flagged, as the text-only chat export file.</summary>
/// <remarks>
///     Chats only: the list read filters <c>kind = chat</c> and purged rows, and a by-id request for a work-session or
///     integration transcript is a 404 like a missing one. Each chat is flattened to its selected path.
/// </remarks>
public sealed class ExportNodeChatConversationsEndpoint : Endpoint<ExportNodeChatConversationsRequest, NodeChatExportDocument>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;
    private readonly IConversationUploadedFileStore _uploadedFiles;

    public ExportNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence,
        IConversationUploadedFileStore uploadedFiles,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(uploadedFiles);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _uploadedFiles = uploadedFiles;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.Export);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ExportNodeChatConversationsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        IReadOnlyList<Guid> conversationIds;
        if (req.ConversationId is { } conversationId)
        {
            if (!string.Equals(await _chatPersistence.GetConversationKindAsync(conversationId, ct), NodeConversationKind.Chat, StringComparison.Ordinal))
            {
                throw new NodeChatConversationNotFoundException(conversationId);
            }

            conversationIds = [conversationId];
        }
        else
        {
            var summaries = await _chatPersistence.ListConversationsAsync(new NodeChatListConversationsRequest
                {
                    IncludeArchived = true
                },
                ct);
            conversationIds = [.. summaries.Select(static summary => summary.ConversationId)];
        }

        // Built whole in memory and NOT capped, unlike the import: an export of a huge history is a file this node's
        // import refuses. Stream per conversation (and split the file) if exports outgrow memory or the import caps.
        var conversations = new List<NodeChatExportConversation>(conversationIds.Count);
        foreach (var id in conversationIds)
        {
            // A chat deleted between the list and this read is simply not exported.
            if (await _chatPersistence.GetConversationAsync(id, ct) is { } conversation)
            {
                conversations.Add(ToExport(conversation, await _uploadedFiles.ListAsync(id, ct)));
            }
        }

        await Send.OkAsync(new NodeChatExportDocument
            {
                Format = NodeChatExportFile.Format,
                FormatVersion = NodeChatExportFile.FormatVersion,
                ExportedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Conversations = conversations
            },
            ct);
    }

    // The selected path in ANCHOR order, as the send path reads it, keeping only user and assistant turns with text: a
    // failed or cancelled reply with no text has nothing to export, and the import refuses an empty message.
    private static NodeChatExportConversation ToExport(NodeChatConversationDto conversation, IReadOnlyList<ConversationUploadedFileInfo> files)
    {
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        return new NodeChatExportConversation
        {
            Title = conversation.Title,
            CreatedAtUtc = conversation.CreatedAtUtc,
            Archived = conversation.Archived,
            Attachments =
            [
                .. files.Select(static file => new NodeChatExportAttachment
                {
                    FileName = file.OriginalFileName,
                    MimeType = file.MimeType,
                    SizeBytes = file.SizeBytes
                })
            ],
            Messages =
            [
                .. SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath)
                                       .Where(static message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content))
                                       .OrderBy(anchorSequence)
                                       .Select(static message => new NodeChatExportMessage
                                       {
                                           Role = message.Role,
                                           Content = message.Content,
                                           CreatedAtUtc = message.CreatedAtUtc,
                                           Model = message.Model,
                                           ToolCallCount = message.Parts?.Count(static part => part.Kind == NodeChatMessagePartKinds.Tool) ?? 0
                                       })
            ]
        };
    }
}
