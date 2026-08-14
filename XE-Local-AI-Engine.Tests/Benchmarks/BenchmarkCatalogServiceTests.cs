namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkCatalogServiceTests
{
    [Test]
    public async Task ListModels_FiltersByRequestedContextAndUsesVerifiedAggregateIdentity()
    {
        var models = Substitute.For<IGgufModelStore>();
        models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            Descriptor("small", 2048),
            Descriptor("large", 8192)
        ]);
        var leases = new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["large"] = CreateSnapshot("large", LocalModelOrigin.Imported)
        });
        var service = CreateService(models, leases);

        var result = await service.ListEligibleModelsAsync(4096).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Count);
        var model = result[0];
        AssertEx.Equal("large", model.ModelName);
        AssertEx.Equal("v1:" + new string('a', 64), model.ModelContentFingerprint);
        AssertEx.Equal(LocalModelOrigin.Imported, model.Origin);
        AssertEx.Equal(expected: 1, leases.Acquired.Count);
    }

    [Test]
    public async Task ListAgents_ReturnsOnlyResolvedEligibleSingleDefinitions()
    {
        var eligibleId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var definitions = Substitute.For<IAgentDefinitionStore>();
        definitions.ListAsync(Arg.Any<CancellationToken>()).Returns([
            Definition(eligibleId, "Eligible", AgentDefinitionKind.Single),
            Definition(missingId, "Missing", AgentDefinitionKind.Single),
            Definition(Guid.NewGuid(), "Orchestrator", AgentDefinitionKind.Orchestrator)
        ]);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(eligibleId, "model", string.Empty, true, false, false, Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("prompt", [], null, null, 7, eligibleId, "Eligible"));
        resolver.ResolveAsync(missingId, "model", string.Empty, true, false, false, Arg.Any<CancellationToken>())
                .Returns((ResolvedAgentRuntime?)null);
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        capabilities.ResolveAsync("model", Arg.Any<CancellationToken>()).Returns((false, true, false));
        var models = Substitute.For<IGgufModelStore>();
        var service = new BenchmarkCatalogService(definitions,
            resolver,
            capabilities,
            new BenchmarkEligibilityPolicy(),
            models,
            new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>
            {
                ["model"] = CreateSnapshot("model", null)
            }));

        var result = await service.ListEligibleAgentsAsync("model").ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Count);
        var agent = result[0];
        AssertEx.Equal(eligibleId, agent.Id);
        AssertEx.Equal("Eligible", agent.Name);
    }

    private static BenchmarkCatalogService CreateService(IGgufModelStore models, IBenchmarkInstalledModelLeaseProvider leases)
    {
        var definitions = Substitute.For<IAgentDefinitionStore>();
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        return new BenchmarkCatalogService(definitions, resolver, capabilities, new BenchmarkEligibilityPolicy(), models, leases);
    }

    private static LocalModelDescriptor Descriptor(string name, int context) =>
        new()
        {
            ModelName = name,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 12,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = context,
            IsToolCapable = true
        };

    private static AgentDefinitionRecord Definition(Guid id, string name, AgentDefinitionKind kind) =>
        new(id, name, null, "instructions", null, null, kind, [], new Dictionary<string, bool>(), null, 7, 1, 1);

    private static InstalledModelSnapshot CreateSnapshot(string name, LocalModelOrigin? origin)
    {
        var identity = "v1:" + new string('a', 64);
        return new InstalledModelSnapshot(name,
            identity,
            [],
            identity,
            [
                new InstalledModelPhysicalMember(name, InstalledModelPhysicalMemberRole.Weight, 12, new string('b', 64),
                    "sha256:" + new string('b', 64) + ":12", [name], true, null)
            ],
            identity,
            origin,
            "llamacpp",
            "map-revision",
            "repo/model",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            identity);
    }

    private sealed class LeaseProvider(IReadOnlyDictionary<string, InstalledModelSnapshot> snapshots) : IBenchmarkInstalledModelLeaseProvider
    {
        public List<string> Acquired { get; } = [];

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
        {
            Acquired.Add(modelName);
            return Task.FromResult<IBenchmarkInstalledModelLease>(new Lease(snapshots[modelName]));
        }
    }

    private sealed class Lease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
