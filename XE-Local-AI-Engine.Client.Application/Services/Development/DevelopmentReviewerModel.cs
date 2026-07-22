namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;

internal enum DevelopmentReviewDisposition
{
    Approved,
    ChangesRequested
}

internal sealed record DevelopmentReviewFinding(string Category, string Summary);

internal sealed record DevelopmentReviewerSubmission(
    DevelopmentReviewDisposition Disposition,
    string Summary,
    IReadOnlyList<DevelopmentReviewFinding> Findings);

internal sealed record DevelopmentReviewerModelResult(
    DevelopmentReviewerSubmission Submission,
    long? InputTokens,
    long? OutputTokens);

internal interface IDevelopmentReviewerModel
{
    Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        DevelopmentCloudRoleRoute? cloudRoute = null,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentReviewerModel(
    IChatClient chatClient,
    IActiveCloudChatClientFactory cloudFactory,
    ILocalModelProviderResolver localProviderResolver) : IDevelopmentReviewerModel
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
    private readonly ILocalModelProviderResolver _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));

    public async Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        DevelopmentCloudRoleRoute? cloudRoute = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(tools);
        var isCloud = _cloudFactory.IsCloudProviderSelected(modelId);
        if (isCloud && cloudRoute is null)
        {
            throw new DevelopmentWorkspaceSecurityException("A cloud Development reviewer attempt requires an explicit immutable CloudScoped route.");
        }
        if (!isCloud && cloudRoute is not null)
        {
            throw new DevelopmentWorkspaceSecurityException("A CloudScoped route cannot be sent through a local Development model.");
        }

        var gateway = new ToolGateway(tools, maxToolCalls, liveProgress);
        ChatOptions options;
        IReadOnlyList<ChatMessage> messages;
        if (isCloud)
        {
            var resolvedProvider = _cloudFactory.ResolveActiveCloudProviderName(modelId);
            if (string.IsNullOrWhiteSpace(resolvedProvider)
                || !string.Equals(resolvedProvider, cloudRoute!.ProviderName, StringComparison.Ordinal)
                || !string.Equals(modelId, cloudRoute.ModelId, StringComparison.Ordinal))
            {
                throw new DevelopmentWorkspaceSecurityException("The selected cloud provider/model no longer matches the authorized Development route.");
            }

            options = cloudRoute.Options;
            options.ModelId = modelId;
            options.MaxOutputTokens = maxOutputTokens;
            options.AllowMultipleToolCalls = false;
            options.Tools =
            [
                .. (options.Tools ?? []),
                AIFunctionFactory.Create(gateway.SubmitReview, "submit_review", "Submit one typed approved or changes-requested review.")
            ];
            messages =
            [
                .. cloudRoute.Messages,
                new ChatMessage(ChatRole.User,
                    "Act as the independent read-only Development reviewer. Read only approved bundle resources and call submit_review exactly once. No repository, write, patch, command, apply, saved-agent, or chat-history capability is available.")
            ];
        }
        else
        {
            var localProvider = await _localProviderResolver.ResolveProviderForModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            var knownModels = await localProvider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (!knownModels.Any(model => model.IsAvailable
                                          && string.Equals(model.ModelName, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DevelopmentWorkspaceSecurityException("Development reviewer attempts require a known, available local model.");
            }

            options = new ChatOptions
            {
                ModelId = modelId,
                MaxOutputTokens = maxOutputTokens,
                AllowMultipleToolCalls = false,
                Tools =
                [
                    AIFunctionFactory.Create(gateway.ListFilesAsync, "list_files", "List files below a workspace-relative path."),
                    AIFunctionFactory.Create(gateway.ReadFileAsync, "read_file", "Read a bounded UTF-8 workspace file."),
                    AIFunctionFactory.Create(gateway.SearchTextAsync, "search_text", "Search fixed text below a workspace-relative path."),
                    AIFunctionFactory.Create(gateway.GetStatusAsync, "get_status", "Inspect the current Git status."),
                    AIFunctionFactory.Create(gateway.GetDiffAsync, "get_diff", "Inspect the current bounded Git diff."),
                    AIFunctionFactory.Create(gateway.SubmitReview, "submit_review", "Submit one typed approved or changes-requested review.")
                ]
            };
            messages =
            [
                new ChatMessage(ChatRole.System,
                    "You are the independent read-only Development reviewer. You have no write, patch, command, or apply capability. Inspect exact evidence and call submit_review exactly once."),
                new ChatMessage(ChatRole.User, prompt)
            ];
        }
        var providerCalls = Math.Max(1, maxToolCalls + 1);
        using var providerBudget = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = providerCalls,
            MaxCumulativeInputTokens = (int)Math.Min(int.MaxValue, Math.Max(1024L, (long)maxOutputTokens * providerCalls)),
            DefaultContextTokens = Math.Max(2048, maxOutputTokens * 2),
            ReservedOutputTokenFloor = maxOutputTokens,
            RecentMessagesToKeep = 2,
            OversizedToolResultExcerptChars = 2000
        });
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages,
            options,
            cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            liveProgress?.Output(update);
        }
        var response = updates.ToChatResponse();
        liveProgress?.CompleteOutput(response.Usage);

        var usage = response.Usage ?? throw new InvalidOperationException("The Development reviewer response did not report token usage.");
        var inputTokens = usage.InputTokenCount ?? throw new InvalidOperationException("The Development reviewer response did not report input-token usage.");
        var outputTokens = usage.OutputTokenCount ?? throw new InvalidOperationException("The Development reviewer response did not report output-token usage.");
        var accountedTokens = checked(inputTokens + outputTokens);
        var totalTokens = Math.Max(usage.TotalTokenCount ?? accountedTokens, accountedTokens);
        if (inputTokens < 0 || outputTokens < 0 || totalTokens < 0 || outputTokens > maxOutputTokens)
        {
            throw new InvalidOperationException("The Development reviewer exceeded the configured output-token limit.");
        }

        return new DevelopmentReviewerModelResult(gateway.Submission
                                                   ?? throw new InvalidOperationException("The reviewer attempt ended without a typed review submission."),
            inputTokens,
            outputTokens);
    }

    private sealed class ToolGateway(IDevelopmentWorkspaceTools tools,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress)
    {
        private readonly int _maxToolCalls = maxToolCalls;
        private readonly IDevelopmentWorkspaceTools _tools = tools;
        private readonly DevelopmentAttemptLiveProgress? _liveProgress = liveProgress;
        private int _toolCalls;

        public DevelopmentReviewerSubmission? Submission { get; private set; }

        public Task<string> ListFilesAsync([Description("Workspace-relative directory; empty means repository root.")] string? path,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("list_files", path, () => _tools.ListFilesAsync(path, cancellationToken));
        }

        public Task<string> ReadFileAsync([Description("Workspace-relative file path.")] string path,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("read_file", path, () => _tools.ReadFileAsync(path, cancellationToken));
        }

        public Task<string> SearchTextAsync([Description("Fixed text to search for.")] string pattern,
            [Description("Workspace-relative directory; empty means repository root.")] string? path,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("search_text", $"{path}:{pattern}", () => _tools.SearchTextAsync(pattern, path, cancellationToken));
        }

        public Task<string> GetStatusAsync(CancellationToken cancellationToken)
        {
            return InvokeAsync("get_status", null, () => _tools.GetStatusAsync(cancellationToken));
        }

        public Task<string> GetDiffAsync(CancellationToken cancellationToken)
        {
            return InvokeAsync("get_diff", null, () => _tools.GetDiffAsync(cancellationToken));
        }

        public string SubmitReview(string disposition,
            string summary,
            DevelopmentReviewFinding[]? findings = null)
        {
            Count("submit_review", null);
            if (Submission is not null)
            {
                throw new InvalidOperationException("The reviewer submission can be recorded only once.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(summary);
            if (!Enum.TryParse<DevelopmentReviewDisposition>(disposition, ignoreCase: true, out var parsed))
            {
                throw new ArgumentException("Review disposition must be Approved or ChangesRequested.", nameof(disposition));
            }

            var boundedFindings = (findings ?? []).Take(64).ToArray();
            if (parsed == DevelopmentReviewDisposition.Approved && boundedFindings.Length != 0)
            {
                throw new InvalidOperationException("An approved review cannot include unresolved findings.");
            }

            if (parsed == DevelopmentReviewDisposition.ChangesRequested && boundedFindings.Length == 0)
            {
                throw new InvalidOperationException("A changes-requested review requires at least one bounded finding.");
            }

            Submission = new DevelopmentReviewerSubmission(parsed, summary, boundedFindings);
            _liveProgress?.ToolCompleted("submit_review");
            return "typed review submission accepted";
        }

        private async Task<string> InvokeAsync(string toolId, string? arguments, Func<Task<string>> action)
        {
            Count(toolId, arguments);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _liveProgress?.ToolCompleted(toolId);
            }
        }

        private void Count(string toolId, string? arguments)
        {
            if (Interlocked.Increment(ref _toolCalls) > _maxToolCalls)
            {
                throw new InvalidOperationException("The Development reviewer exceeded the configured tool-call limit.");
            }
            _liveProgress?.ToolStarted(toolId, arguments);
        }
    }
}
