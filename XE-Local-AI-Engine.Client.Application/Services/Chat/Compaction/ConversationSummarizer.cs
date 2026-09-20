namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IConversationSummarizer" />, running a NODE-LOCAL model at temperature 0 to fold an older
///     conversation span into a compact synopsis.
/// </summary>
/// <remarks>
///     The model is resolved per-model through <see cref="ILocalModelProviderResolver" />, never the shared
///     cloud-capable singleton, so conversation content never crosses the node boundary. A span larger than
///     <see cref="ConversationCompactionOptions.MaxInputCharsPerSummarizationCall" /> folds in multiple passes, a
///     running summary plus the next batch, so no request exceeds the model's window. Tests substitute a fake
///     summarizer, mirroring the memory-extraction seam, so CI needs no runtime.
/// </remarks>
internal sealed class ConversationSummarizer : IConversationSummarizer
{
    private readonly ConversationCompactionOptions _options;

    // The string SENT as the system message and the string budget validation CHARGES must come from one rendering;
    // a drift between them silently invalidates every budget decision.
    private readonly string _systemPrompt;

    // The synopsis ceiling renders at HALF the configured cap, never hard-coded: a ceiling near the cap lets the
    // running summary reach it within a few folds, after which the rune-safe clamp cuts the tail off every later one.
    private const string SystemPromptTemplate = """
                                                You compress an ongoing chat conversation into a single compact synopsis so the assistant can keep going
                                                after the older turns are dropped from its context window.

                                                You are given a JSON object with an optional "priorSummary" (a synopsis of even older turns) and "messages"
                                                (the newer turns to fold in, oldest first). Produce ONE updated synopsis that merges the prior summary with
                                                the new messages.

                                                Rules:
                                                - Preserve everything the assistant must remember to continue: facts established, decisions made, the user's
                                                  stated goals and preferences, named entities/files/values, and any open questions or unfinished tasks.
                                                - Keep every fact, decision, named entity, file path, value and open item from "priorSummary" and "messages",
                                                  written as compressed notes: the synopsis must be far shorter than the messages it replaces.
                                                - Hard limit: keep the synopsis under {0} characters. If it would not fit, merge related items and shorten
                                                  wording; keep the facts.
                                                - Write the synopsis in the conversation's language: the most recent user turn's if they mix, or the language of
                                                  "priorSummary" if this input has no user turn.
                                                - Write terse third-person notes, not a transcript. Drop pleasantries, restated questions, and filler.
                                                - Do NOT answer or continue the conversation, and do NOT add information that is not in the input.
                                                - Output ONLY the synopsis text — no preamble, no headings, no code fences.
                                                """;

