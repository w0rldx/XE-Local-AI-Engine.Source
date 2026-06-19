namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OrchestrationConfigHashTests
{
    [Test]
    public void Compute_WhenSpecIsNull_IsByteIdenticalToPreP5Vector()
    {
        // The cross-repo round-trip guard: a null orchestration spec MUST yield the exact legacy canonical JSON and
        // digest the server MixedEnvelopeConfigHashService reproduces. If this drifts, every encrypted invocation
        // fails runtime-package-config-hash-mismatch.
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
            },
            orchestrationSpec: null);

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
            },
            orchestrationSpec: null);

        // Byte-identical to RuntimePackageConfigHashTests.Compute_WhenUsingSharedVector_ReturnsExpectedDigest: the
        // orchestration field is omitted entirely when null, so the JSON ends at "timeouts" exactly as in the legacy vector.
        AssertEx.Equal(
            "{\"agentDefinitionVersion\":7,\"resolvedSystemPrompt\":\"You are a helpful local AI assistant.\",\"allowedTools\":[{\"name\":\"open_url\",\"description\":\"Open a URL in the worker browser\",\"schema\":\"{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"url\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"url\\\"]}\",\"location\":0,\"requiresApproval\":false}],\"modelProfile\":null,\"reasoningEffort\":null,\"timeouts\":{\"invocationTimeoutSeconds\":300,\"toolCallTimeoutSeconds\":60,\"streamIdleTimeoutSeconds\":30}}",
            canonicalJson);
        AssertEx.Equal("a532bda9b1fbae5b0cb6982317a98450be90a5694bb91e492a552cfed4fdd4ae", digest);
    }

    [Test]
    public void Compute_WhenSpecPresent_ChangesDigestFromNullSpec()
    {
        var nullDigest = ComputeWith(null);
        var specDigest = ComputeWith(SampleSpec());

        AssertEx.True(nullDigest != specDigest, "A present orchestration spec must change the config hash.");
    }

    [Test]
    public void Compute_WhenParticipantsReordered_YieldsSameDigest()
    {
        var ordered = SampleSpec();
        var reordered = ordered with
        {
            Participants = [.. ordered.Participants.AsEnumerable().Reverse()]
        };

        AssertEx.Equal(ComputeWith(ordered), ComputeWith(reordered));
    }

    [Test]
    public void Compute_WhenEdgesReordered_YieldsSameDigest()
    {
        var ordered = SampleSpec();
        var reordered = ordered with
        {
            Edges = [.. ordered.Edges.AsEnumerable().Reverse()]
        };

        AssertEx.Equal(ComputeWith(ordered), ComputeWith(reordered));
    }

    [Test]
    public void Compute_WhenParticipantToolsReordered_YieldsSameDigest()
    {
        var ordered = SampleSpec();
        var reorderedParticipants = ordered.Participants
                                           .Select(participant => participant with
                                           {
                                               Tools = [.. participant.Tools.AsEnumerable().Reverse()]
                                           })
                                           .ToArray();
        var reordered = ordered with
        {
            Participants = reorderedParticipants
        };

        AssertEx.Equal(ComputeWith(ordered), ComputeWith(reordered));
    }

    [Test]
    public void Compute_WhenParticipantPromptChanges_ChangesDigest()
    {
        var baseSpec = SampleSpec();
        var changed = baseSpec with
        {
            Participants =
            [
                baseSpec.Participants[0] with
                {
                    Instructions = "A different participant prompt."
                },
                baseSpec.Participants[1]
            ]
        };

        AssertEx.True(ComputeWith(baseSpec) != ComputeWith(changed), "Changing a participant prompt must change the config hash.");
    }

    [Test]
    public void Compute_WhenParticipantToolSetChanges_ChangesDigest()
    {
        var baseSpec = SampleSpec();
        var changed = baseSpec with
        {
            Participants =
            [
                baseSpec.Participants[0] with
                {
                    Tools = []
                },
                baseSpec.Participants[1]
            ]
        };

        AssertEx.True(ComputeWith(baseSpec) != ComputeWith(changed), "Changing a participant's tool set must change the config hash.");
    }

    [Test]
    public void Compute_WhenEdgeChanges_ChangesDigest()
    {
        var baseSpec = SampleSpec();
        var changed = baseSpec with
        {
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b",
                    Reason = "a different reason"
                }
            ]
        };

        AssertEx.True(ComputeWith(baseSpec) != ComputeWith(changed), "Changing an edge must change the config hash.");
    }

    [Test]
    public void Compute_WhenMaxTurnsChanges_ChangesDigest()
    {
        var baseSpec = SampleSpec();
        var changed = baseSpec with
        {
            MaxTurnsPerAgent = baseSpec.MaxTurnsPerAgent + 1
        };

        AssertEx.True(ComputeWith(baseSpec) != ComputeWith(changed), "Changing maxTurns must change the config hash.");
    }

    [Test]
    public void Compute_WhenReturnToPreviousChanges_ChangesDigest()
    {
        var baseSpec = SampleSpec();
        var changed = baseSpec with
        {
            ReturnToPrevious = !baseSpec.ReturnToPrevious
        };

        AssertEx.True(ComputeWith(baseSpec) != ComputeWith(changed), "Changing returnToPrevious must change the config hash.");
    }

    private static string ComputeWith(OrchestrationSpec? spec)
    {
        return RuntimePackageConfigHash.Compute(1,
            "You are a helpful local AI assistant.",
            [],
            "qwen3:8b",
            new TimeoutSettings(),
            null,
            spec);
    }

    private static OrchestrationSpec SampleSpec()
    {
        return new OrchestrationSpec
        {
            TriageParticipantKey = "a",
            MaxTurnsPerAgent = 6,
            ReturnToPrevious = false,
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "a",
                    Name = "Triage",
                    Description = "Routes work",
                    Instructions = "You are the triage agent.",
                    ModelId = "qwen3:8b",
                    ReasoningEffort = "low",
                    Tools =
                    [
                        new AllowedToolDto
                        {
                            Id = Guid.Empty,
                            Name = "GetCurrentTime",
                            Location = ToolLocation.ClientLocal
                        },
                        new AllowedToolDto
                        {
                            Id = Guid.Empty,
                            Name = "Calculate",
                            Location = ToolLocation.ClientLocal
                        }
                    ]
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Description = "Does the work",
                    Instructions = "You are the specialist.",
                    ModelId = "qwen3:8b",
                    ReasoningEffort = null,
                    Tools = []
                }
            ],
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b",
                    Reason = "specialist work"
                }
            ]
        };
    }
}
