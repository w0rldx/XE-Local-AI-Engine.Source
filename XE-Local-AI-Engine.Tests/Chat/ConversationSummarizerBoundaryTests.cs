namespace XE_Local_AI_Engine.Tests.Chat;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
        var validationResults = new List<System.ComponentModel.DataAnnotations.ValidationResult>();

        var valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
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

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _responseFactory(Requests.Count - 1))));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
