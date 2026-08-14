namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

public sealed record BenchmarkProjectDraft(
    Guid Id,
    string Name,
    string CoreTask,
    int ContextTokens,
    Guid AgentDefinitionId,
    bool JudgeEnabled,
    string? JudgeModelName,
    int? JudgeContextTokens,
    int JudgePromptVersion = 1,
    int JudgeOutputSchemaVersion = 1);

public interface IBenchmarkProjectService
{
    Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);
    Task<BenchmarkProjectRecord> UpdateAsync(Guid projectId, long expectedVersion, BenchmarkProjectDraft draft, CancellationToken cancellationToken = default);
}

public sealed class BenchmarkProjectService(
    IBenchmarkStore benchmarkStore,
    IAgentDefinitionStore agentDefinitionStore,
    IBenchmarkInstalledModelLeaseProvider installedModels) : IBenchmarkProjectService
{
    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));

    public async Task<BenchmarkProjectRecord> CreateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken = default)
    {
        var input = await ValidateAsync(draft, cancellationToken).ConfigureAwait(false);
        return await _benchmarkStore.CreateProjectAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkProjectRecord> UpdateAsync(Guid projectId,
        long expectedVersion,
        BenchmarkProjectDraft draft,
        CancellationToken cancellationToken = default)
    {
        var input = await ValidateAsync(draft with
        {
            Id = projectId
        }, cancellationToken).ConfigureAwait(false);
        return await _benchmarkStore.UpdateProjectAsync(projectId, expectedVersion, input, cancellationToken).ConfigureAwait(false);
    }

    internal static string DecodeCoreTask(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(payload)
                   ?? throw new BenchmarkValidationException("The benchmark task is required.");
        }
        catch (JsonException exception)
        {
            throw new BenchmarkValidationException($"The benchmark task payload is invalid: {exception.Message}");
        }
    }

    private async Task<BenchmarkProjectInput> ValidateAsync(BenchmarkProjectDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.CoreTask))
        {
            throw new BenchmarkValidationException("Benchmark name and task are required.");
        }

        ValidateContext(draft.ContextTokens, "primary");
        var definition = await _agentDefinitionStore.GetByIdAsync(draft.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.Kind != AgentDefinitionKind.Single)
        {
            throw new BenchmarkValidationException("An existing Single agent definition is required.");
        }

        string? judgeModel = null;
        int? judgeContext = null;
        if (draft.JudgeEnabled)
        {
            judgeModel = draft.JudgeModelName?.Trim();
            if (string.IsNullOrWhiteSpace(judgeModel) || draft.JudgeContextTokens is not { } requestedJudgeContext)
            {
                throw new BenchmarkValidationException("An enabled judge requires a local model and context.");
            }

            ValidateContext(requestedJudgeContext, "judge");
            ValidateJudgeVersions(draft.JudgePromptVersion, draft.JudgeOutputSchemaVersion);
            try
            {
                await using var judgeLease = await _installedModels.AcquireAsync(judgeModel, cancellationToken).ConfigureAwait(false);
                BenchmarkModelEligibility.Validate(judgeLease.Snapshot, "judge");
            }
            catch (KeyNotFoundException exception)
            {
                throw new BenchmarkValidationException("The selected judge model is not installed or eligible.")
                {
                    Source = exception.Source
                };
            }
            catch (BenchmarkEligibilityException exception)
            {
                throw new BenchmarkValidationException(exception.Message)
                {
                    Source = exception.Source
                };
            }

            judgeContext = requestedJudgeContext;
        }
        else
        {
            ValidateJudgeVersions(draft.JudgePromptVersion, draft.JudgeOutputSchemaVersion);
        }

        return new BenchmarkProjectInput(draft.Id,
            draft.Name.Trim(),
            JsonSerializer.SerializeToUtf8Bytes(draft.CoreTask),
            draft.ContextTokens,
            draft.AgentDefinitionId,
            draft.JudgeEnabled,
            judgeModel,
            judgeContext,
            draft.JudgePromptVersion,
            draft.JudgeOutputSchemaVersion);
    }

    private static void ValidateContext(int contextTokens, string role)
    {
        if (!LlamaServerLaunchPolicyOptions.ChatContextTiers.Contains(contextTokens))
        {
            throw new BenchmarkValidationException($"The {role} context budget is not supported.");
        }
    }

    private static void ValidateJudgeVersions(int promptVersion, int outputSchemaVersion)
    {
        if (!BenchmarkFrozenPolicies.SupportsVersions(promptVersion, outputSchemaVersion))
        {
            throw new BenchmarkValidationException("The benchmark judge prompt or output schema version is not supported.");
        }
    }
}
