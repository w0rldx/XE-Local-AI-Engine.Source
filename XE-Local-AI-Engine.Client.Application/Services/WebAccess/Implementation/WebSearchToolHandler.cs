namespace XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>web_search</c>: returns the result the review gate stashed for THIS call,
///     and never reaches the network itself.
/// </summary>
/// <remarks>
///     The chat runner retrieves the content and the user reviews it before the framework executes the (always approved)
///     call. Any other caller, a blank call id or another invocation's call finds no stash entry and gets a refusal.
/// </remarks>
internal sealed class WebSearchToolHandler : IClientLocalToolHandler
{
    public string ToolName => WebSearchToolDefinition.ToolName;

    public string Description => WebSearchToolDefinition.Description;

    public string ParameterSchema => WebSearchToolDefinition.ParameterSchema;

    // Structural, like ask_user: the approval wrap is the pause the result review gate builds on.
    public bool RequiresApproval => true;

    /// <summary>How this handler learns its tool-call id; a test seam, as on <c>AskUserToolHandler</c>.</summary>
    internal Func<string?> ResolveCallId { get; set; } =
        static () => FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId;

    public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(WebReviewResultScope.TryPop(ResolveCallId(), out var resultJson)
            ? resultJson
            : WebReviewRetrieval.RefusalJson("not-reviewed",
                "This web_search call did not pass the chat result review, so no web content was added to the conversation."));
    }
}