    // CA1863: the template is formatted on every construction and on every options validation, so parse it once.
    private static readonly CompositeFormat SystemPromptFormat = CompositeFormat.Parse(SystemPromptTemplate);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // Budget accounting measures the SERIALIZED form, where the default encoder charges six characters per Han
        // rune. Relaxed escaping is safe only for a body that is never rendered, so keep these options private.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly int FrameOverhead = JsonSerializer.Serialize(ToPromptModel(string.Empty,
        [new ConversationSummarizerMessage { Role = "user", Content = "😀" }]), SerializerOptions).Length;

    private readonly ILogger<ConversationSummarizer> _logger;
    private readonly ILocalModelProviderResolver _providerResolver;

    public ConversationSummarizer(
        ILocalModelProviderResolver providerResolver,
        IOptions<ConversationCompactionOptions> options,
        ILogger<ConversationSummarizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _systemPrompt = RenderSystemPrompt(options.Value.MaxSummaryChars);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(providerResolver);
        _providerResolver = providerResolver;
    }

    public async Task<string?> SummarizeAsync(ConversationSummarizerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ModelName) || input.Messages.Count == 0)
        {
            return null;
        }

        // Route the model to the runtime that serves it, node-local only and never the cloud singleton. THIS
        // resolution IS the privacy invariant: conversation content only ever reaches a per-provider chat client.
        var provider = await _providerResolver.ResolveProviderForModelAsync(input.ModelName, cancellationToken);
        var selection = new LocalModelSelection
        {
            ModelName = input.ModelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection).WithProviderTelemetry();

        // The span folds in batches bounded by the TOTAL model-facing budget, splitting an oversized message into
        // fragments. A pass yielding nothing aborts everything, or the covered sequence advances past lost messages.
        var budget = Math.Max(1, _options.MaxInputCharsPerSummarizationCall);
        var running = string.IsNullOrWhiteSpace(input.PriorSummary) ? null : input.PriorSummary;
        var batch = new List<ConversationSummarizerMessage>();

        foreach (var message in input.Messages)
        {
            var remainingContent = message.Content ?? string.Empty;
            do
            {
                var wholeRemainder = new ConversationSummarizerMessage { Role = message.Role, Content = remainingContent };
                if (RequestFitsBudget(running, [.. batch, wholeRemainder], budget))
                {
                    batch.Add(wholeRemainder);
                    break;
                }

                if (batch.Count > 0)
                {
                    running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken);
                    if (running is null)
                    {
                        return null;
                    }

                    batch.Clear();
                    continue;
                }

                var prefixLength = FindLargestFittingPrefix(message.Role, remainingContent, running, budget);
                if (prefixLength == 0)
                {
                    _logger.LogWarning("Conversation summarization request overhead exceeded the configured {Budget}-character total request budget; aborting without advancing coverage.", budget);
                    return null;
                }

                batch.Add(new ConversationSummarizerMessage { Role = message.Role, Content = remainingContent[..prefixLength] });
                remainingContent = remainingContent[prefixLength..];

                // A fragment was necessary, so flush it before considering the rest. The returned synopsis becomes
                // the prior summary of the next request and is included in that request's budget calculation.
                running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken);
                if (running is null)
                {
                    return null;
                }

                batch.Clear();
            } while (remainingContent.Length > 0);
        }

        if (batch.Count > 0)
        {
            running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(running) ? null : running;
    }

    // One fold pass: (prior summary + one batch) -> updated summary. Returns null when the model yields nothing, so the
    // caller aborts the whole summarization rather than advancing coverage over a batch that was never summarized.
    private async Task<string?> FoldAsync(IChatClient chatClient,
        string? priorSummary,
        IReadOnlyList<ConversationSummarizerMessage> batch,
        bool supportsThinking,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, JsonSerializer.Serialize(ToPromptModel(priorSummary, batch), SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f,

            // A deliberately LOOSE token cap whose job is to stop a reasoning model spending a whole window on one
            // fold. It can bind first on a byte-fallback script, so TruncateAtRuneBoundary is the only length guarantee.
            MaxOutputTokens = Math.Max(1, _options.MaxSummaryChars)
        };

        // Reasoning OFF for a fold, or an unbounded reasoning block spends the token cap and returns no synopsis.
        // Written as InvocationAgentFactory writes it, both halves at once, and GATED on the thinking capability.
        if (supportsThinking)
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false,
                [InvocationAgentFactory.LlamaDisableThinkingMarkerKey] = true
            };
        }

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken);
        var text = response.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Conversation summarizer returned no usable text for a fold pass; aborting so coverage is not advanced past un-summarized messages.");
            return null;
        }

        var clamped = TruncateAtRuneBoundary(text, Math.Max(1, _options.MaxSummaryChars));

        // One line per fold: how close the synopsis came to the rendered ceiling, and - because only the summarizer
        // emits it - how many folds a compaction ran. Debug, so it costs nothing outside a diagnostic round.
        _logger.LogDebug("Conversation summarizer fold produced a {Length}-character synopsis.", clamped.Length);
        return clamped;
    }

    internal static string RenderSystemPrompt(int maxSummaryChars) =>
        string.Format(CultureInfo.InvariantCulture, SystemPromptFormat, Math.Max(1, maxSummaryChars / 2));

    internal static long GetMinimumRequestBudget(int maxSummaryChars) =>
        RenderSystemPrompt(maxSummaryChars).Length + FrameOverhead + (long)Math.Max(0, maxSummaryChars);

    private int FindLargestFittingPrefix(string role, string content, string? priorSummary, int budget)
    {
        var low = 1;
        var high = content.Length;
        var best = 0;
        while (low <= high)
        {
            var midpoint = low + ((high - low) / 2);
            var candidateLength = MoveBeforeSplitSurrogate(content, midpoint);
            if (candidateLength == 0)
            {
                low = midpoint + 1;
                continue;
            }

            var candidate = new ConversationSummarizerMessage { Role = role, Content = content[..candidateLength] };
            if (RequestFitsBudget(priorSummary, [candidate], budget))
            {
                best = candidateLength;
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return best;
    }

    internal static string TruncateAtRuneBoundary(string value, int maximumChars)
    {
        if (value.Length <= maximumChars)
        {
            return value;
        }

        return value[..MoveBeforeSplitSurrogate(value, maximumChars)];
    }

    private static int MoveBeforeSplitSurrogate(string value, int index)
    {
        return index > 0
               && index < value.Length
               && char.IsHighSurrogate(value[index - 1])
               && char.IsLowSurrogate(value[index])
            ? index - 1
            : index;
    }

    private bool RequestFitsBudget(string? priorSummary, IReadOnlyList<ConversationSummarizerMessage> batch, int budget)
    {
        var serializedPrompt = JsonSerializer.Serialize(ToPromptModel(priorSummary, batch), SerializerOptions);
        return _systemPrompt.Length + serializedPrompt.Length <= budget;
    }

    private static object ToPromptModel(string? priorSummary, IReadOnlyList<ConversationSummarizerMessage> messages)
    {
        return new
        {
            PriorSummary = priorSummary,
            Messages = messages.Select(static message => new
            {
                message.Role,
                message.Content
            }).ToArray()
        };
    }
}
