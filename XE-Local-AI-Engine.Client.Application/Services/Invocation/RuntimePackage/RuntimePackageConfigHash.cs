namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Represents runtime package config hash.
/// </summary>
public static class RuntimePackageConfigHash
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Compute(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts,
        string? reasoningEffort = null,
        OrchestrationSpec? orchestrationSpec = null,
        IReadOnlyList<ResolvedSkill>? skills = null,
        bool omitSystemPrompt = false,
        bool requireNodeManagedLlama = false)
    {
        var canonicalJson = SerializeCanonicalJson(agentDefinitionVersion,
            resolvedSystemPrompt,
            allowedTools,
            modelProfile,
            timeouts,
            reasoningEffort,
            orchestrationSpec,
            skills,
            omitSystemPrompt,
            requireNodeManagedLlama);

        return FormatLowercaseHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public static string SerializeCanonicalJson(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts,
        string? reasoningEffort = null,
        OrchestrationSpec? orchestrationSpec = null,
        IReadOnlyList<ResolvedSkill>? skills = null,
        bool omitSystemPrompt = false,
        bool requireNodeManagedLlama = false)
    {
        if (resolvedSystemPrompt is null ||
            (omitSystemPrompt
                ? !string.IsNullOrWhiteSpace(resolvedSystemPrompt)
                : string.IsNullOrWhiteSpace(resolvedSystemPrompt)))
        {
            throw new ArgumentException("Resolved system prompt must be blank exactly when it is explicitly omitted.", nameof(resolvedSystemPrompt));
        }

        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(timeouts);

        var payload = new ConfigHashPayload
        {
            AgentDefinitionVersion = agentDefinitionVersion,
            ResolvedSystemPrompt = resolvedSystemPrompt,
            AllowedTools =
            [
                .. allowedTools.Select(static tool => new MixedEnvelopeAllowedToolDto
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Schema = tool.Schema,
                    Location = tool.Location,
                    RequiresApproval = tool.RequiresApproval
                })
            ],
            ModelProfile = modelProfile,
            ReasoningEffort = ReasoningEffortNormalizer.Normalize(reasoningEffort),
            Timeouts = new TimeoutSettingsHashPayload
            {
                InvocationTimeoutSeconds = timeouts.InvocationTimeoutSeconds,
                ToolCallTimeoutSeconds = timeouts.ToolCallTimeoutSeconds,
                StreamIdleTimeoutSeconds = timeouts.StreamIdleTimeoutSeconds
            },
            // Folded deterministically (sorted participants/tools/edges) ONLY when present; the per-property
            // WhenWritingNull overrides the type-wide Never, so a loopback stays BYTE-IDENTICAL for the digest.
            Orchestration = BuildOrchestrationHashPayload(orchestrationSpec),
            // Sorted by Id, body HASHED not embedded, folded ONLY when non-empty and WhenWritingNull like Orchestration.
            // Progressive disclosure keeps bodies out of the prompt, so only this fold invalidates resume on an edit.
            Skills = BuildSkillsHashPayload(skills),
            OmitSystemPrompt = omitSystemPrompt,
            RequireNodeManagedLlama = requireNodeManagedLlama
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static List<SkillHashPayload>? BuildSkillsHashPayload(IReadOnlyList<ResolvedSkill>? skills)
    {
        if (skills is null || skills.Count == 0)
        {
            return null;
        }

        return
        [
            .. skills
               .OrderBy(static skill => skill.Id.ToString("N"), StringComparer.Ordinal)
               .Select(static skill => new SkillHashPayload
               {
                   Name = skill.Name,
                   Description = skill.Description,
                   BodyHash = HashBody(skill.Body),
                   Version = skill.Version
               })
        ];
    }

    private static string HashBody(string body)
    {
        return FormatLowercaseHex(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    }

    private static OrchestrationHashPayload? BuildOrchestrationHashPayload(OrchestrationSpec? spec)
    {
        if (spec is null)
        {
            return null;
        }

        return new OrchestrationHashPayload
        {
            TriageParticipantKey = spec.TriageParticipantKey,
            MaxTurnsPerAgent = spec.MaxTurnsPerAgent,
            ReturnToPrevious = spec.ReturnToPrevious,
            Participants =
            [
                .. spec.Participants
                       .OrderBy(static participant => participant.Key, StringComparer.Ordinal)
                       .Select(static participant => new OrchestrationParticipantHashPayload
                       {
                           Key = participant.Key,
                           Name = participant.Name,
                           Description = participant.Description,
                           Instructions = participant.Instructions,
                           ModelProfile = participant.ModelId,
                           ReasoningEffort = ReasoningEffortNormalizer.Normalize(participant.ReasoningEffort),
                           // Unlike top-level MapAllowedTools, a participant tool's Description IS folded: it is shown
                           // to the model and steers its tool choice, so an edit must invalidate resume.
                           Tools =
                           [
                               .. participant.Tools
                                             .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                                             .Select(static tool => new MixedEnvelopeAllowedToolDto
                                             {
                                                 Name = tool.Name,
                                                 Description = tool.Description,
                                                 Schema = tool.ParameterSchema,
                                                 Location = tool.Location,
                                                 RequiresApproval = tool.RequiresApproval
                                             })
                           ]
                       })
            ],
            Edges =
            [
                .. spec.Edges
                       .OrderBy(static edge => edge.FromKey, StringComparer.Ordinal)
                       .ThenBy(static edge => edge.ToKey, StringComparer.Ordinal)
                       .ThenBy(static edge => edge.Reason, StringComparer.Ordinal)
                       .Select(static edge => new OrchestrationEdgeHashPayload
                       {
                           FromKey = edge.FromKey,
                           ToKey = edge.ToKey,
                           Reason = edge.Reason
                       })
            ]
        };
    }

    private static string FormatLowercaseHex(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length * 2, bytes.ToArray(), static (buffer, source) =>
        {
            const string HexAlphabet = "0123456789abcdef";

            for (var index = 0; index < source.Length; index++)
            {
                var value = source[index];
                buffer[index * 2] = HexAlphabet[value >> 4];
                buffer[(index * 2) + 1] = HexAlphabet[value & 0x0F];
            }
        });
    }

    private sealed record ConfigHashPayload
    {
        [JsonPropertyOrder(1)]
        public required int AgentDefinitionVersion { get; init; }

        [JsonPropertyOrder(2)]
        public required string ResolvedSystemPrompt { get; init; }

        [JsonPropertyOrder(3)]
        public required List<MixedEnvelopeAllowedToolDto> AllowedTools { get; init; }

        [JsonPropertyOrder(4)]
        public string? ModelProfile { get; init; }

        [JsonPropertyOrder(5)]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyOrder(6)]
        public required TimeoutSettingsHashPayload Timeouts { get; init; }

        // Omitted entirely when null so the single-agent payload is byte-identical (the cross-repo round-trip
        // digest depends on this). Only the loopback orchestration path sets it.
        [JsonPropertyOrder(7)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OrchestrationHashPayload? Orchestration { get; init; }

        // Last field, omitted entirely when null so the pre-skills payload is byte-identical (same posture as
        // Orchestration). Only the loopback path with a non-empty resolved skill set populates it.
        [JsonPropertyOrder(8)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SkillHashPayload>? Skills { get; init; }

        // Appended and omitted at its false default, preserving the exact canonical bytes of every existing package.
        [JsonPropertyOrder(9)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool OmitSystemPrompt { get; init; }

        [JsonPropertyOrder(10)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RequireNodeManagedLlama { get; init; }
    }

    private sealed record SkillHashPayload
    {
        [JsonPropertyOrder(1)]
        public required string Name { get; init; }

        [JsonPropertyOrder(2)]
        public required string Description { get; init; }

        [JsonPropertyOrder(3)]
        public required string BodyHash { get; init; }

        [JsonPropertyOrder(4)]
        public required int Version { get; init; }
    }

    private sealed record TimeoutSettingsHashPayload
    {
        [JsonPropertyOrder(1)]
        public required int InvocationTimeoutSeconds { get; init; }

        [JsonPropertyOrder(2)]
        public required int ToolCallTimeoutSeconds { get; init; }

        [JsonPropertyOrder(3)]
        public required int StreamIdleTimeoutSeconds { get; init; }
    }

    private sealed record OrchestrationHashPayload
    {
        [JsonPropertyOrder(1)]
        public required string TriageParticipantKey { get; init; }

        [JsonPropertyOrder(2)]
        public required List<OrchestrationParticipantHashPayload> Participants { get; init; }

        [JsonPropertyOrder(3)]
        public required List<OrchestrationEdgeHashPayload> Edges { get; init; }

        [JsonPropertyOrder(4)]
        public required int MaxTurnsPerAgent { get; init; }

        [JsonPropertyOrder(5)]
        public required bool ReturnToPrevious { get; init; }
    }

    private sealed record OrchestrationParticipantHashPayload
    {
        [JsonPropertyOrder(1)]
        public required string Key { get; init; }

        [JsonPropertyOrder(2)]
        public required string Name { get; init; }

        [JsonPropertyOrder(3)]
        public string? Description { get; init; }

        [JsonPropertyOrder(4)]
        public required string Instructions { get; init; }

        [JsonPropertyOrder(5)]
        public string? ModelProfile { get; init; }

        [JsonPropertyOrder(6)]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyOrder(7)]
        public required List<MixedEnvelopeAllowedToolDto> Tools { get; init; }
    }

    private sealed record OrchestrationEdgeHashPayload
    {
        [JsonPropertyOrder(1)]
        public required string FromKey { get; init; }

        [JsonPropertyOrder(2)]
        public required string ToKey { get; init; }

        [JsonPropertyOrder(3)]
        public string? Reason { get; init; }
    }
}
