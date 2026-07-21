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
        CancellationToken cancellationToken = default);
}

internal sealed class DevelopmentCoderModel(IChatClient chatClient, IActiveCloudChatClientFactory cloudFactory) : IDevelopmentCoderModel
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    private readonly IActiveCloudChatClientFactory _cloudFactory = cloudFactory ?? throw new ArgumentNullException(nameof(cloudFactory));

    public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(tools);
        if (_cloudFactory.IsCloudProviderSelected(modelId))
        {
            throw new DevelopmentWorkspaceSecurityException("Gate 3 coder attempts require an explicitly local model.");
        }

        var gateway = new ToolGateway(tools, maxToolCalls);
        var options = new ChatOptions
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
        _ = await _chatClient.GetResponseAsync([
                new ChatMessage(ChatRole.System,
                    "You are the bounded local Development coder. Use only the provided workspace tools. Never claim completion without calling submit_implementation exactly once."),
                new ChatMessage(ChatRole.User, prompt)
            ],
            options,
            cancellationToken).ConfigureAwait(false);

        return new DevelopmentCoderModelResult(gateway.Submission
                                                ?? throw new InvalidOperationException("The coder attempt ended without a typed implementation submission."),
            InputTokens: null,
            OutputTokens: null);
    }

    private sealed class ToolGateway(IDevelopmentWorkspaceTools tools, int maxToolCalls)
    {
        private readonly IDevelopmentWorkspaceTools _tools = tools;
        private readonly int _maxToolCalls = maxToolCalls;
        private int _toolCalls;

        public DevelopmentCoderSubmission? Submission { get; private set; }

        public Task<string> ListFilesAsync(
            [Description("Workspace-relative directory; empty means repository root.")] string? path,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.ListFilesAsync(path, cancellationToken);
        }

        public Task<string> ReadFileAsync(
            [Description("Workspace-relative file path.")] string path,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.ReadFileAsync(path, cancellationToken);
        }

        public Task<string> SearchTextAsync(
            [Description("Fixed text to search for.")] string pattern,
            [Description("Workspace-relative directory; empty means repository root.")] string? path,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.SearchTextAsync(pattern, path, cancellationToken);
        }

        public Task<string> WriteFileAsync(
            [Description("Workspace-relative file path.")] string path,
            [Description("Complete UTF-8 file content.")] string content,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.WriteFileAsync(path, content, cancellationToken);
        }

        public Task<string> ApplyPatchAsync(
            [Description("Git unified diff with explicit diff --git path headers.")] string patch,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.ApplyPatchAsync(patch, cancellationToken);
        }

        public Task<string> GetStatusAsync(CancellationToken cancellationToken)
        {
            Count();
            return _tools.GetStatusAsync(cancellationToken);
        }

        public Task<string> GetDiffAsync(CancellationToken cancellationToken)
        {
            Count();
            return _tools.GetDiffAsync(cancellationToken);
        }

        public Task<string> RunCommandAsync(
            [Description("One of: git_status, git_diff_check, dotnet_restore, dotnet_build_release_no_restore, dotnet_test_release_no_build.")] string commandId,
            CancellationToken cancellationToken)
        {
            Count();
            return _tools.RunCommandAsync(commandId, cancellationToken);
        }

        public string SubmitImplementation(string summary, string[] changedFiles, string[] commandIds, string? notes = null)
        {
            Count();
            if (Submission is not null)
            {
                throw new InvalidOperationException("The coder submission can be recorded only once.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(summary);
            Submission = new DevelopmentCoderSubmission(summary,
                changedFiles ?? [],
                commandIds ?? [],
                notes);
            return "typed implementation submission accepted";
        }

        private void Count()
        {
            if (Interlocked.Increment(ref _toolCalls) > _maxToolCalls)
            {
                throw new InvalidOperationException("The Development coder exceeded the configured tool-call limit.");
            }
        }
    }
}
