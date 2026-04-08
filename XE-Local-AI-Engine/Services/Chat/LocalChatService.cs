namespace XE_Local_AI_Engine.Services.Chat;

using Microsoft.Extensions.AI;

public sealed class LocalChatService
{
    private const int MaxHistoryMessages = 20;

    private readonly IChatClient _chatClient;
    private readonly List<ChatMessage> _history = [];

    public LocalChatService(IChatClient chatClient, ILocalToolExecutor toolExecutor, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(toolExecutor);
        ArgumentNullException.ThrowIfNull(configuration);

        _chatClient = chatClient;
        SelectedModel = configuration["Ollama:ChatModel"] ?? "qwen3.5:9b";
    }

    public string SelectedModel { get; set; }

    public async IAsyncEnumerable<string> SendMessageAsync(string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        var responseChunks = new List<string>();

        var options = new ChatOptions { ModelId = SelectedModel };

        await foreach (var update in _chatClient.GetStreamingResponseAsync(_history, options, ct).ConfigureAwait(false))
        {
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                responseChunks.Add(text);
                yield return text;
            }
        }

        var fullResponse = string.Concat(responseChunks);
        if (!string.IsNullOrEmpty(fullResponse))
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, fullResponse));
        }

        TrimHistory();
    }

    public void ClearHistory()
    {
        _history.Clear();
    }

    public IReadOnlyList<ChatMessage> GetHistory()
    {
        return _history.AsReadOnly();
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaxHistoryMessages)
        {
            return;
        }

        // Preserve system message if present at index 0
        var hasSystemMessage = _history.Count > 0 && _history[0].Role == ChatRole.System;
        var removeFrom = hasSystemMessage ? 1 : 0;
        var toRemove = _history.Count - MaxHistoryMessages;

        _history.RemoveRange(removeFrom, toRemove);
    }
}
