namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction;

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Default <see cref="IConversationSummarizer" />: runs a NODE-LOCAL model (resolved per-model via
///     <see cref="ILocalModelProviderResolver" />, never the shared cloud-capable <see cref="IChatClient" /> singleton)
///     at temperature 0 to fold an older conversation span into a compact synopsis. Conversation content reaches the
///     model on-node only — it never crosses the node boundary. A span larger than
///     <see cref="ConversationCompactionOptions.MaxInputCharsPerSummarizationCall" /> is folded in multiple passes
///     (running summary + next batch) so no single provider request exceeds the model's context window — the
///     oversized-conversation case this feature most needs to handle. Not unit-tested against a live model; tests
///     substitute a fake <see cref="IConversationSummarizer" /> (mirroring the memory-extraction agent seam, so CI needs
///     no runtime).
/// </summary>
internal sealed class ConversationSummarizer(
    ILocalModelProviderResolver providerResolver,
    IOptions<ConversationCompactionOptions> options,
    ILogger<ConversationSummarizer> logger) : IConversationSummarizer
{
    private readonly ConversationCompactionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    // The string SENT as the system message and the string budget validation CHARGES must come from one rendering;
    // a drift between them silently invalidates every budget decision. The null guard is repeated rather than read
    // off _options because a field initializer cannot reference another instance field (CS0236) — repeating it is
    // what removes the dependency on _options being declared first.
    private readonly string _systemPrompt = RenderSystemPrompt((options ?? throw new ArgumentNullException(nameof(options))).Value.MaxSummaryChars);

    // The synopsis ceiling is rendered from the configured cap, never hard-coded: at a MaxSummaryChars below the
    // 4,000 default a prompt that still promised 3,500 characters would invite a synopsis the rune-safe clamp in
    // FoldAsync then cuts from the tail, which is the exact fidelity loss this prompt exists to remove.
    private const string SystemPromptTemplate = """
                                        You compress an ongoing chat conversation into a single compact synopsis so the assistant can keep going
                                        after the older turns are dropped from its context window.

                                        You are given a JSON object with an optional "priorSummary" (a synopsis of even older turns) and "messages"
                                        (the newer turns to fold in, oldest first). Produce ONE updated synopsis that merges the prior summary with
                                        the new messages.

                                        Rules:
                                        - Preserve everything the assistant must remember to continue: facts established, decisions made, the user's
                                          stated goals and preferences, named entities/files/values, and any open questions or unfinished tasks.
                                        - Carry EVERY fact, entity, file path, value, decision and open item in "priorSummary" and in "messages" into the
                                          output. When space is tight, shorten wording, not coverage; drop a fact only when a later message supersedes it.
                                        - Keep the synopsis under {0} characters. If it would exceed that, merge and compress items; delete none.
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
        // Budget accounting is in characters and RequestFitsBudget measures the SERIALIZED form. The default encoder
        // escapes every non-ASCII rune to \uXXXX, so a Han character costs 6 budget characters - pinning the synopsis
        // language would make a CJK running summary unaffordable and abort compaction. Relaxed escaping is safe here:
        // this JSON is a node-local model request body, never rendered as HTML, never embedded in a script or an
        // attribute, never persisted, and it stays valid JSON. It frees BMP runes only; supplementary scalars (emoji)
        // are still written as two escapes, so the probe frame below is unchanged. Keep these options private to this
        // class - any reuse on a rendered surface breaks that argument.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly int FrameOverhead = JsonSerializer.Serialize(ToPromptModel(string.Empty,
        [new ConversationSummarizerMessage("user", "😀")]), SerializerOptions).Length;

    private readonly ILogger<ConversationSummarizer> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILocalModelProviderResolver _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));

    public async Task<string?> SummarizeAsync(ConversationSummarizerInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ModelName) || input.Messages.Count == 0)
        {
            return null;
        }

        // Route the model to the runtime that serves it (persisted map, else the configured default provider). Node-local
        // only — never the cloud singleton. THIS resolution is the privacy invariant: conversation content only ever
        // reaches a provider.CreateChatClient(...) client.
        var provider = await _providerResolver.ResolveProviderForModelAsync(input.ModelName, cancellationToken).ConfigureAwait(false);
        var selection = new LocalModelSelection
        {
            ModelName = input.ModelName,
            ProviderName = provider.ProviderName
        };

        // IChatClient is IDisposable — dispose the per-run node-local client.
        using var chatClient = provider.CreateChatClient(selection);

        // Fold the span in batches bounded by the TOTAL model-facing character budget (system prompt + serialized
        // user JSON, including the running summary) so no single request overruns the model's context window. An
        // individually oversized message is split into contiguous fragments; sending it alone would still violate the
        // bound this option promises.
        // If ANY pass yields nothing, abort the whole summarization (return null) — a partial summary that silently
        // omits a failed batch would let the caller advance the covered-sequence past messages that were never
        // summarized, dropping them from every later prompt. Failing whole leaves coverage unchanged; the user retries.
        var budget = Math.Max(1, _options.MaxInputCharsPerSummarizationCall);
        var running = string.IsNullOrWhiteSpace(input.PriorSummary) ? null : input.PriorSummary;
        var batch = new List<ConversationSummarizerMessage>();

        foreach (var message in input.Messages)
        {
            var remainingContent = message.Content ?? string.Empty;
            do
            {
                var wholeRemainder = new ConversationSummarizerMessage(message.Role, remainingContent);
                if (RequestFitsBudget(running, [.. batch, wholeRemainder], budget))
                {
                    batch.Add(wholeRemainder);
                    break;
                }

                if (batch.Count > 0)
                {
                    running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken).ConfigureAwait(false);
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

                batch.Add(new ConversationSummarizerMessage(message.Role, remainingContent[..prefixLength]));
                remainingContent = remainingContent[prefixLength..];

                // A fragment was necessary, so flush it before considering the rest. The returned synopsis becomes
                // the prior summary of the next request and is included in that request's budget calculation.
                running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken).ConfigureAwait(false);
                if (running is null)
                {
                    return null;
                }

                batch.Clear();
            } while (remainingContent.Length > 0);
        }

        if (batch.Count > 0)
        {
            running = await FoldAsync(chatClient, running, batch, input.SupportsThinking, cancellationToken).ConfigureAwait(false);
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

            // A token cap numerically equal to the CHARACTER cap below. At least one character per token holds for Latin
            // and for CJK — where a Qwen3-class tokenizer runs about 1.3 characters per token — so on those scripts the
            // cap sits above the character backstop and cannot bind first. It is NOT a universal property: byte-fallback
            // BPE spends several tokens per character for any character outside its merge table (Georgian, Burmese,
            // Devanagari, rare CJK, most emoji), and for a synopsis in such a script the token cap CAN bind before the
            // character backstop. TruncateAtRuneBoundary below therefore stays the only guarantee on synopsis length.
            // Deliberately LOOSE: the cap's job is to stop a reasoning model spending a 64k window on one fold, not to
            // size the synopsis. Same Math.Max(1, ...) guard the character truncation already uses, so a pathological
            // configured value cannot produce a non-positive cap.
            MaxOutputTokens = Math.Max(1, _options.MaxSummaryChars)
        };

        // Reasoning OFF for a fold: a synopsis needs no scratchpad, and an unbounded reasoning block would spend the
        // MaxOutputTokens cap above and return no synopsis at all. Written exactly as InvocationAgentFactory.CreateAsync
        // writes it — the Ollama `think` field AND the llama.cpp template marker together, because the two runtimes read
        // different halves. GATED on the model's thinking capability for the two reasons that factory records: Ollama
        // rejects `think` on a non-thinking model, and a NATIVE-reasoning template (gpt-oss harmony) has no
        // enable_thinking kwarg and must never be sent one.
        if (supportsThinking)
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false,
                [InvocationAgentFactory.LlamaDisableThinkingMarkerKey] = true
            };
        }

        var response = await chatClient.GetResponseAsync(messages, chatOptions, cancellationToken).ConfigureAwait(false);
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
        string.Format(CultureInfo.InvariantCulture, SystemPromptFormat, (Math.Max(1, maxSummaryChars) * 7) / 8);

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

            var candidate = new ConversationSummarizerMessage(role, content[..candidateLength]);
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
