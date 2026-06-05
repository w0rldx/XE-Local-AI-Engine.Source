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

    public static string Compute(EncryptedRuntimePackageDto package)
    {
        ArgumentNullException.ThrowIfNull(package);

        return Compute(package.AgentDefinitionVersion,
            package.ResolvedSystemPrompt,
            package.AllowedTools,
            package.ModelProfile,
            package.Timeouts,
            package.ReasoningEffort);
    }

    public static string Compute(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts,
        string? reasoningEffort = null,
        OrchestrationSpec? orchestrationSpec = null)
    {
        var canonicalJson = SerializeCanonicalJson(agentDefinitionVersion,
            resolvedSystemPrompt,
            allowedTools,
            modelProfile,
            timeouts,
            reasoningEffort,
            orchestrationSpec);

        return FormatLowercaseHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public static string SerializeCanonicalJson(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts,
        string? reasoningEffort = null,
        OrchestrationSpec? orchestrationSpec = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedSystemPrompt);
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
            // The orchestration spec is folded deterministically (sorted participants/tools/edges) ONLY when present.
            // It is emitted with WhenWritingNull so a single-agent loopback or the encrypted/server path (which never
            // sets it) serializes BYTE-IDENTICALLY to the pre-P5 payload — the cross-repo round-trip digest depends on
            // this. The per-property condition overrides the type-wide DefaultIgnoreCondition=Never.
            Orchestration = BuildOrchestrationHashPayload(orchestrationSpec)
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
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
                           // Unlike the top-level MapAllowedTools (which drops Description), a participant tool's
                           // Description IS folded into the hash: a participant agent's tool description is shown to the
                           // model and can influence its tool choice within that participant, so a description edit is a
                           // config-affecting change that must invalidate resume. (For MCP tools this is non-null.)
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

        // Last field, omitted entirely when null so the pre-P5 payload is byte-identical (the cross-repo round-trip
        // digest depends on this). Only the loopback orchestration path sets it.
        [JsonPropertyOrder(7)]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OrchestrationHashPayload? Orchestration { get; init; }
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
