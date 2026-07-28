namespace XE_Local_AI_Engine.Client.Services.Development;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

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
    ILocalModelProviderResolver localProviderResolver) : IDevelopmentCoderModel
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));
    private readonly ILocalModelProviderResolver _localProviderResolver = localProviderResolver ?? throw new ArgumentNullException(nameof(localProviderResolver));

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
                    AIFunctionFactory.Create(gateway.WriteFileAsync, "write_file", "Write one bounded UTF-8 workspace file."),
                    AIFunctionFactory.Create(gateway.ApplyPatchAsync, "apply_patch", "Apply one bounded Git patch to safe workspace paths."),
                    AIFunctionFactory.Create(gateway.GetStatusAsync, "get_status", "Inspect the current Git status."),
                    AIFunctionFactory.Create(gateway.GetDiffAsync, "get_diff", "Inspect the current bounded Git diff."),
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

        var usage = response.Usage ?? throw new InvalidOperationException("The Development coder response did not report token usage.");
        var inputTokens = usage.InputTokenCount ?? throw new InvalidOperationException("The Development coder response did not report input-token usage.");
        var outputTokens = usage.OutputTokenCount ?? throw new InvalidOperationException("The Development coder response did not report output-token usage.");
        var accountedTokens = checked(inputTokens + outputTokens);
        var totalTokens = Math.Max(usage.TotalTokenCount ?? accountedTokens, accountedTokens);
        if (inputTokens < 0 || outputTokens < 0 || totalTokens < 0 || outputTokens > maxOutputTokens)
        {
            throw new InvalidOperationException("The Development coder exceeded the configured output-token limit.");
        }

        if (isCloud)
        {
            _ = await tools.ApplyPatchAsync(gateway.CloudPatch
                                            ?? throw new InvalidOperationException("The cloud coder submission did not include a bounded patch."),
                cancellationToken).ConfigureAwait(false);
        }

        return new DevelopmentCoderModelResult(gateway.Submission
                                               ?? throw new InvalidOperationException("The coder attempt ended without a typed implementation submission."),
            inputTokens,
            outputTokens);
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
                throw new InvalidOperationException("The coder submission can be recorded only once.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(summary);
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
                throw new InvalidOperationException("The Development coder exceeded the configured tool-call limit.");
            }

            _liveProgress?.ToolStarted(toolId, arguments);
        }
    }
}
