namespace XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;

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
            package.Timeouts);
    }

    public static string Compute(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts)
    {
        var canonicalJson = SerializeCanonicalJson(agentDefinitionVersion,
            resolvedSystemPrompt,
            allowedTools,
            modelProfile,
            timeouts);

        return FormatLowercaseHex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }

    public static string SerializeCanonicalJson(int agentDefinitionVersion,
        string resolvedSystemPrompt,
        IReadOnlyList<MixedEnvelopeAllowedToolDto> allowedTools,
        string? modelProfile,
        TimeoutSettings timeouts)
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
                    Schema = tool.Schema
                })
            ],
            ModelProfile = modelProfile,
            Timeouts = new TimeoutSettingsHashPayload
            {
                InvocationTimeoutSeconds = timeouts.InvocationTimeoutSeconds,
                ToolCallTimeoutSeconds = timeouts.ToolCallTimeoutSeconds,
                StreamIdleTimeoutSeconds = timeouts.StreamIdleTimeoutSeconds
            }
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
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
        public required TimeoutSettingsHashPayload Timeouts { get; init; }
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
}
