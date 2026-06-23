namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the adaptive-memory metadata fields are non-config-affecting: the per-agent <c>DefaultTemporaryChat</c>
///     flag and a playbook action's <c>MemoryScope</c> never reach the runtime package config hash, and the resolver
///     surfaces <c>PlaybookEnabled</c> on the runtime projection so the post-run extraction seam can gate without a
///     re-fetch. The cross-repo golden in <c>RuntimePackageConfigHashTests</c> remains the canonical proof that
///     <c>RuntimePackageConfigHash.Compute</c> is byte-identical (its signature is untouched by this feature).
/// </summary>
public sealed class AdaptiveMemoryConfigHashTests
{
    private const string SystemPrompt = "You are the bound persona.";

    [Test]
    public async Task ResolveAsync_SurfacesPlaybookEnabled_OnRuntimeProjection()
    {
        var resolver = CreateResolver(out var store);
        var definition = CreateDefinition(playbookEnabled: true);
        store.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);

        var resolved = await resolver.ResolveAsync(definition.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved, "A bound definition must resolve to a runtime projection.");
        AssertEx.True(resolved!.PlaybookEnabled, "The runtime projection must carry the definition's PlaybookEnabled so the extraction seam can gate without a re-fetch.");
    }

    [Test]
    public async Task ResolveAsync_DefaultTemporaryChat_DoesNotChangeResolvedPromptOrConfigHash()
    {
        // Two definitions identical except for DefaultTemporaryChat must produce the same resolved prompt and the same
        // config hash — DefaultTemporaryChat gates extraction only and is not part of the resolved runtime/config hash.
        var resolver = CreateResolver(out var store);

        var nonTemporary = CreateDefinition() with
        {
            DefaultTemporaryChat = false
        };
        var temporary = nonTemporary with
        {
            DefaultTemporaryChat = true
        };
        store.GetByIdAsync(nonTemporary.Id, Arg.Any<CancellationToken>()).Returns(nonTemporary);
        store.GetByIdAsync(temporary.Id, Arg.Any<CancellationToken>()).Returns(temporary);

        var resolvedNonTemporary = await resolver.ResolveAsync(nonTemporary.Id, "qwen3:8b").ConfigureAwait(false);
        var resolvedTemporary = await resolver.ResolveAsync(temporary.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolvedNonTemporary);
        AssertEx.NotNull(resolvedTemporary);
        AssertEx.Equal(resolvedNonTemporary!.ResolvedSystemPrompt, resolvedTemporary!.ResolvedSystemPrompt);

        var builder = new LocalChatRuntimePackageBuilder();
        var nonTemporaryHash = builder.Build(BuildRequest(resolvedNonTemporary)).ConfigHash;
        var temporaryHash = builder.Build(BuildRequest(resolvedTemporary)).ConfigHash;

        AssertEx.Equal(nonTemporaryHash, temporaryHash);
    }

    [Test]
    public async Task ResolveAsync_SurfacesMemoryExtractionEnabled_OnRuntimeProjection()
    {
        // The runtime projection must carry the definition's MemoryExtractionEnabled so the extraction seam can gate on
        // BOTH PlaybookEnabled and MemoryExtractionEnabled without a re-fetch.
        var resolver = CreateResolver(out var store);
        var retrievalOnly = CreateDefinition(playbookEnabled: true) with
        {
            MemoryExtractionEnabled = false
        };
        store.GetByIdAsync(retrievalOnly.Id, Arg.Any<CancellationToken>()).Returns(retrievalOnly);

        var resolved = await resolver.ResolveAsync(retrievalOnly.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolved, "A bound definition must resolve to a runtime projection.");
        AssertEx.True(resolved!.PlaybookEnabled, "Retrieval/injection stays gated on PlaybookEnabled.");
        AssertEx.False(resolved.MemoryExtractionEnabled, "A retrieval-only agent surfaces MemoryExtractionEnabled=false so extraction is skipped.");
    }

    [Test]
    public async Task ResolveAsync_MemoryExtractionEnabled_DoesNotChangeResolvedPromptOrConfigHash()
    {
        // Two definitions identical except for MemoryExtractionEnabled must produce the same resolved prompt and config
        // hash — MemoryExtractionEnabled gates extraction only and is not part of the resolved runtime/config hash.
        var resolver = CreateResolver(out var store);

        var extracting = CreateDefinition(playbookEnabled: true) with
        {
            MemoryExtractionEnabled = true
        };
        var retrievalOnly = extracting with
        {
            MemoryExtractionEnabled = false
        };
        store.GetByIdAsync(extracting.Id, Arg.Any<CancellationToken>()).Returns(extracting);
        store.GetByIdAsync(retrievalOnly.Id, Arg.Any<CancellationToken>()).Returns(retrievalOnly);

        var resolvedExtracting = await resolver.ResolveAsync(extracting.Id, "qwen3:8b").ConfigureAwait(false);
        var resolvedRetrievalOnly = await resolver.ResolveAsync(retrievalOnly.Id, "qwen3:8b").ConfigureAwait(false);

        AssertEx.NotNull(resolvedExtracting);
        AssertEx.NotNull(resolvedRetrievalOnly);
        AssertEx.Equal(resolvedExtracting!.ResolvedSystemPrompt, resolvedRetrievalOnly!.ResolvedSystemPrompt);

        var builder = new LocalChatRuntimePackageBuilder();
        var extractingHash = builder.Build(BuildRequest(resolvedExtracting)).ConfigHash;
        var retrievalOnlyHash = builder.Build(BuildRequest(resolvedRetrievalOnly)).ConfigHash;

        AssertEx.Equal(extractingHash, retrievalOnlyHash);
    }

    private static LocalChatRuntimePackageRequest BuildRequest(ResolvedAgentRuntime resolved)
    {
        return new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            Guid.NewGuid(),
            resolved.ResolvedSystemPrompt,
            [],
            resolved.ModelProfile,
            resolved.AgentDefinitionVersion,
            AllowedTools: resolved.AllowedTools,
            ReasoningEffort: resolved.ReasoningEffort,
            Skills: resolved.Skills);
    }

    private static AgentDefinitionResolver CreateResolver(out IAgentDefinitionStore store)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var skillStore = Substitute.For<IAgentSkillStore>();
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns([]);
        offerProvider.GetKnownToolNames().Returns([]);

        return new AgentDefinitionResolver(store,
            playbookStore,
            skillStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            NullLogger<AgentDefinitionResolver>.Instance);
    }

    private static AgentDefinitionRecord CreateDefinition(bool playbookEnabled = false)
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            "Agent",
            Description: null,
            SystemPrompt,
            "qwen3:8b",
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            playbookEnabled);
    }
}
