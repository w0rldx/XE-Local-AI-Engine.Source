namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;

internal sealed record DevelopmentCoderSubmission(
    string Summary,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> CommandIds,
    string? Notes);

internal sealed record DevelopmentCoderModelResult(
    DevelopmentCoderSubmission Submission,
    long? InputTokens,
    long? OutputTokens);

internal interface IDevelopmentCoderModel
{
    Task<DevelopmentCoderModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        DevelopmentCloudRoleRoute? cloudRoute = null,
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentCoderModel(
    IChatClient chatClient,
    IActiveCloudChatClientFactory cloudFactory,
    ILocalModelProviderResolver localProviderResolver,
    IModelTrustResolver modelTrustResolver,
    ILogger<DevelopmentCoderModel> logger) : IDevelopmentCoderModel
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
    private readonly ILocalModelProviderResolver _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));
    private readonly IModelTrustResolver _modelTrustResolver = modelTrustResolver ?? throw new ArgumentNullException(nameof(modelTrustResolver));
    private readonly ILogger<DevelopmentCoderModel> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
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
            throw new DevelopmentWorkspaceSecurityException("A cloud Development coder attempt requires an explicit immutable CloudScoped route.");
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
                AIFunctionFactory.Create(gateway.SubmitCloudImplementation,
                    "submit_implementation",
                    "Submit one bounded Git patch and typed implementation evidence. The host applies the patch only inside the isolated Development workspace.")
            ];
            messages =
            [
                .. cloudRoute.Messages,
                new ChatMessage(ChatRole.User,
                    "Act as the bounded Development coder. Read only approved bundle resources and call submit_implementation exactly once. Do not claim command execution; no shell or general repository capability is available.")
            ];
        }
        else
        {
            var localProvider = await _localProviderResolver.ResolveProviderForModelAsync(modelId, cancellationToken).ConfigureAwait(false);
            var knownModels = await localProvider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            if (!knownModels.Any(model => model.IsAvailable
                                          && string.Equals(model.ModelName, modelId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DevelopmentWorkspaceSecurityException("Development coder attempts require a known, available local model.");
            }

            contextBudget = await DevelopmentAttemptContextBudget.ResolveAsync(localProvider, modelId, maxOutputTokens, "coder", _logger, cancellationToken)
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
                    AIFunctionFactory.Create(gateway.WriteFileAsync, "write_file", "Write one bounded UTF-8 workspace file."),
                    AIFunctionFactory.Create(gateway.ApplyPatchAsync, "apply_patch", "Apply one bounded Git patch to safe workspace paths."),
                    AIFunctionFactory.Create(gateway.GetStatusAsync, "get_status", "Inspect the current Git status."),
                    AIFunctionFactory.Create(gateway.GetDiffAsync, "get_diff", "Inspect the bounded Git diff of the workspace against the base commit. Files created in this attempt are untracked and are not in it; get_status lists those."),
                    AIFunctionFactory.Create(gateway.RunCommandAsync, "run_command", "Run one code-owned command id from the fixed Development catalog."),
                    AIFunctionFactory.Create(gateway.SubmitImplementation, "submit_implementation", "Submit the typed final implementation evidence after all changes are complete.")
                ]
            };
            messages =
            [
                new ChatMessage(ChatRole.System,
                    "You are the bounded local Development coder. Use only the provided workspace tools. Never claim completion without calling submit_implementation exactly once."),
                new ChatMessage(ChatRole.User, prompt)
            ];
        }

        var providerCalls = Math.Max(1, maxToolCalls + 1);
        var cumulativeInputTokens = (int)Math.Min(int.MaxValue, Math.Max(1024L, (long)maxOutputTokens * providerCalls));
        using var providerBudget = ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
        {
            MaxProviderCallsPerInvocation = providerCalls,
            MaxCumulativeInputTokens = cumulativeInputTokens,
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
            "coder");

        if (isCloud)
        {
            _ = await tools.ApplyPatchAsync(gateway.CloudPatch
                                            ?? throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSubmission,
                                                "The cloud Development coder submission did not include a bounded patch, so there is nothing to apply to the workspace."),
                cancellationToken).ConfigureAwait(false);
        }

        return new DevelopmentCoderModelResult(gateway.Submission
                                               ?? throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSubmission,
                                                   "The Development coder stopped without calling submit_implementation, so the attempt produced no evidence to validate. "
                                                   + "Any workspace changes it made are preserved; re-run the task, or use a model that reliably closes with a tool call."),
            inputTokens,
            outputTokens);
    }

    /// <summary>
    ///     Refuses a Development coder attempt on an external OpenAI-compatible model that is not positively declared
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
        private readonly IDevelopmentWorkspaceTools _tools = tools;
        private readonly int _maxToolCalls = maxToolCalls;
        private readonly DevelopmentAttemptLiveProgress? _liveProgress = liveProgress;
        private int _toolCalls;

        public DevelopmentCoderSubmission? Submission { get; private set; }
        public string? CloudPatch { get; private set; }

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

        public Task<string> WriteFileAsync([Description("Workspace-relative file path.")] string path,
            [Description("Complete UTF-8 file content.")]
            string content,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("write_file", path, () => _tools.WriteFileAsync(path, content, cancellationToken));
        }

        public Task<string> ApplyPatchAsync([Description("Git unified diff with explicit diff --git path headers.")] string patch,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("apply_patch", DevelopmentAttemptLiveSanitizer.StableFingerprint("patch", patch),
                () => _tools.ApplyPatchAsync(patch, cancellationToken));
        }

        public Task<string> GetStatusAsync(CancellationToken cancellationToken)
        {
            return InvokeAsync("get_status", null, () => _tools.GetStatusAsync(cancellationToken));
        }

        public Task<string> GetDiffAsync(CancellationToken cancellationToken)
        {
            return InvokeAsync("get_diff", null, () => _tools.GetDiffAsync(cancellationToken));
        }

        // Deliberately generic: the set of valid ids is per-project now and comes from the command profile, which is
        // listed in the system prompt. An attribute cannot interpolate it (it must be a compile-time constant), and
        // hardcoding one repository's ids here is what this change exists to remove. The closed-enum contract is
        // enforced by DevelopmentCommandProfile.ResolveCommand, not by this description.
        public Task<string> RunCommandAsync([Description("The id of one command from the project's command profile, exactly as listed in the prompt.")] string commandId,
            CancellationToken cancellationToken)
        {
            return InvokeAsync("run_command", commandId, () => _tools.RunCommandAsync(commandId, cancellationToken));
        }

        public string SubmitImplementation(string summary, string[] changedFiles, string[] commandIds, string? notes = null)
        {
            Count("submit_implementation", null);
            if (Submission is not null)
            {
                throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.DuplicateSubmission,
                    "The Development coder called submit_implementation more than once. Exactly one submission closes an attempt.");
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new DevelopmentAttemptEvidenceException(DevelopmentAttemptFailureCodes.MissingSummary,
                    "The Development coder's submit_implementation call had an empty summary. The summary is the operator-facing record of what changed.");
            }

            Submission = new DevelopmentCoderSubmission(summary,
                changedFiles ?? [],
                commandIds ?? [],
                notes);
            _liveProgress?.ToolCompleted("submit_implementation");
            return "typed implementation submission accepted";
        }

        public string SubmitCloudImplementation(string summary,
            [Description("One bounded Git unified diff using repository-relative paths.")]
            string patch,
            string[] changedFiles,
            string? notes = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(patch);
            var result = SubmitImplementation(summary, changedFiles, [], notes);
            CloudPatch = patch;
            return result;
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
                    $"The Development coder asked for more than {_maxToolCalls} tool calls. "
                    + "Give the task a narrower objective, or raise Development:MaxToolCalls.");
            }

            _liveProgress?.ToolStarted(toolId, arguments);
        }
    }
}
