namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Providers.LlamaServer;

public interface IBenchmarkFreezeDependencyService
{
    Task<BenchmarkFreezeDependencySetV1> CaptureAsync(Guid agentDefinitionId,
        ResolvedAgentRuntime resolvedRuntime,
        string primaryModelName,
        string? judgeModelName,
        CancellationToken cancellationToken);
}

public sealed class BenchmarkFreezeDependencyService(
    IAgentDefinitionStore agentDefinitions,
    IPlaybookActionStore playbooks,
    IAgentSkillStore skills,
    ICustomToolStore customTools,
    IInferenceProfileStore inferenceProfiles) : IBenchmarkFreezeDependencyService
{
    private readonly IAgentDefinitionStore _agentDefinitions = agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions));
    private readonly IPlaybookActionStore _playbooks = playbooks ?? throw new ArgumentNullException(nameof(playbooks));
    private readonly IAgentSkillStore _skills = skills ?? throw new ArgumentNullException(nameof(skills));
    private readonly ICustomToolStore _customTools = customTools ?? throw new ArgumentNullException(nameof(customTools));
    private readonly IInferenceProfileStore _inferenceProfiles = inferenceProfiles ?? throw new ArgumentNullException(nameof(inferenceProfiles));

    public async Task<BenchmarkFreezeDependencySetV1> CaptureAsync(Guid agentDefinitionId,
        ResolvedAgentRuntime resolvedRuntime,
        string primaryModelName,
        string? judgeModelName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolvedRuntime);
        var agent = await _agentDefinitions.GetByIdAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new BenchmarkEligibilityException("The selected agent definition no longer exists.");
        if (agent.Kind != AgentDefinitionKind.Single
            || resolvedRuntime.Kind != AgentDefinitionKind.Single
            || resolvedRuntime.AgentDefinitionId != agent.Id
            || resolvedRuntime.AgentDefinitionVersion != agent.Version)
        {
            throw new BenchmarkEligibilityException("The selected agent definition changed during benchmark resolution.");
        }

        var playbookRows = await _playbooks.ListByAgentAsync(agent.Id, cancellationToken).ConfigureAwait(false);
        var skillRows = await LoadAssignedSkillsAsync(agent, cancellationToken).ConfigureAwait(false);
        var customToolRows = await LoadAssignedCustomToolsAsync(agent, cancellationToken).ConfigureAwait(false);
        var profiles = await _inferenceProfiles.ListAsync(cancellationToken).ConfigureAwait(false);

        return new BenchmarkFreezeDependencySetV1(Hash(agent),
            Hash(playbookRows.OrderBy(static item => item.Id).ToArray()),
            Hash(skillRows),
            Hash(customToolRows),
            HashProfiles(profiles, primaryModelName),
            string.IsNullOrWhiteSpace(judgeModelName) ? null : HashProfiles(profiles, judgeModelName));
    }

    private async Task<IReadOnlyList<object>> LoadAssignedSkillsAsync(AgentDefinitionRecord agent, CancellationToken cancellationToken)
    {
        var result = new List<object>();
        foreach (var id in (agent.AllowedSkillIds ?? []).Distinct().Order())
        {
            var skill = await _skills.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
                        ?? throw new BenchmarkEligibilityException("An assigned benchmark skill no longer exists.");
            var resources = await _skills.ListResourcesAsync(id, cancellationToken).ConfigureAwait(false);
            result.Add(new
            {
                Skill = skill,
                Resources = resources.OrderBy(static resource => resource.Id).ToArray()
            });
        }

        return result;
    }

    private async Task<IReadOnlyList<CustomToolRecord>> LoadAssignedCustomToolsAsync(AgentDefinitionRecord agent, CancellationToken cancellationToken)
    {
        var assignedNames = agent.AllowedToolNames.Where(static name => name.StartsWith("custom__", StringComparison.Ordinal))
                                 .ToHashSet(StringComparer.Ordinal);
        var rows = await _customTools.ListAsync(cancellationToken).ConfigureAwait(false);
        if (assignedNames.Except(rows.Select(static row => row.Name), StringComparer.Ordinal).Any())
        {
            throw new BenchmarkEligibilityException("An assigned benchmark custom tool no longer exists.");
        }

        return rows.Where(row => assignedNames.Contains(row.Name)).OrderBy(static row => row.Id).ToArray();
    }

    private static string HashProfiles(IReadOnlyList<InferenceProfileRecord> profiles, string modelName) =>
        Hash(profiles.Where(profile => profile.Role == (int)ModelRole.Chat
                                       && string.Equals(profile.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static profile => profile.MachineKey, StringComparer.Ordinal)
                     .ThenBy(static profile => profile.Backend, StringComparer.Ordinal)
                     .ThenBy(static profile => profile.Id)
                     .ToArray());

    private static string Hash<T>(T value) =>
        $"sha256:{Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)))}";
}
