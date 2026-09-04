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

        var definition = await _store.GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            // A binding pointing at a deleted definition degrades to the default persona rather than failing the
            // turn — matches the no-FK provenance choice on the conversation column.
            _logger.LogWarning("Agent definition {AgentDefinitionId} is bound to a conversation but no longer exists; using the default persona.", definitionId);
            return null;
        }

        // The definition's pinned ModelProfile (when set) is normally the model the turn actually runs on, so gate the
        // tool offer by it — not the caller's active model — to keep capability gating and the runtime model consistent.
        // When the user explicitly picked a concrete model in the chat dropdown the caller passes honorModelProfile=false:
        // the pin is suppressed entirely so the active model wins for BOTH tool gating AND the returned ModelProfile
        // (null), letting the caller's `resolved?.ModelProfile ?? activeModel` yield the user's pick. When the definition
        // pins no profile (or the pin is suppressed) the turn keeps the caller's active model.
        var pinnedModel = honorModelProfile ? definition.ModelProfile : null;
        var effectiveModel = pinnedModel ?? activeModelId;

        // Gate the knowledge tools on the EFFECTIVE model's provider locality, not the turn's active model. When the
        // definition pins a model (including a spawned sub-agent, whose child model IS the pin) the offer keys on that
        // pinned model, so its locality must too — otherwise a cloud-pinned agent on a local-active turn would keep the
        // knowledge tools. The pin is classified through the shared capability resolver (one cache-first lookup); with no
        // pin the effective model IS the active model, so reuse the flag the caller already resolved (no extra lookup).
        var effectiveModelIsCloud = pinnedModel is null
            ? activeModelIsCloud
            : (await _modelCapabilityResolver.ResolveAsync(pinnedModel, cancellationToken).ConfigureAwait(false)).IsCloud;
        var allowedTools = await ProjectAllowedToolsAsync(definition, effectiveModel, supportsTools, effectiveModelIsCloud, cancellationToken).ConfigureAwait(false);
        var resolvedPrompt = await ComposePromptAsync(definition, retrievalQuery, cancellationToken).ConfigureAwait(false);
        var skills = await ResolveSkillsAsync(definition, cancellationToken).ConfigureAwait(false);
        var customTools = await ResolveCustomToolsAsync(allowedTools, cancellationToken).ConfigureAwait(false);

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
    ///     Projects the resolved offer's custom tools into the runtime-package metadata the session-approval memo needs
    ///     (name + version + Fixed/Parameterized). Reads the store ONCE, and only when the offer actually carries a
    ///     <c>custom__</c> tool — the common no-custom-tool path does no store read and returns <c>null</c> so the package
    ///     stays byte-identical to before this feature. A tool offered but no longer in the store (a mid-turn delete) is
    ///     simply omitted; its later approval falls back to always-prompt.
    /// </summary>
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

        var stored = await _customToolStore.ListAsync(cancellationToken).ConfigureAwait(false);
        var resolved = stored
                       .Where(tool => offeredCustomNames.Contains(tool.Name))
                       .Select(static tool => new ResolvedCustomTool(tool.Name, tool.Version, tool.Mode == CustomToolMode.Fixed))
                       .ToArray();

        return resolved.Length > 0 ? resolved : null;
    }

    /// <summary>
    ///     Resolves the definition's per-agent skill picklist into the enabled, decrypted skills MAF progressive
    ///     disclosure will offer. The store's fast-path filters to Enabled==true and omits missing ids; any assigned id
    ///     absent from the result (the skill was deleted or disabled) is dropped and logged by id only — never the body
    ///     or description (privacy: dropped-skill warnings carry no encrypted content). An empty/null picklist short-
    ///     circuits with no store call so the no-skills path stays byte-identical to the pre-skills resolve. Each
    ///     surviving row is projected by <see cref="ProjectSkill" />, which is where an imported skill's content is
    ///     fenced — this method is the one place every skills consumer routes through, so the trust decision is made
    ///     once here rather than at each consumer.
    /// </summary>
    /// <remarks>
    ///     A skill whose stored Name no longer satisfies the Agent Skills specification is dropped here rather than
    ///     carried forward. Rows predating the switch to <see cref="AgentSkillFrontmatter" /> validation may hold a
    ///     name with consecutive hyphens, which the local regex once accepted; constructing an <c>AgentInlineSkill</c>
    ///     from one throws <see cref="ArgumentException" /> and takes down the whole turn at agent-construction time —
    ///     in both the invocation factory and the sub-agent spawn path. Dropping degrades one skill instead of failing
    ///     the agent, matching the dropped-tool posture in <see cref="ProjectAllowedTools" />: degrade, log, never
    ///     fabricate. This is the single choke point every skills consumer routes through, so the guard covers them all.
    /// </remarks>
    private async Task<IReadOnlyList<ResolvedSkill>> ResolveSkillsAsync(AgentDefinitionRecord definition, CancellationToken cancellationToken)
    {
        var assignedIds = definition.AllowedSkillIds;
        if (assignedIds is null || assignedIds.Count == 0)
        {
            return [];
        }

        var enabled = await _agentSkillStore.ListEnabledByIdsAsync(assignedIds, cancellationToken).ConfigureAwait(false);

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
    ///     Projects one stored skill onto the runtime DTO, applying the trust decision that every skills consumer
    ///     inherits. An operator-authored (<see cref="AgentSkillOrigin.Local" />) row passes through with its bytes
    ///     EXACTLY as stored — anything else would move the body hash, and therefore the runtime config hash, of every
    ///     locally authored skill in the library. An <see cref="AgentSkillOrigin.Imported" /> row is third-party text we
    ///     did not write and cannot validate, so its body AND every bundled resource payload are wrapped in the
    ///     untrusted-content fence before they can reach the model — the same boundary this repo already puts around
    ///     knowledge-base hits, read documents, uploaded attachments and coder workspace reads. Without it, imported
    ///     markdown would be the single most trusted text in the context: it is injected verbatim as instructions, and
    ///     an indirect-prompt-injection payload inside it needs no approval to reach the tools that require none.
    /// </summary>
    /// <remarks>
    ///     The DETERMINISTIC-nonce overload is mandatory here. A random nonce per resolve would change the fenced bytes
    ///     on every turn, moving the folded body hash and flapping the runtime config hash — resume would never match
    ///     twice. The seed is the skill's identity (id + version): stable across resolves, and unpredictable to whoever
    ///     authored the content, because the id is a server-minted GUID assigned at import that never appears in the
    ///     skill file. That is the property the fence needs (a body author who cannot derive the nonce cannot forge the
    ///     closing marker). The node-key-derived seed used for chat attachments guards a different threat — there the
    ///     salt is the conversation id, which IS handed back to clients — and buying it here would cost a node-key
    ///     dependency on the resolver for no additional protection: anyone who can read a skill's id already has
    ///     authenticated node-local access and could simply store the content as Local.
    ///     <para>
    ///         Known residual: MAF renders a skill's NAME and DESCRIPTION, and each resource's name and description,
    ///         into the generated skill content outside any fence we control — they are lookup keys, not payload. They
    ///         are length-capped by frontmatter validation, shown verbatim in the import preview the operator must
    ///         approve, and are additionally carried INSIDE the fence as metadata so the boundary states what the
    ///         surrounding text claims to be.
    ///     </para>
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

            // One seed per skill, but the framing binds the marker to the payload as well (it HMACs the fenced content
            // under the seed), so each resource — and the body — still gets a marker of its own. One resource's
            // model-visible closing marker therefore cannot close another's fence.
            var content = fenceNonceSeed is null
                ? resource.Content
                : UntrustedContentFraming.WrapDocument(resource.Content, BuildFenceMetadata(skill, resource), fenceNonceSeed);
            projected[index] = new ResolvedSkillResource(resource.Name, resource.Description, resource.MediaType, content);
        }

        return projected;
    }

    /// <summary>
    ///     The labels that ride INSIDE an imported skill's fence. Every attacker-controlled field the fence can carry
    ///     goes in here (source, skill name, resource name and media type) rather than being emitted around the
    ///     boundary, and the trust label states plainly what the enclosed bytes are. Blank values are dropped by the
    ///     framing, so the body's metadata block is the resource block minus the two resource labels.
    /// </summary>
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
    ///     Composes the definition's final resolved prompt: the versioned base instruction scaffold (identity/
    ///     grounding/tool/output discipline), a blank line, then the persona prompt (Instructions, with playbook
    ///     memories folded in per <see cref="ComposePersonaPromptAsync" />). A definition with
    ///     <see cref="AgentDefinitionRecord.DisableBaseScaffold" /> set — or the defensive case of a blank scaffold
    ///     resource — skips the prepend entirely, keeping the resolved prompt byte-identical to the pre-scaffold
    ///     persona-only path (preserving that definition's config hash across the scaffold's introduction).
    /// </summary>
    private async Task<string> ComposePromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        var personaPrompt = await ComposePersonaPromptAsync(definition, retrievalQuery, cancellationToken).ConfigureAwait(false);
        return definition.DisableBaseScaffold
            ? personaPrompt
            : BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), personaPrompt);
    }

    /// <summary>
    ///     Folds the definition's enabled playbook actions into its prompt when the playbook is enabled. When it is
    ///     disabled the query is skipped entirely and the base Instructions flow through unchanged — keeping the
    ///     resolved prompt (and thus the runtime config hash) byte-identical to the no-playbook path. When the enabled set
    ///     exceeds the retrieval threshold and a non-blank <paramref name="retrievalQuery" /> is supplied, only the
    ///     top-k most relevant actions are injected (relevance retrieval and cohort monitoring, the relevance-retrieval gate); at or below the threshold — or with a blank
    ///     query — the full static prepend is used, so the resolved prompt stays byte-identical to the pre-retrieval path.
    /// </summary>
    private async Task<string> ComposePersonaPromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        if (!definition.PlaybookEnabled)
        {
            return definition.Instructions;
        }

        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        var selected = await PlaybookRetrievalSelector.SelectAsync(_retrievalRanker,
            retrievalQuery,
            enabled,
            _retrievalOptions.RetrievalThreshold,
            _retrievalOptions.TopK,
            cancellationToken,
            _retrievalOptions.MaxInjectedMemoryTokens,
            _retrievalOptions.MaxInjectedFailureMemoryTokens,
            _logger).ConfigureAwait(false);
        return PlaybookPromptComposer.Compose(definition.Instructions, selected);
    }

    private async Task<IReadOnlyList<AllowedToolDto>> ProjectAllowedToolsAsync(AgentDefinitionRecord definition, string? effectiveModelId, bool supportsTools, bool effectiveModelIsCloud,
        CancellationToken cancellationToken)
    {
        // A model that does not advertise the Ollama "tools" capability cannot drive ANY tool call, so withhold the
        // entire offer (empty) before the per-tool name gating below. This is the capability gate; the offer provider's
        // ToolCapableModels name allow-list remains the additional gate for high-risk tools (run_in_agent_home / MCP).
        if (!supportsTools)
        {
            return [];
        }

        // The seeded "Default Assistant" (mode-off persona) reproduces today's chat exactly: it receives the FULL
        // capability-gated offer for the effective model, NOT the intersected allowed set. It is the ONLY
        // definition granted the full offer — every other definition stays intersected (security invariant: a selected
        // agent's tool offer is never widened beyond its allowed set). The provenance is forge-proof (only the seeder
        // mints Source=Seeded with this slug), so an operator-authored row can never claim the full offer.
        if (definition.Source == AgentDefinitionSource.Seeded
            && string.Equals(definition.SeedSlug, AgentDefaults.DefaultAgentSeedSlug, StringComparison.Ordinal))
        {
            // The mode-off Default Assistant takes the WHOLE capability-gated offer (never the intersected allowed set),
            // but the node-default approval policy still applies (tighten-only) so a node-wide policy is not bypassable by
            // plain mode-off chat. With NO node policy configured the Permissive floor is identity, so the offer — and the
            // runtime-package config hash — stay byte-identical to the mode-off path from before this feature existed. Per-agent ToolApprovals
            // are intentionally NOT applied here: this path reproduces plain chat, which carries no per-agent overrides.
            var wholeOffer = await _localToolOfferProvider.GetOfferedToolsAsync(effectiveModelId, effectiveModelIsCloud, cancellationToken).ConfigureAwait(false);
            AllowedToolDto[] composedWholeOffer =
            [
                .. wholeOffer.Select(tool => tool with
                {
                    RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                })
            ];

            // The Default Assistant ships with ZERO AllowedToolNames, so ask_user must not depend on the allowed set to
            // reach it. It arrives here inside the whole offer already, which makes this call a no-op today; it is kept
            // so the availability rule is enforced AT the seam rather than remotely, and a future gate on the offer side
            // cannot silently drop the tool from mode-off chat.
            return AskUserToolOffer.EnsureOffered(composedWholeOffer, wholeOffer, _toolApprovalPolicy);
        }

        // Start from the PROFILE offer pool for the effective model (the whole capability-gated offer PLUS the
        // opt-in-only spawn_subagent), then keep only the tools the definition allows and resolve each tool's approval
        // flag through the TIGHTEN-ONLY compose below. Using the profile pool — not the whole offer — is what lets a
        // profile that lists spawn_subagent resolve it while the default/mode-off path never does. Tools the definition
        // names but the pool does not contain (uninstalled or not capability-eligible) are dropped and logged — never
        // fabricated.
        var offered = await _localToolOfferProvider.GetOfferedToolsForProfileAsync(effectiveModelId, effectiveModelIsCloud, cancellationToken).ConfigureAwait(false);
        var allowedNames = new HashSet<string>(definition.AllowedToolNames, StringComparer.Ordinal);

        var projected = offered
                        .Where(tool => allowedNames.Contains(tool.Name))
                        .Select(tool => tool with
                        {
                            // TIGHTEN-ONLY 3-tier compose: the node policy (which already ORs the tool's catalog
                            // default with its category/per-tool node rule) first, THEN the per-agent override can only
                            // ADD approval. A per-agent true tightens a tool the node policy left auto-execute; a
                            // per-agent false is a NO-OP (it can no longer loosen a tool the node policy — or the catalog
                            // default — requires approval for). The pre-wrap floor at the registries remains authoritative
                            // for execution regardless of this flag.
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

        // ask_user is unioned in AFTER the intersection, so a bound agent gets it whatever its AllowedToolNames says
        // (including the empty set): being able to ask the operator a question is a property of running an interactive
        // turn, not a per-agent permission. Widening is safe here in a way it would not be for any other tool — the
        // union adds an approval-gated, side-effect-free tool whose only action is to show the operator a question, and
        // its approval flag goes through the same tighten-only compose as everything else.
        return AskUserToolOffer.EnsureOffered(projected, offered, _toolApprovalPolicy);
    }
}
