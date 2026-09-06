namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;

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
    ILocalModelProviderResolver localProviderResolver,
    IModelTrustResolver modelTrustResolver,
    ILogger<DevelopmentReviewerModel> logger) : IDevelopmentReviewerModel
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
    private readonly ILocalModelProviderResolver _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly ILogger<DevelopmentReviewerModel> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        await RejectExternalModelAsync(modelId, cancellationToken).ConfigureAwait(false);
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
        DevelopmentAttemptContextBudget contextBudget;
        if (isCloud)
        {
            var resolvedProvider = _cloudFactory.ResolveActiveCloudProviderName(modelId);
            if (string.IsNullOrWhiteSpace(resolvedProvider)
                || !string.Equals(resolvedProvider, cloudRoute!.ProviderName, StringComparison.Ordinal)
                || !string.Equals(modelId, cloudRoute.ModelId, StringComparison.Ordinal))
            {
                throw new DevelopmentWorkspaceSecurityException("The selected cloud provider/model no longer matches the authorized Development route.");
            }

            // A cloud route has no launched window to read, so it keeps the conservative synthetic budget.
            contextBudget = DevelopmentAttemptContextBudget.Unknown(maxOutputTokens);
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

            contextBudget = await DevelopmentAttemptContextBudget.ResolveAsync(localProvider, modelId, maxOutputTokens, "reviewer", _logger, cancellationToken)
                                                                 .ConfigureAwait(false);
            options = new ChatOptions
            {
                ModelId = modelId,
                MaxOutputTokens = contextBudget.RoundOutputTokens,
                AllowMultipleToolCalls = false,

                // The served window travels as the option the provider-round budgeter prefers, exactly as the chat and
                // orchestration lanes carry it, so a round is measured against the context the model really has. A
                // runtime that reports none sends no override, the same fallback every other lane takes.
                AdditionalProperties = contextBudget.Served
                    ? new AdditionalPropertiesDictionary
                    {
                        [SamplingOptionKeys.NumCtx] = contextBudget.ContextTokens
                    }
                    : null,
                Tools =
                [
                    AIFunctionFactory.Create(gateway.ListFilesAsync, "list_files", "List files below a workspace-relative path."),
                    AIFunctionFactory.Create(gateway.ReadFileAsync, "read_file", "Read a bounded UTF-8 workspace file."),
                    AIFunctionFactory.Create(gateway.SearchTextAsync, "search_text", "Search fixed text below a workspace-relative path."),
                    AIFunctionFactory.Create(gateway.GetStatusAsync, "get_status", "Inspect the current Git status."),
                    AIFunctionFactory.Create(gateway.GetDiffAsync, "get_diff",
                        "Inspect the bounded Git diff of the workspace against the base commit. Files created in this attempt are untracked and are not in it; get_status lists those."),
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
            DefaultContextTokens = contextBudget.ContextTokens,
            ReservedOutputTokenFloor = contextBudget.RoundOutputTokens,
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

        var usage = response.Usage;
        var (inputTokens, outputTokens) = DevelopmentAttemptOutputBudget.Accept(usage?.InputTokenCount,
            usage?.OutputTokenCount,
            usage?.TotalTokenCount,

            // The CONFIGURED per-call budget, not the round ceiling this attempt narrowed to fit the window: this is a
            // whole-attempt ceiling, and tightening it here would newly fail long tool loops that no round overspent.
            maxOutputTokens,
            providerCalls,
            "reviewer");

        return new DevelopmentReviewerModelResult(gateway.Submission
                                                  ?? throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSubmission,
                                                      "The Development reviewer stopped without calling submit_review, so the round produced no disposition. "
                                                      + "Re-run the review, or use a model that reliably closes with a tool call."),
            inputTokens,
            outputTokens);
    }

    /// <summary>
    ///     Refuses a Development reviewer attempt on an external OpenAI-compatible model that is not positively declared
    ///     node-local.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Dev Mode hands the model a workspace: real files, real patches, real command evidence. The plan's
    ///         non-goal is explicit — no dev-mode support at all for declared-cloud external models, under EITHER egress
    ///         policy — and UNRESOLVED is refused with them, because a connection deleted mid-attempt or a store that
    ///         will not decrypt tells us nothing about where the prompt would have gone.
    ///     </para>
    ///     <para>
    ///         Refused here rather than folded into <c>isCloud</c>: a declared-cloud external model has no CloudScoped
    ///         route either, so treating it as cloud would send it down the route-verification branch and fail with a
    ///         message about a mismatched authorized route rather than the real reason.
    ///     </para>
    /// </remarks>
    private async Task RejectExternalModelAsync(string modelId, CancellationToken cancellationToken)
    {
        if (!ExternalModelId.HasExternalScheme(modelId))
        {
            return;
        }

        if (await _modelTrustResolver.ResolveAsync(modelId, cancellationToken).ConfigureAwait(false) != ModelTrustLocality.Local)
        {
            throw new DevelopmentWorkspaceSecurityException("Development attempts cannot use an external model that is not declared local to this node's trust boundary.");
        }
    }

    private sealed class ToolGateway(
        IDevelopmentWorkspaceTools tools,
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
            [Description("Workspace-relative directory; empty means repository root.")]
            string? path,
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
                throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.DuplicateSubmission,
                    "The Development reviewer called submit_review more than once. Exactly one submission closes a review round.");
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSummary,
                    "The Development reviewer's submit_review call had an empty summary. The summary is the operator-facing record of the verdict.");
            }

            if (!Enum.TryParse<DevelopmentReviewDisposition>(disposition, ignoreCase: true, out var parsed))
            {
                // Handed BACK as a tool result rather than thrown, the way a rejected command id is. A thrown
                // ArgumentException here terminalizes the WHOLE reviewer attempt and costs the node one of its three —
                // measured live on 2026-09-02, three attempts in a row on one task, from a model that had produced a
                // valid Approved review on the same subject minutes earlier. A mis-spelled enum is a formatting flake,
                // and the model can answer a correction; it cannot answer a discarded attempt. The round still ends
                // unsubmitted if it never corrects itself, which is the existing missing-submission failure.
                _liveProgress?.ToolCompleted("submit_review");
                return "submit_review was not accepted: disposition must be exactly \"Approved\" or \"ChangesRequested\". "
                       + "Call submit_review again with one of those two values.";
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
                throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.ToolCallBudgetExceeded,
                    $"The Development reviewer asked for more than {_maxToolCalls} tool calls. "
                    + "Give the task a narrower objective, or raise Development:MaxToolCalls.");
            }

            _liveProgress?.ToolStarted(toolId, arguments);
        }
    }
}
