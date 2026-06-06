namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RuntimePackageConfigHashTests
{
    // Frozen golden: the digest of the no-playbook / empty-playbook config (version 7, prompt "You are the bound
    // persona.", no tools, default timeouts). Pinned as a literal so any canonical-serialization drift fails loudly.
    private const string EmptyPlaybookDigest = "0727fbe875f076fbdb61c855b1f6ec2d11c06a692f82155b44a9a0b02e9a7df9";


    [Test]
    public void Compute_WhenUsingSharedVector_ReturnsExpectedDigest()
    {
        var canonicalJson = RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        var digest = RuntimePackageConfigHash.Compute(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        AssertEx.Equal(
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"open_url\",\"description\":\"Open a URL in the worker browser\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"url\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"url\\\"]}\",\"location\":0,\"requiresApproval\":false}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("a532bda9b1fbae5b0cb6982317a98450be90a5694bb91e492a552cfed4fdd4ae", digest);
    }

    // CENTRAL regression for the agent-skills feature: a null OR empty resolved skill set must yield the EXACT legacy
    // canonical JSON and digest the pre-skills payload produced (and that the server MixedEnvelopeConfigHashService
    // still reproduces). The skill field is emitted WhenWritingNull, so the JSON ends at "timeouts" exactly as the
    // shared vector above. If this drifts, every encrypted invocation fails runtime-package-config-hash-mismatch.
    [Test]
    public void ConfigHash_NoSkills_ByteIdenticalToPreSkillsDigest()
    {
        var nullSkillsJson = SerializeSharedVector(skills: null);
        var emptySkillsJson = SerializeSharedVector(skills: []);
        var nullSkillsDigest = ComputeSharedVector(skills: null);
        var emptySkillsDigest = ComputeSharedVector(skills: []);

        const string ExpectedJson =
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"open_url\",\"description\":\"Open a URL in the worker browser\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"url\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"url\\\"]}\",\"location\":0,\"requiresApproval\":false}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}";

        AssertEx.Equal(ExpectedJson, nullSkillsJson);
        AssertEx.Equal(ExpectedJson, emptySkillsJson);
        AssertEx.Equal("a532bda9b1fbae5b0cb6982317a98450be90a5694bb91e492a552cfed4fdd4ae", nullSkillsDigest);
        AssertEx.Equal("a532bda9b1fbae5b0cb6982317a98450be90a5694bb91e492a552cfed4fdd4ae", emptySkillsDigest);
    }

    // Resume invalidation: a non-empty skill set must change the digest off the no-skills baseline, and EACH of a body
    // edit, a rename, and a picklist change (an added/removed skill) must move it again. The body is HASHED into the
    // payload — never embedded — so a body edit changes the digest without placing plaintext in the canonical JSON.
    [Test]
    public void ConfigHash_SkillBodyEdit_RenameOrPicklistChange_ChangesDigest()
    {
        var skillId = Guid.NewGuid();
        var secondSkillId = Guid.NewGuid();

        var baseline = ComputeSharedVector(skills: null);
        var withSkill = ComputeSharedVector(skills: [new ResolvedSkill(skillId, "kubernetes-debug", "Debug k8s", "## Body v1", 1)]);
        var bodyEdited = ComputeSharedVector(skills: [new ResolvedSkill(skillId, "kubernetes-debug", "Debug k8s", "## Body v2", 1)]);
        var renamed = ComputeSharedVector(skills: [new ResolvedSkill(skillId, "k8s-debug", "Debug k8s", "## Body v1", 1)]);
        var descriptionEdited = ComputeSharedVector(skills: [new ResolvedSkill(skillId, "kubernetes-debug", "Debug Kubernetes clusters", "## Body v1", 1)]);
        var versionBumped = ComputeSharedVector(skills: [new ResolvedSkill(skillId, "kubernetes-debug", "Debug k8s", "## Body v1", 2)]);
        var picklistAdded = ComputeSharedVector(skills:
        [
            new ResolvedSkill(skillId, "kubernetes-debug", "Debug k8s", "## Body v1", 1),
            new ResolvedSkill(secondSkillId, "log-triage", "Triage logs", "## Logs", 1)
        ]);

        AssertEx.True(withSkill != baseline, "Adding a skill must change the digest off the no-skills baseline.");
        AssertEx.True(bodyEdited != withSkill, "A skill body edit must change the digest.");
        AssertEx.True(renamed != withSkill, "A skill rename must change the digest.");
        AssertEx.True(descriptionEdited != withSkill, "A skill description edit must change the digest.");
        AssertEx.True(versionBumped != withSkill, "A skill version bump must change the digest.");
        AssertEx.True(picklistAdded != withSkill, "Adding a skill to the picklist must change the digest.");
    }

    private static string SerializeSharedVector(IReadOnlyList<ResolvedSkill>? skills)
    {
        return RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            reasoningEffort: null,
            orchestrationSpec: null,
            skills: skills);
    }

    private static string ComputeSharedVector(IReadOnlyList<ResolvedSkill>? skills)
    {
        return RuntimePackageConfigHash.Compute(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\",\"properties\":{\"url\":{\"type\":\"string\"}},\"required\":[\"url\"]}"
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            reasoningEffort: null,
            orchestrationSpec: null,
            skills: skills);
    }

    // Cross-repo round-trip guard (worker half): a ClientLocal tool carrying RequiresApproval must canonicalize to
    // the SAME bytes/digest the server MixedEnvelopeConfigHashService produces for the identical fixture vector. The
    // matching server assertion lives in C0re.Tests.UnitTests MixedEnvelopeConfigHashServiceTests; if these two golden
    // strings ever diverge, every encrypted invocation fails runtime-package-config-hash-mismatch.
    [Test]
    public void Compute_WhenUsingClientLocalSharedVector_CarriesLocationAndApprovalInDigest()
    {
        var canonicalJson = RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "run_in_agent_home",
                    Description = "Run a task in the agent home workspace",
                    Schema = "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\"}},\"required\":[\"goal\"]}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        var digest = RuntimePackageConfigHash.Compute(7,
            "You are a helpful local AI assistant.",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "run_in_agent_home",
                    Description = "Run a task in the agent home workspace",
                    Schema = "{\"type\":\"object\",\"properties\":{\"goal\":{\"type\":\"string\"}},\"required\":[\"goal\"]}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
                }
            ],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            });

        AssertEx.Equal(
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"run_in_agent_home\",\"description\":\"Run a task in the agent home workspace\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"goal\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"goal\\\"]}\",\"location\":1,\"requiresApproval\":true}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("58ed36f89182fe9e40d0c0d0dc4bc8cbee19e210419046e188e2af91fb077fbb", digest);
    }

    [Test]
    public void Compute_WhenToolLocationChanges_ChangesDigest()
    {
        var apiSideDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ApiSide,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        var clientLocalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(apiSideDigest, clientLocalDigest);
    }

    [Test]
    public void Compute_WhenRequiresApprovalChanges_ChangesDigest()
    {
        var withoutApprovalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = false
                }
            ],
            null,
            new TimeoutSettings());

        var withApprovalDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool",
                    Description = "d",
                    Schema = "{}",
                    Location = ToolLocation.ClientLocal,
                    RequiresApproval = true
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(withoutApprovalDigest, withApprovalDigest);
    }

    [Test]
    public void Compute_WhenReasoningEffortChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "low");

        var secondDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "high");

        AssertEx.NotEqual(firstDigest, secondDigest);
    }

    [Test]
    public void Compute_WhenReasoningEffortIsBinaryOn_DiffersFromNullAndNone()
    {
        var onDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "on");

        var nullDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings());

        var noneDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings(),
            "none");

        AssertEx.NotEqual(nullDigest, onDigest);
        AssertEx.NotEqual(noneDigest, onDigest);
    }

    // Stability guard: the binary-"on" feature must NOT shift the hash of any capable-model effort. The canonical
    // JSON for low/medium/high/none must remain byte-identical to the pre-fix serialization (only the previously
    // failing binary-on turn changes). Pinned as literals so any drift in named-effort normalization fails loudly.
    [Test]
    [Arguments("low", "low")]
    [Arguments("medium", "medium")]
    [Arguments("high", "high")]
    [Arguments("none", "none")]
    public void SerializeCanonicalJson_WhenReasoningEffortIsNamed_IsUnchanged(string reasoningEffort, string expectedNormalized)
    {
        var canonicalJson = RuntimePackageConfigHash.SerializeCanonicalJson(7,
            "prompt",
            [],
            null,
            new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            reasoningEffort);

        AssertEx.Equal("{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"prompt\",\"allowedTools\":[],\"modelProfile\":null,\"reasoningEffort\":\"" +
                       expectedNormalized +
                       "\",\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
    }

    [Test]
    public void Compute_WhenAllowedToolOrderChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_a",
                    Description = "A",
                    Schema = "{}"
                },
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_b",
                    Description = "B",
                    Schema = "{}"
                }
            ],
            null,
            new TimeoutSettings());

        var secondDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_b",
                    Description = "B",
                    Schema = "{}"
                },
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "tool_a",
                    Description = "A",
                    Schema = "{}"
                }
            ],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(firstDigest, secondDigest);
    }

    [Test]
    public void Compute_WhenAgentDefinitionVersionChanges_ChangesDigest()
    {
        var firstDigest = RuntimePackageConfigHash.Compute(7,
            "prompt",
            [],
            null,
            new TimeoutSettings());

        var secondDigest = RuntimePackageConfigHash.Compute(8,
            "prompt",
            [],
            null,
            new TimeoutSettings());

        AssertEx.NotEqual(firstDigest, secondDigest);
    }

    // The playbook reaches the config hash only through resolvedSystemPrompt (the composed prompt). The
    // following tests pin that the composer's output flows into the digest as expected. The byte-identical guard — an
    // empty playbook leaving resolvedSystemPrompt unchanged — is the central regression invariant.

    [Test]
    public void Compute_WhenPlaybookEmpty_MatchesBasePromptDigest()
    {
        const string basePrompt = "You are the bound persona.";
        var composedEmpty = PlaybookPromptComposer.Compose(basePrompt, []);

        var baseDigest = RuntimePackageConfigHash.Compute(7, basePrompt, [], null, new TimeoutSettings());
        var composedDigest = RuntimePackageConfigHash.Compute(7, composedEmpty, [], null, new TimeoutSettings());

        AssertEx.Equal(baseDigest, composedDigest);

        // Pin the no-playbook digest as a frozen literal (mirrors the a532bda9.../58ed36f8... goldens above). The
        // base==composed assertion alone recomputes through the same code and would drift silently if the canonical
        // serialization ever changed; pinning the literal makes any such drift fail loudly. The empty-playbook prompt
        // is byte-identical to the base prompt, so both equal this digest.
        AssertEx.Equal(EmptyPlaybookDigest, composedDigest);
    }

    [Test]
    public void Compute_WhenPlaybookActionAppended_ChangesDigest()
    {
        const string basePrompt = "You are the bound persona.";
        var composed = PlaybookPromptComposer.Compose(basePrompt, [EnabledAction("Run the tests first.", priority: 1)]);

        var baseDigest = RuntimePackageConfigHash.Compute(7, basePrompt, [], null, new TimeoutSettings());
        var composedDigest = RuntimePackageConfigHash.Compute(7, composed, [], null, new TimeoutSettings());

        AssertEx.NotEqual(baseDigest, composedDigest);
    }

    [Test]
    public void Compute_WhenPlaybookBehaviorEdited_ChangesDigest()
    {
        const string basePrompt = "You are the bound persona.";
        var first = PlaybookPromptComposer.Compose(basePrompt, [EnabledAction("Run the tests first.", priority: 1)]);
        var edited = PlaybookPromptComposer.Compose(basePrompt, [EnabledAction("Run the FULL test suite first.", priority: 1)]);

        var firstDigest = RuntimePackageConfigHash.Compute(7, first, [], null, new TimeoutSettings());
        var editedDigest = RuntimePackageConfigHash.Compute(7, edited, [], null, new TimeoutSettings());

        AssertEx.NotEqual(firstDigest, editedDigest);
    }

    [Test]
    public void Compute_WhenPlaybookPriorityReordered_ChangesDigest()
    {
        // The store orders by Priority; a reorder swaps the bullet order in the composed prompt, which must change the
        // digest. (The composer emits in the order received, so the test supplies the two priority orderings directly.)
        const string basePrompt = "You are the bound persona.";
        var order1 = PlaybookPromptComposer.Compose(basePrompt,
        [
            EnabledAction("Run the tests first.", priority: 1),
            EnabledAction("Prefer small commits.", priority: 5)
        ]);
        var order2 = PlaybookPromptComposer.Compose(basePrompt,
        [
            EnabledAction("Prefer small commits.", priority: 1),
            EnabledAction("Run the tests first.", priority: 5)
        ]);

        var order1Digest = RuntimePackageConfigHash.Compute(7, order1, [], null, new TimeoutSettings());
        var order2Digest = RuntimePackageConfigHash.Compute(7, order2, [], null, new TimeoutSettings());

        AssertEx.NotEqual(order1Digest, order2Digest);
    }

    private static PlaybookActionRecord EnabledAction(string behavior, int priority)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            behavior,
            Scope: null,
            priority,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
