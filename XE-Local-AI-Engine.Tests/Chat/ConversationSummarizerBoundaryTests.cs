namespace XE_Local_AI_Engine.Tests.Chat;

using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConversationSummarizerBoundaryTests
{
    [Test]
    public async Task SummarizeAsync_WhenOneMessageExceedsBudget_SplitsItWithoutAnyRequestExceedingTotalBudget()
    {
        const int requestBudget = 1800;
        var content = string.Concat(Enumerable.Repeat("quoted \\\"value\\\" and slash \\\\ and newline\n", 90));
        using var client = new CapturingChatClient();
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("local");
        provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(client);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync("model", Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));
        var summarizer = new ConversationSummarizer(resolver,
            Options.Create(new ConversationCompactionOptions
            {
                MaxInputCharsPerSummarizationCall = requestBudget
            }),
            NullLogger<ConversationSummarizer>.Instance);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", content)],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.True(client.Requests.Count > 1, "An individually oversized message must be split across fold requests.");
        AssertEx.True(client.Requests.All(request => request.Sum(message => message.Text?.Length ?? 0) <= requestBudget),
            "The configured limit is a total serialized request budget, including the system prompt and prior summary.");

        var reconstructed = string.Concat(client.Requests.SelectMany(static request => PromptContents(request)));
        AssertEx.Equal(content, reconstructed, "Splitting must neither omit nor duplicate any source characters.");
    }

    [Test]
    public void OptionsValidation_WhenTotalBudgetCannotFitPromptSummaryAndOneRune_RejectsConfiguration()
    {
        var options = new ConversationCompactionOptions
        {
            MaxSummaryChars = ConversationCompactionOptions.MaximumSummaryChars,
            MaxInputCharsPerSummarizationCall = ConversationCompactionOptions.MinimumInputCharsPerSummarizationCall
        };
        var validationResults = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(options,
            new ValidationContext(options),
            validationResults,
            validateAllProperties: true);

        AssertEx.False(valid);
        AssertEx.Contains(validationResults, result => result.ErrorMessage?.Contains("total request", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Test]
    public async Task SummarizeAsync_WhenIntermediateSummaryIsOversized_CapsItBeforeTheNextRequest()
    {
        const int requestBudget = 1800;
        const int summaryCap = 120;
        using var client = new CapturingChatClient(responseFactory: requestIndex => requestIndex == 0 ? new string('s', 2000) : "done");
        var summarizer = CreateSummarizer(client, requestBudget, summaryCap);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 1400))],
            "model")).ConfigureAwait(false);

        AssertEx.Equal("done", result);
        AssertEx.True(client.Requests.Count > 1);
        using var secondPrompt = JsonDocument.Parse(client.Requests[1].Single(message => message.Role == ChatRole.User).Text);
        AssertEx.Equal(summaryCap, secondPrompt.RootElement.GetProperty("priorSummary").GetString()?.Length ?? -1,
            "An intermediate model response must be capped before it becomes the next request's prior summary.");
    }

    [Test]
    public async Task SummarizeAsync_WhenOversizedMessageContainsNonBmpRunes_NeverSplitsASurrogatePair()
    {
        const int requestBudget = 1800;
        var content = string.Concat(Enumerable.Repeat("😀", 900));
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget, maxSummaryChars: 120);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", content)],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        var fragments = client.Requests.SelectMany(static request => PromptContents(request)).ToList();
        AssertEx.True(fragments.All(static fragment => !char.IsHighSurrogate(fragment[^1]) && !char.IsLowSurrogate(fragment[0])),
            "Every fragment boundary must fall between complete Unicode scalar values.");
        AssertEx.Equal(content, string.Concat(fragments));
    }

    [Test]
    public async Task FoldAsync_SetsMaxOutputTokensToTheConfiguredSummaryCap()
    {
        // Multi-fold on purpose: an unbounded fold is what stalls a reasoning model, so EVERY request must carry the cap,
        // not just the first.
        const int summaryCap = 300;
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 1800, summaryCap);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 4000))],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.True(client.Options.Count > 1, "The oversized message must be folded in more than one pass.");
        AssertEx.True(client.Options.All(options => options?.MaxOutputTokens == summaryCap),
            "Every fold request must cap generation at the configured synopsis size.");
    }

    [Test]
    public async Task FoldAsync_KeepsTemperatureAtZero()
    {
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 1800, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 4000))],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.NotEmpty(client.Options);
        AssertEx.True(client.Options.All(options => options?.Temperature == 0f),
            "A fold is a deterministic compression pass; the output cap must not disturb its temperature.");
    }

    [Test]
    public async Task SummarizeAsync_WhenAFoldReturnsNothing_AbortsWithoutAdvancingCoverage()
    {
        // A blank fold response must fail the WHOLE summarization: returning the partial synopsis would let the caller
        // advance the covered sequence past messages that were never summarized.
        using var client = new CapturingChatClient(responseFactory: requestIndex => requestIndex == 1 ? string.Empty : "running summary");
        var summarizer = CreateSummarizer(client, requestBudget: 1800, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 6000))],
            "model")).ConfigureAwait(false);

        AssertEx.Null(result, "A fold that yields no text must abort the summarization rather than return partial coverage.");
        AssertEx.Equal(expected: 2, client.Requests.Count, "No further fold may be attempted once one returned nothing.");
    }

    [Test]
    public async Task FoldAsync_WhenTheModelSupportsThinking_DisablesItOnEveryRequest()
    {
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 1800, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 4000))],
            "model",
            SupportsThinking: true)).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.True(client.Options.Count > 1, "The oversized message must be folded in more than one pass.");
        AssertEx.True(client.Options.All(static options => options?.AdditionalProperties is { } properties
                                                           && properties.ContainsKey("think")
                                                           && properties["think"] is false),
            "Every fold on a thinking-capable model must send the Ollama think:false field.");
        AssertEx.True(client.Options.All(static options => options?.AdditionalProperties is { } properties
                                                           && properties.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey)
                                                           && properties[InvocationAgentFactory.LlamaDisableThinkingMarkerKey] is true),
            "llama.cpp ignores think:false, so every fold must also carry the disable-thinking template marker.");
    }

    [Test]
    public async Task FoldAsync_WhenTheModelDoesNotSupportThinking_SendsNoThinkingFields()
    {
        // Mirrors InvocationAgentFactoryTests.CreateAsync_WhenNativeReasoningModel_OmitsThinkAndNeverDisablesTemplateThinking:
        // Ollama rejects think on a non-thinking model, and a native-reasoning template has no enable_thinking kwarg.
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 1800, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", new string('a', 4000))],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.NotEmpty(client.Options);
        AssertEx.True(client.Options.All(static options => options?.AdditionalProperties?.ContainsKey("think") != true),
            "A non-thinking model must never be sent the think field.");
        AssertEx.True(client.Options.All(static options => options?.AdditionalProperties?.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey) != true),
            "A native-reasoning model must never be sent enable_thinking=false — the harmony template has no such kwarg.");
    }

    [Test]
    public async Task FoldAsync_WhenABatchContainsHanCharacters_SerializesThemWithoutEscaping()
    {
        // ConversationSummarizer.RequestFitsBudget measures the SERIALIZED request, so the JSON encoder decides what a
        // Han character costs. The default encoder writes every non-ASCII rune as \uXXXX - six budget characters each -
        // which makes a CJK running summary unaffordable now that the synopsis language is pinned to the conversation's.
        const string han = "\u672c\u5730\u63a8\u7406\u8282\u70b9\u76d1\u7763 llama-server \u5b50\u8fdb\u7a0b";
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 6000, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", han)],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.NotEmpty(client.Requests);
        var sent = client.Requests[0].Single(static message => message.Role == ChatRole.User).Text;
        AssertEx.Contains(sent, han, StringComparison.Ordinal,
            "A Han batch must reach the model as raw UTF-16, not as escapes that cost six budget characters per character.");
        AssertEx.False(sent.Contains("\\u", StringComparison.Ordinal),
            "A BMP rune escaped to \\uXXXX is what inflates the serialized budget cost; no fold request may carry one.");
    }

    [Test]
    public async Task FoldAsync_SendsTheFidelityAndLanguageRulesInTheSystemPrompt()
    {
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 6000, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", "the node supervises llama-server")],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.NotEmpty(client.Requests);
        var systemPrompt = client.Requests[0].Single(static message => message.Role == ChatRole.System).Text;
        AssertEx.Contains(systemPrompt, "from \"priorSummary\" and \"messages\"", StringComparison.Ordinal,
            "The fidelity rule must bind the new messages too: fold 0 has no prior summary, so a priorSummary-only "
            + "rule could not make an early fact survive its own fold.");
        AssertEx.Contains(systemPrompt, "written as compressed notes", StringComparison.Ordinal,
            "Fidelity without a compression demand is what drove the model to near-transcription, filling the cap "
            + "within a few folds so the clamp cut the tail off every fold after.");
        AssertEx.Contains(systemPrompt, "in the conversation's language", StringComparison.Ordinal,
            "Nothing else in the pipeline detects or sets the synopsis language; the prompt is the only pin.");
    }

    [Test]
    public async Task SummarizeAsync_WhenAHanBatchFitsOnlyUnderRelaxedEscaping_SendsItAsOneRequest()
    {
        // 200 Han characters, at this test's maxSummaryChars of 300 (so the prompt renders "under 150 characters" and
        // is 1,399 chars, one shorter than the 1,400 of the 4,000-cap rendering). RequestFitsBudget charges the prompt
        // plus the SERIALIZED request: a 63-char JSON frame plus the content. Relaxed, that is 1,399 + 63 + 200 =
        // 1,662, which fits the 2,000 budget; under the default encoder each Han rune becomes a 6-char \uXXXX escape,
        // so the same batch is 1,399 + 63 + 1,200 = 2,662 and does not, and one short message would be fragmented.
        // The 63 here is this request's own frame, NOT the FrameOverhead constant: that one probes with an emoji, whose
        // supplementary scalar the relaxed encoder still writes as two escapes, and it is charged only by
        // GetMinimumRequestBudget. This pins that RequestFitsBudget measures the relaxed form the request carries.
        const int requestBudget = 2000;
        var content = new string('中', 200);
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget, maxSummaryChars: 300);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", content)],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.Equal(expected: 1, client.Requests.Count,
            "A Han batch that fits the budget under relaxed escaping must be folded in a single pass, not fragmented.");
        AssertEx.Equal(content, string.Concat(client.Requests.SelectMany(static request => PromptContents(request))));
    }

    [Test]
    [Arguments(4000, "under 2000 characters")]
    [Arguments(1000, "under 500 characters")]
    public async Task SystemPrompt_StatesTheCeilingDerivedFromTheConfiguredCap(int maxSummaryChars, string expectedCeiling)
    {
        using var client = new CapturingChatClient();
        var summarizer = CreateSummarizer(client, requestBudget: 6000, maxSummaryChars);

        var result = await summarizer.SummarizeAsync(new ConversationSummarizerInput(null,
            [new ConversationSummarizerMessage("user", "the node supervises llama-server")],
            "model")).ConfigureAwait(false);

        AssertEx.NotNull(result);
        AssertEx.NotEmpty(client.Requests);
        var systemPrompt = client.Requests[0].Single(static message => message.Role == ChatRole.System).Text;
        AssertEx.Contains(systemPrompt, expectedCeiling, StringComparison.Ordinal,
            "The stated ceiling must track the configured cap at half of it; a ceiling near the cap invites a synopsis "
            + "the clamp then tail-cuts on every later fold.");
        AssertEx.Equal(ConversationSummarizer.RenderSystemPrompt(maxSummaryChars).Length, systemPrompt.Length,
            "Sending and budget validation must charge one rendering; a drift between them invalidates every budget decision.");
    }

    private static ConversationSummarizer CreateSummarizer(CapturingChatClient client, int requestBudget, int maxSummaryChars)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("local");
        provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(client);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync("model", Arg.Any<CancellationToken>()).Returns(Task.FromResult(provider));
        return new ConversationSummarizer(resolver,
            Options.Create(new ConversationCompactionOptions
            {
                MaxInputCharsPerSummarizationCall = requestBudget,
                MaxSummaryChars = maxSummaryChars
            }),
            NullLogger<ConversationSummarizer>.Instance);
    }

    private static IEnumerable<string> PromptContents(IReadOnlyList<ChatMessage> request)
    {
        using var document = JsonDocument.Parse(request.Single(message => message.Role == ChatRole.User).Text);
        foreach (var message in document.RootElement.GetProperty("messages").EnumerateArray())
        {
            yield return message.GetProperty("content").GetString()
                         ?? throw new InvalidOperationException("The summarizer prompt emitted a null message content value.");
        }
    }

    private sealed class CapturingChatClient(Func<int, string>? responseFactory = null) : IChatClient
    {
        private readonly Func<int, string> _responseFactory = responseFactory ?? (_ => "short running summary");

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        /// <summary>The ChatOptions of each captured request, index-aligned with <see cref="Requests" />.</summary>
        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            Options.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseFactory(Requests.Count - 1))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose()
        {
        }
    }
}
