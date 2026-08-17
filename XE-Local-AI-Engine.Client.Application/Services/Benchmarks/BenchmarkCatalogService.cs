namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public interface IBenchmarkCatalogService
{
    Task<IReadOnlyList<BenchmarkEligibleAgent>> ListEligibleAgentsAsync(string modelName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BenchmarkEligibleModel>> ListEligibleModelsAsync(int? contextTokens, CancellationToken cancellationToken = default);
}

internal sealed class BenchmarkCatalogService(
    IAgentDefinitionStore agentDefinitions,
    IAgentDefinitionResolver agentResolver,
    IModelCapabilityResolver modelCapabilities,
    IBenchmarkEligibilityPolicy eligibilityPolicy,
    IGgufModelStore ggufModels,
    IBenchmarkInstalledModelLeaseProvider installedModels,
    ILogger<BenchmarkCatalogService> logger) : IBenchmarkCatalogService
{
    private readonly IAgentDefinitionStore _agentDefinitions = agentDefinitions ?? throw new ArgumentNullException(nameof(agentDefinitions));
    private readonly IAgentDefinitionResolver _agentResolver = agentResolver ?? throw new ArgumentNullException(nameof(agentResolver));
    private readonly IModelCapabilityResolver _modelCapabilities = modelCapabilities ?? throw new ArgumentNullException(nameof(modelCapabilities));
    private readonly IBenchmarkEligibilityPolicy _eligibilityPolicy = eligibilityPolicy ?? throw new ArgumentNullException(nameof(eligibilityPolicy));
    private readonly IGgufModelStore _ggufModels = ggufModels ?? throw new ArgumentNullException(nameof(ggufModels));
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));
    private readonly ILogger<BenchmarkCatalogService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<IReadOnlyList<BenchmarkEligibleAgent>> ListEligibleAgentsAsync(string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await using var modelLease = await AcquireEligibleModelAsync(modelName.Trim(), cancellationToken).ConfigureAwait(false);
        var (_, supportsTools, isCloud) = await _modelCapabilities.ResolveAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (isCloud)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.");
        }

        var definitions = await _agentDefinitions.ListAsync(cancellationToken).ConfigureAwait(false);
        var eligible = new List<BenchmarkEligibleAgent>();
        foreach (var definition in definitions.Where(static definition => definition.Kind == AgentDefinitionKind.Single))
        {
            var runtime = await _agentResolver.ResolveAsync(definition.Id,
                                                  modelName,
                                                  retrievalQuery: string.Empty,
                                                  supportsTools,
                                                  honorModelProfile: false,
                                                  activeModelIsCloud: false,
                                                  cancellationToken)
                                              .ConfigureAwait(false);
            if (runtime is null)
            {
                continue;
            }

            try
            {
                _ = _eligibilityPolicy.Apply(runtime);
                eligible.Add(new BenchmarkEligibleAgent(definition.Id, definition.Name, definition.Version));
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

        var descriptors = await _ggufModels.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
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
                await using var lease = await AcquireEligibleModelAsync(descriptor.ModelName, cancellationToken).ConfigureAwait(false);
                eligible.Add(new BenchmarkEligibleModel(lease.Snapshot.ModelName,
                    descriptor.MaxContextTokens,
                    EffectiveContextTokens: null,
                    lease.Snapshot.Origin,
                    lease.Snapshot.ModelContentFingerprint,
                    descriptor.IsToolCapable));
            }
            catch (BenchmarkEligibilityException)
            {
                // Non-chat or non-llama.cpp entries are not benchmark candidates. A chat model that carries an
                // optional mmproj projector companion IS a candidate — the benchmark is text-only either way. An
                // installed model that fails snapshot verification arrives here too (see AcquireEligibleModelAsync)
                // so a single broken registry entry costs its own row, not the whole catalog.
            }
            catch (BenchmarkNotFoundException)
            {
                // The installed catalog raced a delete. A later request observes the stable post-delete state.
            }
        }

        return eligible;
    }

    private async Task<IBenchmarkInstalledModelLease> AcquireEligibleModelAsync(string modelName, CancellationToken cancellationToken)
    {
        IBenchmarkInstalledModelLease lease;
        try
        {
            lease = await _installedModels.AcquireAsync(modelName, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new BenchmarkNotFoundException("Benchmark model was not found.")
            {
                Source = exception.Source
            };
        }
        catch (InstalledGgufSnapshotException exception)
        {
            // One unverifiable installed model must never fail the whole catalog: the list path already isolates a
            // BenchmarkEligibilityException per entry, and the single-model path turns this into the typed 422 the
            // endpoint already declares instead of a bare 500. The store's own reason is logged, never returned.
            _logger.LogWarning(exception, "Benchmark catalog: installed model {ModelName} could not be verified and is excluded.", modelName);
            throw new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry.");
        }

        try
        {
            BenchmarkModelEligibility.Validate(lease.Snapshot, "benchmark");
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
