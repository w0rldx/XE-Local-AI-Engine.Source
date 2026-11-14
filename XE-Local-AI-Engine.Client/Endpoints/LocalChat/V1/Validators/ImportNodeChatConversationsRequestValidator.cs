namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>The trust boundary of a chat import: format and version first, then caps, roles and text, all before any write.</summary>
/// <remarks>
///     Rules use <c>Must</c>, never range or length rules: the export RESPONSE shares the document type, and FastEndpoints
///     would publish those as schema bounds that the browser's response validation then enforces on an export.
/// </remarks>
public sealed class ImportNodeChatConversationsRequestValidator : Validator<ImportNodeChatConversationsRequest>
{
    /// <summary>How far past this node's clock a time in the file may lie: another node's clock may run ahead, a time years out is corrupt.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromDays(1);

    public ImportNodeChatConversationsRequestValidator()
    {
        RuleFor(static request => request.Document)
            .Cascade(CascadeMode.Stop)
            .Must(static document => document is not null)
            .WithMessage("The request body must be a chat export file.")
            .ChildRules(document =>
            {
                document.RuleFor(static request => request.Format)
                        .Must(static format => string.Equals(format, NodeChatExportFile.Format, StringComparison.Ordinal))
                        .WithMessage($"This is not a chat export file: its format must be '{NodeChatExportFile.Format}'.");

                document.RuleFor(static request => request.FormatVersion)
                        .Must(static version => version == NodeChatExportFile.FormatVersion)
                        .WithMessage($"This node reads chat export files of format version {NodeChatExportFile.FormatVersion} only.");

                // Unconditional: a null list fails the first rule, and Stop keeps it from reaching the message count.
                document.RuleFor(static request => request.Conversations)
                        .Cascade(CascadeMode.Stop)
                        .Must(static conversations => conversations is { Count: > 0 and <= NodeChatExportFile.MaxConversations } && conversations.All(static conversation => conversation is not null))
                        .WithMessage($"A chat export file must hold between 1 and {NodeChatExportFile.MaxConversations} chats.")
                        .Must(static conversations => conversations.Sum(static conversation => conversation?.Messages?.Count ?? 0) <= NodeChatExportFile.MaxMessages)
                        .WithMessage($"A chat export file may hold at most {NodeChatExportFile.MaxMessages} messages.");

                document.RuleForEach(static request => request.Conversations)
                        .ChildRules(conversation =>
                        {
                            conversation.RuleFor(static c => c.Title)
                                        .Must(static title => title is null || title.Length <= NodeChatExportFile.MaxTitleLength)
                                        .WithMessage($"A chat title may be at most {NodeChatExportFile.MaxTitleLength} characters.");

                            conversation.RuleFor(static c => c.CreatedAtUtc)
                                        .Must(IsPlausibleTime)
                                        .WithMessage("A chat's creation time must be neither negative nor in the future.");

                            conversation.RuleFor(static c => c.Messages)
                                        .Must(static messages => messages is not null && messages.All(static message => message is not null))
                                        .WithMessage("A chat needs a message list, even an empty one, and no empty entries.");

                            conversation.RuleForEach(static c => c.Messages)
                                        .ChildRules(message =>
                                        {
                                            message.RuleFor(static m => m.Role)
                                                   .Must(static role => role is "user" or "assistant")
                                                   .WithMessage(static m => $"Message role '{m.Role}' cannot be imported: only user and assistant messages can, not tool or system messages.");

                                            message.RuleFor(static m => m.Content)
                                                   .Must(static content => !string.IsNullOrWhiteSpace(content))
                                                   .WithMessage("A message needs text.");

                                            message.RuleFor(static m => m.CreatedAtUtc)
                                                   .Must(IsPlausibleTime)
                                                   .WithMessage("A message's creation time must be neither negative nor in the future.");
                                        });
                        })
                        .When(static request => request.Conversations is not null);
            });
    }

    // Unix milliseconds from 1970 up to this node's now plus the skew, read from the registered clock per request.
    private bool IsPlausibleTime(long unixMilliseconds)
    {
        return unixMilliseconds >= 0 && unixMilliseconds <= (Resolve<TimeProvider>().GetUtcNow() + ClockSkew).ToUnixTimeMilliseconds();
    }
}
