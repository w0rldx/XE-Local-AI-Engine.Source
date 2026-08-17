namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
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
        capabilities.ResolveAsync("model", Arg.Any<CancellationToken>()).Returns(new ModelCapabilitySnapshot(false, true, false));
        var models = Substitute.For<IGgufModelStore>();
        var service = new BenchmarkCatalogService(definitions,
            resolver,
            capabilities,
            new BenchmarkEligibilityPolicy(),
            models,
            new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>
            {
                ["model"] = CreateSnapshot("model", null)
            }),
            NullLogger<BenchmarkCatalogService>.Instance);

        var result = await service.ListEligibleAgentsAsync("model").ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Count);
        var agent = result[0];
        AssertEx.Equal(eligibleId, agent.Id);
        AssertEx.Equal("Eligible", agent.Name);
    }

    /// <summary>
    ///     A chat GGUF that carries the optional <c>mmproj</c> projector companion the HF acquisition path auto-attaches
    ///     to modern text models stays benchmark-eligible; role and provider remain the only disqualifiers.
    /// </summary>
    [Test]
    public async Task ListModels_KeepsProjectorBearingChatModelAndStillDropsNonChatAndNonLlamaCpp()
    {
        var models = Substitute.For<IGgufModelStore>();
        models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            Descriptor("chat-with-projector", 8192),
            Descriptor("embedding", 8192),
            Descriptor("ollama-chat", 8192)
        ]);
        var leases = new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["chat-with-projector"] = CreateSnapshot("chat-with-projector", LocalModelOrigin.Imported, withProjector: true),
            ["embedding"] = CreateSnapshot("embedding", LocalModelOrigin.Imported, role: GgufRole.Embedding),
            ["ollama-chat"] = CreateSnapshot("ollama-chat", LocalModelOrigin.Imported, providerName: "ollama")
        });
        var service = CreateService(models, leases);

        var result = await service.ListEligibleModelsAsync(4096).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal("chat-with-projector", result[0].ModelName);
    }

    /// <summary>
    ///     One installed model whose snapshot fails verification (a legacy registry entry, a member changed on disk)
    ///     costs its own catalog row only. Before this, the unhandled <see cref="InstalledGgufSnapshotException" />
    ///     escaped the loop and turned the whole eligible-models endpoint into a 500.
    /// </summary>
    [Test]
    public async Task ListModels_ExcludesOnlyTheModelWhoseSnapshotFailsVerification()
    {
        var models = Substitute.For<IGgufModelStore>();
        models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            Descriptor("broken", 8192),
            Descriptor("healthy", 8192)
        ]);
        var leases = new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["healthy"] = CreateSnapshot("healthy", LocalModelOrigin.Imported)
        })
        {
            Unverifiable =
            {
                "broken"
            }
        };
        var service = CreateService(models, leases);

        var result = await service.ListEligibleModelsAsync(4096).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal("healthy", result[0].ModelName);
    }

    /// <summary>The requested model being unverifiable stays a typed eligibility refusal, not an unhandled fault.</summary>
    [Test]
    public async Task ListAgents_WhenRequestedModelFailsVerification_ThrowsEligibility()
    {
        var models = Substitute.For<IGgufModelStore>();
        var leases = new LeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase))
        {
            Unverifiable =
            {
                "broken"
            }
        };
        var service = CreateService(models, leases);

        _ = await AssertEx.ThrowsAsync<BenchmarkEligibilityException>(() => service.ListEligibleAgentsAsync("broken")).ConfigureAwait(false);
    }

    /// <summary>
    ///     The listing reads REGISTRY-RECORDED facts and hashes nothing: verifying costs one full re-hash of every
    ///     member of every installed model per request (measured at 6m34s over a 174 GB models directory, page cache
    ///     warm). A model whose registry entry predates the recorded aggregate identity is the only one that still pays
    ///     for verification, and only for itself. <see cref="FactsProvider.Verified" /> is the proof.
    /// </summary>
    [Test]
    public async Task ListModels_ListsFromRecordedFactsAndVerifiesOnlyEntriesWithoutARecordedIdentity()
    {
        var recorded = "v1:" + new string('a', 64);
        var models = Substitute.For<IGgufModelStore>();
        models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            Descriptor("chat", 8192),
            Descriptor("embedding", 8192),
            Descriptor("legacy", 8192)
        ]);
        var provider = new FactsProvider(new Dictionary<string, InstalledModelFacts>(StringComparer.OrdinalIgnoreCase)
            {
                ["chat"] = new("chat", "llamacpp", GgufRole.Chat, LocalModelOrigin.HuggingFace, recorded),
                ["embedding"] = new("embedding", "llamacpp", GgufRole.Embedding, LocalModelOrigin.HuggingFace, recorded),
                ["legacy"] = new("legacy", "llamacpp", GgufRole.Chat, null, null)
            },
            new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["legacy"] = CreateSnapshot("legacy", LocalModelOrigin.Imported)
            });
        var service = CreateService(models, provider);

        var result = await service.ListEligibleModelsAsync(4096).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.Equal("chat", result[0].ModelName);
        AssertEx.Equal(recorded, result[0].ModelContentFingerprint);
        AssertEx.Equal(LocalModelOrigin.HuggingFace, result[0].Origin);
        AssertEx.Equal("legacy", result[1].ModelName);
        AssertEx.True(provider.Verified.SequenceEqual(["legacy"], StringComparer.Ordinal),
            "Only the entry without a recorded aggregate identity may be verified.");
    }

    private static BenchmarkCatalogService CreateService(IGgufModelStore models, IBenchmarkInstalledModelLeaseProvider leases)
    {
        var definitions = Substitute.For<IAgentDefinitionStore>();
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        return new BenchmarkCatalogService(definitions,
            resolver,
            capabilities,
            new BenchmarkEligibilityPolicy(),
            models,
            leases,
            NullLogger<BenchmarkCatalogService>.Instance);
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

    private static InstalledModelSnapshot CreateSnapshot(string name,
        LocalModelOrigin? origin,
        bool withProjector = false,
        GgufRole role = GgufRole.Chat,
        string providerName = "llamacpp")
    {
        var identity = "v1:" + new string('a', 64);
        List<InstalledModelPhysicalMember> members =
        [
            new(name, InstalledModelPhysicalMemberRole.Weight, 12, new string('b', 64),
                "sha256:" + new string('b', 64) + ":12", [name], true, null)
        ];
        if (withProjector)
        {
            members.Add(new InstalledModelPhysicalMember(name + "-mmproj", InstalledModelPhysicalMemberRole.Projector, 6,
                new string('c', 64), "sha256:" + new string('c', 64) + ":6", [name + "-mmproj"], true, null));
        }

        return new InstalledModelSnapshot(name,
            identity,
            [],
            identity,
            members,
            identity,
            origin,
            providerName,
            "map-revision",
            "repo/model",
            "revision",
            "Q4_K_M",
            role,
            identity);
    }

    private sealed class LeaseProvider(IReadOnlyDictionary<string, InstalledModelSnapshot> snapshots) : IBenchmarkInstalledModelLeaseProvider
    {
        public List<string> Acquired { get; } = [];

        /// <summary>Model names whose snapshot acquisition fails verification, as a legacy registry entry does.</summary>
        public HashSet<string> Unverifiable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
        {
            Acquired.Add(modelName);
            return Unverifiable.Contains(modelName)
                ? throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model weight no longer matches its registry value.")
                : Task.FromResult<IBenchmarkInstalledModelLease>(new Lease(snapshots[modelName]));
        }
    }

    /// <summary>
    ///     Serves recorded facts cheaply and fails any verification the caller did not have to do — the model must be in
    ///     <paramref name="snapshots" /> to be verifiable at all.
    /// </summary>
    private sealed class FactsProvider(
        IReadOnlyDictionary<string, InstalledModelFacts> facts,
        IReadOnlyDictionary<string, InstalledModelSnapshot> snapshots) : IBenchmarkInstalledModelLeaseProvider
    {
        public List<string> Verified { get; } = [];

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
        {
            Verified.Add(modelName);
            return Task.FromResult<IBenchmarkInstalledModelLease>(new Lease(snapshots[modelName]));
        }

        public Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<InstalledModelFacts?>(facts.GetValueOrDefault(modelName));
    }

    private sealed class Lease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
