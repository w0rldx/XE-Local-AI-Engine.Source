namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CustomTools;

internal sealed class AgentDefinitionResolver : IAgentDefinitionResolver
{
    // Rides inside the fence with every imported skill payload so the boundary the model sees states WHY the enclosed
    // bytes are fenced, not merely that they are.
    private const string ImportedSkillTrustStatement = "third-party skill, not validated by this node";

    private readonly IAgentSkillStore _agentSkillStore;
    private readonly ICustomToolStore _customToolStore;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly ILogger<AgentDefinitionResolver> _logger;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly PlaybookRetrievalOptions _retrievalOptions;
    private readonly IPlaybookRetrievalRanker _retrievalRanker;
    private readonly IAgentDefinitionStore _store;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;

    public AgentDefinitionResolver(IAgentDefinitionStore store,
        IPlaybookActionStore playbookActionStore,
        IAgentSkillStore agentSkillStore,
        ICustomToolStore customToolStore,
        ILocalToolOfferProvider localToolOfferProvider,
        IPlaybookRetrievalRanker retrievalRanker,
        IOptions<PlaybookRetrievalOptions> retrievalOptions,
        IAgentInstructionProvider instructionProvider,
        IModelCapabilityResolver modelCapabilityResolver,
        IToolApprovalPolicy toolApprovalPolicy,
        ILogger<AgentDefinitionResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _agentSkillStore = agentSkillStore ?? throw new ArgumentNullException(nameof(agentSkillStore));
        _customToolStore = customToolStore ?? throw new ArgumentNullException(nameof(customToolStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _toolApprovalPolicy = toolApprovalPolicy ?? throw new ArgumentNullException(nameof(toolApprovalPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        bool honorModelProfile = true, bool activeModelIsCloud = false, CancellationToken cancellationToken = default)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            // Unbound conversation: keep the default persona (embedded prompt, full offer, version 1).
            return null;
        }

        var definition = await _store.GetByIdAsync(definitionId, cancellationToken);
        if (definition is null)
        {
            // A binding pointing at a deleted definition degrades to the default persona rather than failing the
            // turn — matches the no-FK provenance choice on the conversation column.
            _logger.LogWarning("Agent definition {AgentDefinitionId} is bound to a conversation but no longer exists; using the default persona.", definitionId);
            return null;
        }

        return await ResolveAsync(definition, activeModelId, retrievalQuery, supportsTools, honorModelProfile, activeModelIsCloud, cancellationToken);
    }

    public async Task<ResolvedAgentRuntime?> ResolveAsync(AgentDefinitionRecord definition, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        bool honorModelProfile = true, bool activeModelIsCloud = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // The pin is normally the model the turn runs on, so it gates the offer, keeping capability gating and the
        // runtime model consistent. Suppressed, the active model wins for BOTH the gating and the returned profile.
        var pinnedModel = honorModelProfile ? definition.ModelProfile : null;
        var effectiveModel = pinnedModel ?? activeModelId;

        // Gate the knowledge tools on the EFFECTIVE model's locality, never the turn's active one, or a cloud-pinned
        // agent keeps them on a local turn. With no pin the effective model IS the active one: reuse the caller's flag.
        var effectiveModelIsCloud = pinnedModel is null
            ? activeModelIsCloud
            : (await _modelCapabilityResolver.ResolveAsync(pinnedModel, cancellationToken)).IsCloud;
        var allowedTools = await ProjectAllowedToolsAsync(definition, effectiveModel, supportsTools, effectiveModelIsCloud, cancellationToken);
        var resolvedPrompt = await ComposePromptAsync(definition, retrievalQuery, cancellationToken);
        var skills = await ResolveSkillsAsync(definition, cancellationToken);
        var customTools = await ResolveCustomToolsAsync(allowedTools, cancellationToken);

        return new ResolvedAgentRuntime(resolvedPrompt,
            allowedTools,
            pinnedModel,
            definition.ReasoningEffort,
            definition.Version,
            definition.Id,
            definition.Name,
            skills,
            definition.PlaybookEnabled,
            definition.MemoryExtractionEnabled,
            effectiveModelIsCloud,
            definition.Kind,
            customTools,
            definition.DisableToolRelevanceFilter);
    }

    /// <summary>
    ///     Projects the resolved offer's custom tools into the runtime-package metadata the session-approval memo
    ///     needs: name, version and Fixed or Parameterized.
    /// </summary>
    /// <remarks>
    ///     The store is read ONCE, and only when the offer carries a <c>custom__</c> tool, so the common path reads
    ///     nothing and answers <c>null</c>. A tool offered but gone from the store — a mid-turn delete — is omitted,
    ///     and its later approval falls back to always-prompt.
    /// </remarks>
    private async Task<IReadOnlyList<ResolvedCustomTool>?> ResolveCustomToolsAsync(IReadOnlyList<AllowedToolDto> allowedTools, CancellationToken cancellationToken)
    {
        var offeredCustomNames = allowedTools
                                 .Where(static tool => tool.Name.StartsWith(CustomToolValidation.ToolNamePrefix, StringComparison.Ordinal))
                                 .Select(static tool => tool.Name)
                                 .ToHashSet(StringComparer.Ordinal);
        if (offeredCustomNames.Count == 0)
        {
            return null;
        }

        var stored = await _customToolStore.ListAsync(cancellationToken);
        var resolved = stored
                       .Where(tool => offeredCustomNames.Contains(tool.Name))
                       .Select(static tool => new ResolvedCustomTool(tool.Name, tool.Version, tool.Mode == CustomToolMode.Fixed))
                       .ToArray();

        return resolved.Length > 0 ? resolved : null;
    }

    /// <summary>
    ///     Resolves the definition's per-agent skill picklist into the enabled, decrypted skills MAF progressive
    ///     disclosure will offer.
    /// </summary>
    /// <remarks>
    ///     An assigned id the store's Enabled fast path drops is logged BY ID ONLY, never the body or description, and
    ///     an empty picklist short-circuits with no store call. A skill whose stored Name no longer satisfies the
    ///     Agent Skills specification is dropped too, because constructing an <c>AgentInlineSkill</c> from one throws
    ///     and takes down the whole turn: degrade, log, never fabricate, as <see cref="ProjectAllowedTools" /> does.
    ///     This is the one choke point every skills consumer routes through, so <see cref="ProjectSkill" /> fences once.
    /// </remarks>
    private async Task<IReadOnlyList<ResolvedSkill>> ResolveSkillsAsync(AgentDefinitionRecord definition, CancellationToken cancellationToken)
    {
        var assignedIds = definition.AllowedSkillIds;
        if (assignedIds is null || assignedIds.Count == 0)
        {
            return [];
        }

        var enabled = await _agentSkillStore.ListEnabledByIdsAsync(assignedIds, cancellationToken);

        var resolvedIds = new HashSet<Guid>(enabled.Select(static skill => skill.Id));
        var droppedIds = assignedIds.Where(id => !resolvedIds.Contains(id)).ToArray();
        if (droppedIds.Length > 0)
        {
            _logger.LogWarning("Agent definition {AgentDefinitionId} assigns {DroppedCount} skill(s) that are missing or disabled ({DroppedSkillIds}); they were dropped.",
                definition.Id,
                droppedIds.Length,
                string.Join(", ", droppedIds));
        }

        var resolved = new List<ResolvedSkill>(enabled.Count);
        List<Guid>? unbuildableIds = null;
        foreach (var skill in enabled)
        {
            // MAAI001: scoped suppression, same rationale as AgentSkillService — the frontmatter validator is the code
            // AgentInlineSkill's constructor runs, so this predicts construction exactly.
#pragma warning disable MAAI001
            if (!AgentSkillFrontmatter.ValidateName(skill.Name, out _))
#pragma warning restore MAAI001
            {
                (unbuildableIds ??= []).Add(skill.Id);
                continue;
            }

            resolved.Add(ProjectSkill(skill));
        }

        if (unbuildableIds is not null)
        {
            // Ids only — a dropped-skill warning never carries the encrypted Description/Body, and the Name is omitted
            // too so a crafted name cannot shape a log line.
            _logger.LogWarning(
                "Agent definition {AgentDefinitionId} assigns {UnbuildableCount} skill(s) whose stored name is not a valid Agent Skills name ({UnbuildableSkillIds}); they were dropped so the agent can still be built. Rename them to restore the skill.",
                definition.Id,
                unbuildableIds.Count,
                string.Join(", ", unbuildableIds));
        }

        return resolved;
    }

    /// <summary>
    ///     Projects one stored skill onto the runtime DTO, applying the trust decision every skills consumer inherits.
    /// </summary>
    /// <remarks>
    ///     A <see cref="AgentSkillOrigin.Local" /> row passes through byte-exact, or the body hash and the runtime
    ///     config hash would move for every locally authored skill. An <see cref="AgentSkillOrigin.Imported" /> row is
    ///     third-party text, so its body AND every bundled resource go inside the untrusted-content fence: injected
    ///     verbatim as instructions, it would otherwise be the most trusted text in the context. The DETERMINISTIC
    ///     nonce is mandatory — see the <c>fenceNonceSeed</c> local for the seed and its residual.
    /// </remarks>
    private static ResolvedSkill ProjectSkill(AgentSkillRecord skill)
    {
        if (skill.Origin != AgentSkillOrigin.Imported)
        {
            return new ResolvedSkill(skill.Id,
                skill.Name,
                skill.Description,
                skill.Body,
                skill.Version,
                License: skill.License,
                Compatibility: skill.Compatibility,
                AllowedTools: skill.AllowedTools,
                Metadata: skill.Metadata,
                Resources: ProjectResources(skill, fenceNonceSeed: null));
        }

        var nonceSeed = BuildFenceNonceSeed(skill);
        return new ResolvedSkill(skill.Id,
            skill.Name,
            skill.Description,
            UntrustedContentFraming.WrapDocument(skill.Body, BuildFenceMetadata(skill), nonceSeed),
            skill.Version,
            IsImported: true,
            License: skill.License,
            Compatibility: skill.Compatibility,
            AllowedTools: skill.AllowedTools,
            Metadata: skill.Metadata,
            Resources: ProjectResources(skill, nonceSeed));
    }

    /// <summary>
    ///     Projects the skill's bundled resources, fencing each payload when <paramref name="fenceNonceSeed" /> is
    ///     supplied (the imported case). Returns <c>null</c> for a skill with no resources so the no-resource path stays
    ///     byte-identical to the pre-resource resolve.
    /// </summary>
    private static IReadOnlyList<ResolvedSkillResource>? ProjectResources(AgentSkillRecord skill, string? fenceNonceSeed)
    {
        if (skill.Resources is not { Count: > 0 } resources)
        {
            return null;
        }

        var projected = new ResolvedSkillResource[resources.Count];
        for (var index = 0; index < resources.Count; index++)
        {
            var resource = resources[index];

            // One seed per skill, but the framing HMACs the fenced content under it, so body and each resource still
            // get their own marker: one resource's model-visible closing marker cannot close another's fence.
            var content = fenceNonceSeed is null
                ? resource.Content
                : UntrustedContentFraming.WrapDocument(resource.Content, BuildFenceMetadata(skill, resource), fenceNonceSeed);
            projected[index] = new ResolvedSkillResource(resource.Name, resource.Description, resource.MediaType, content);
        }

        return projected;
    }

    /// <summary>
    ///     The labels that ride INSIDE an imported skill's fence.
    /// </summary>
    /// <remarks>
    ///     Every attacker-controlled field the fence can carry — source, skill name, resource name and media type —
    ///     goes in here rather than around the boundary, and the trust label states plainly what the enclosed bytes
    ///     are. Blank values are dropped by the framing, so the body's block is the resource block minus its two
    ///     resource labels.
    /// </remarks>
    private static KeyValuePair<string, string?>[] BuildFenceMetadata(AgentSkillRecord skill, AgentSkillResourceRecord? resource = null)
    {
        return
        [
            new("source", skill.SourceUri),
            new("skill", skill.Name),
            new("resource", resource?.Name),
            new("media-type", resource?.MediaType),
            new("trust", ImportedSkillTrustStatement)
        ];
    }

    private static string BuildFenceNonceSeed(AgentSkillRecord skill)
    {
        return string.Create(CultureInfo.InvariantCulture, $"agent-skill:{skill.Id:N}:{skill.Version}");
    }

    /// <summary>
    ///     Composes the definition's final resolved prompt: the versioned base instruction scaffold, a blank line,
    ///     then the persona prompt from <see cref="ComposePersonaPromptAsync" />.
    /// </summary>
    /// <remarks>
    ///     A definition with <see cref="AgentDefinitionRecord.DisableBaseScaffold" /> set, or the defensive case of a
    ///     blank scaffold resource, skips the prepend entirely, so its resolved prompt and config hash are
    ///     byte-identical to the persona-only path.
    /// </remarks>
    private async Task<string> ComposePromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        var personaPrompt = await ComposePersonaPromptAsync(definition, retrievalQuery, cancellationToken);
        return definition.DisableBaseScaffold
            ? personaPrompt
            : BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), personaPrompt);
    }

    /// <summary>
    ///     Folds the definition's enabled playbook actions into its prompt when the playbook is enabled.
    /// </summary>
    /// <remarks>
    ///     Disabled, the query is skipped entirely and the base Instructions flow through unchanged, keeping prompt
    ///     and config hash byte-identical. Above the retrieval threshold with a non-blank
    ///     <paramref name="retrievalQuery" /> only the top-k most relevant actions are injected; at or below it, or
    ///     with a blank query, the full static prepend keeps the prompt byte-identical as well.
    /// </remarks>
    private async Task<string> ComposePersonaPromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        if (!definition.PlaybookEnabled)
        {
            return definition.Instructions;
        }

        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(definition.Id, cancellationToken);
        var selected = await PlaybookRetrievalSelector.SelectAsync(_retrievalRanker,
            retrievalQuery,
            enabled,
            _retrievalOptions.RetrievalThreshold,
            _retrievalOptions.TopK,
            cancellationToken,
            _retrievalOptions.MaxInjectedMemoryTokens,
            _retrievalOptions.MaxInjectedFailureMemoryTokens,
            _logger);
        return PlaybookPromptComposer.Compose(definition.Instructions, selected);
    }

    private async Task<IReadOnlyList<AllowedToolDto>> ProjectAllowedToolsAsync(AgentDefinitionRecord definition, string? effectiveModelId, bool supportsTools, bool effectiveModelIsCloud,
        CancellationToken cancellationToken)
    {
        // A model that does not advertise "tools" cannot drive ANY call, so withhold the whole offer before per-tool
        // gating. This is the capability gate; ToolCapableModels remains the extra gate for high-risk tools.
        if (!supportsTools)
        {
            return [];
        }

        // SECURITY INVARIANT: the seeded Default Assistant is the ONLY definition granted the full capability-gated
        // offer; every other stays intersected. Its forge-proof provenance is what an operator row cannot claim.
        if (definition.Source == AgentDefinitionSource.Seeded
            && string.Equals(definition.SeedSlug, AgentDefaults.DefaultAgentSeedSlug, StringComparison.Ordinal))
        {
            // The node-default approval policy still applies, tighten-only, so no node-wide policy is bypassable by
            // mode-off chat. Per-agent ToolApprovals are NOT applied: this path reproduces plain chat, which has none.
            var wholeOffer = await _localToolOfferProvider.GetOfferedToolsAsync(effectiveModelId, effectiveModelIsCloud, cancellationToken);
            AllowedToolDto[] composedWholeOffer =
            [
                .. wholeOffer.Select(tool => tool with
                {
                    RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                })
            ];

            // The Default Assistant ships with ZERO AllowedToolNames, so ask_user must not depend on the allowed set.
            // A no-op today, kept so the rule is enforced AT the seam and no offer-side gate can silently drop it.
            return AskUserToolOffer.EnsureOffered(composedWholeOffer, wholeOffer, _toolApprovalPolicy);
        }

        // Start from the PROFILE pool (the whole offer plus opt-in-only spawn_subagent), which is what lets a profile
        // listing it resolve while mode-off never does, then intersect. A named tool absent from the pool is dropped.
        var offered = await _localToolOfferProvider.GetOfferedToolsForProfileAsync(effectiveModelId, effectiveModelIsCloud, cancellationToken);
        var allowedNames = new HashSet<string>(definition.AllowedToolNames, StringComparer.Ordinal);

        var projected = offered
                        .Where(tool => allowedNames.Contains(tool.Name))
                        .Select(tool => tool with
                        {
                            // TIGHTEN-ONLY three-tier compose: node policy first, then a per-agent override that can
                            // only ADD approval — a per-agent false is a NO-OP. The registry floor still rules execution.
                            RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                                               || (definition.ToolApprovals.TryGetValue(tool.Name, out var perAgentApproval) && perAgentApproval)
                        })
                        .ToArray();

        var droppedNames = allowedNames
                           .Where(name => !offered.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
                           .ToArray();
        if (droppedNames.Length > 0)
        {
            _logger.LogWarning("Agent definition {AgentDefinitionId} names {DroppedCount} tool(s) not in the current offer ({DroppedTools}); they were dropped.",
                definition.Id,
                droppedNames.Length,
                string.Join(", ", droppedNames));
        }

        // ask_user is unioned in AFTER the intersection, whatever AllowedToolNames says: asking the operator is a
        // property of an interactive turn. Safe to widen only because it is approval-gated and side-effect-free.
        return AskUserToolOffer.EnsureOffered(projected, offered, _toolApprovalPolicy);
    }
}
