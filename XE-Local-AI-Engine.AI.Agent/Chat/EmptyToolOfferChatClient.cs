namespace XE_Local_AI_Engine.AI.Agent.Chat;

using Microsoft.Extensions.AI;

/// <summary>Skips function-call recovery when the caller offered no tools.</summary>
internal sealed class EmptyToolOfferChatClient : DelegatingChatClient
{
    private readonly IChatClient _providerClient;

    public EmptyToolOfferChatClient(IChatClient functionInvokingClient, IChatClient providerClient)
        : base(functionInvokingClient)
    {
        _providerClient = providerClient ?? throw new ArgumentNullException(nameof(providerClient));
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Resolve(options).GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Resolve(options).GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    // The direct provider reference is borrowed; the base owns it through the function-invoking chain.
    private IChatClient Resolve(ChatOptions? options)
    {
        return options?.Tools is not { Count: > 0 } ? _providerClient : InnerClient;
    }
}
