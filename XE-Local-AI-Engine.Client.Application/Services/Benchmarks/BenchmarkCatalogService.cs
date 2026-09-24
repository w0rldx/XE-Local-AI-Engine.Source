namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public interface IBenchmarkCatalogService
{
    Task<IReadOnlyList<BenchmarkEligibleAgent>> ListEligibleAgentsAsync(string modelName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkEligibleModel>> ListEligibleModelsAsync(int? contextTokens, CancellationToken cancellationToken = default);
}

internal sealed class BenchmarkCatalogService : IBenchmarkCatalogService
{
    private readonly IAgentDefinitionStore _agentDefinitions;
    private readonly IAgentDefinitionResolver _agentResolver;
    private readonly IModelCapabilityResolver _modelCapabilities;
    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy;
    private readonly IGgufModelStore _ggufModels;
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels;
    private readonly ILogger<BenchmarkCatalogService> _logger;

    public BenchmarkCatalogService(IAgentDefinitionStore agentDefinitions,
        IAgentDefinitionResolver agentResolver,
        IModelCapabilityResolver modelCapabilities,
        IBenchmarkEligibilityPolicy eligibilityPolicy,
        IGgufModelStore ggufModels,
        IBenchmarkInstalledModelLeaseProvider installedModels,
        ILogger<BenchmarkCatalogService> logger)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitions);
        ArgumentNullException.ThrowIfNull(agentResolver);
        ArgumentNullException.ThrowIfNull(modelCapabilities);
        ArgumentNullException.ThrowIfNull(eligibilityPolicy);
        ArgumentNullException.ThrowIfNull(ggufModels);
        ArgumentNullException.ThrowIfNull(installedModels);
        ArgumentNullException.ThrowIfNull(logger);
        _agentDefinitions = agentDefinitions;
        _agentResolver = agentResolver;
        _modelCapabilities = modelCapabilities;
        _eligibilityPolicy = eligibilityPolicy;
        _ggufModels = ggufModels;
        _installedModels = installedModels;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BenchmarkEligibleAgent>> ListEligibleAgentsAsync(string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _ = await ReadEligibleModelFactsAsync(modelName.Trim(), cancellationToken);
        var (_, supportsTools, isCloud) = await _modelCapabilities.ResolveAsync(modelName, cancellationToken);
        if (isCloud)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.");
        }

        var definitions = await _agentDefinitions.ListAsync(cancellationToken);
        var eligible = new List<BenchmarkEligibleAgent>();
        foreach (var definition in definitions.Where(static definition => definition.Kind == AgentDefinitionKind.Single))
        {
            var runtime = await _agentResolver.ResolveAsync(definition.Id,
                modelName,
                retrievalQuery: string.Empty,
                supportsTools,
                honorModelProfile: false,
                activeModelIsCloud: false,
                cancellationToken);
            if (runtime is null)
            {
                continue;
            }

            try
            {
                _ = _eligibilityPolicy.Apply(runtime);
                eligible.Add(new BenchmarkEligibleAgent
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    Version = definition.Version
                });
            }
            catch (BenchmarkEligibilityException)
            {
                // The catalog advertises only runnable definitions. Rejection details are intentionally not public.
            }
        }

        return eligible.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(static item => item.Id)
                       .ToArray();
    }

    public async Task<IReadOnlyList<BenchmarkEligibleModel>> ListEligibleModelsAsync(int? contextTokens,
        CancellationToken cancellationToken = default)
    {
        if (contextTokens is <= 0)
        {
            throw new BenchmarkValidationException("Context tokens must be positive.");
        }

        var descriptors = await _ggufModels.ListInstalledModelsAsync(cancellationToken);
        var eligible = new List<BenchmarkEligibleModel>();
        foreach (var descriptor in descriptors.Where(static model => model.IsAvailable)
                                              .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                                              .ThenBy(static model => model.ModelName, StringComparer.Ordinal))
        {
            if (contextTokens is { } requested
                && (descriptor.MaxContextTokens is not { } maximum || maximum < requested))
            {
                continue;
            }

            try
            {
                var facts = await ReadEligibleModelFactsAsync(descriptor.ModelName, cancellationToken);
                if (facts.ModelContentFingerprint is not { } fingerprint)
                {
                    // A legacy entry acquired before the registry recorded an aggregate identity has to be verified to
                    // learn one. That model alone pays the hashing cost the whole catalog used to pay.
                    await using var lease = await AcquireEligibleModelAsync(descriptor.ModelName, cancellationToken);
                    eligible.Add(new BenchmarkEligibleModel
                    {
                        ModelName = lease.Snapshot.ModelName,
                        MaxContextTokens = descriptor.MaxContextTokens,
                        EffectiveContextTokens = null,
                        Origin = lease.Snapshot.Origin,
                        ModelContentFingerprint = lease.Snapshot.ModelContentFingerprint,
                        SupportsTools = descriptor.IsToolCapable
                    });
                    continue;
                }

                eligible.Add(new BenchmarkEligibleModel
                {
                    ModelName = facts.ModelName,
                    MaxContextTokens = descriptor.MaxContextTokens,
                    EffectiveContextTokens = null,
                    Origin = facts.Origin,
                    ModelContentFingerprint = fingerprint,
                    SupportsTools = descriptor.IsToolCapable
                });
            }
            catch (BenchmarkEligibilityException)
            {
                // Non-chat and non-llama.cpp entries are not candidates; a chat model with an mmproj projector IS one — the benchmark is text-only either way. An unreadable entry arrives here
                // too (ReadEligibleModelFactsAsync), so one broken entry costs only its row. Content is NOT verified: the listing believes the registry, and the freeze catches a mismatch.
            }
            catch (BenchmarkNotFoundException)
            {
                // The installed catalog raced a delete. A later request observes the stable post-delete state.
            }
        }

        return eligible;
    }

    /// <summary>The catalog's eligibility read: registry-recorded facts, no content hashing.</summary>
    /// <remarks>
    ///     The listing calls this once per installed model, so verifying here re-hashed the entire models directory on
    ///     every request (measured: 6m34s over 174 GB, page-cache warm). Full verification belongs to
    ///     <see cref="BenchmarkRunFreezeService" />, which pays it for the one model a run actually freezes.
    /// </remarks>
    private async Task<InstalledModelFacts> ReadEligibleModelFactsAsync(string modelName, CancellationToken cancellationToken)
    {
        InstalledModelFacts? facts;
        try
        {
            facts = await _installedModels.ReadFactsAsync(modelName, cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.", exception)
            {
                Source = exception.Source
            };
        }
        catch (InstalledGgufSnapshotException exception)
        {
            _logger.LogWarning(exception, "Benchmark catalog: installed model {ModelName} could not be read and is excluded.", modelName);
            throw new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry.", exception);
        }

        if (facts is null)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.");
        }

        BenchmarkModelEligibility.Validate(facts.ProviderName, facts.Role, "benchmark");
        return facts;
    }

    private async Task<IBenchmarkInstalledModelLease> AcquireEligibleModelAsync(string modelName, CancellationToken cancellationToken)
    {
        IBenchmarkInstalledModelLease lease;
        try
        {
            lease = await _installedModels.AcquireAsync(modelName, cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.", exception)
            {
                Source = exception.Source
            };
        }
        catch (InstalledGgufSnapshotException exception)
        {
            // One unverifiable installed model must never fail the whole catalog: the list path isolates a BenchmarkEligibilityException per entry,
            // and the single-model path turns this into the typed 422 the endpoint already declares instead of a bare 500. The store's own reason is logged, never returned.
            _logger.LogWarning(exception, "Benchmark catalog: installed model {ModelName} could not be verified and is excluded.", modelName);
            throw new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry.", exception);
        }

        try
        {
            BenchmarkModelEligibility.Validate(lease.Snapshot, "benchmark");
            return lease;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}
