namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using System.Buffers;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

internal sealed class McpAgentRunRequestFingerprint(McpAgentRunPayloadProtector protector)
{
    private const int CanonicalVersion = 1;
    private readonly McpAgentRunPayloadProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public byte[] Compute(McpAgentRunStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var canonical = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CanonicalVersion);
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("task", request.Task);
            writer.WriteString("agentKey", NullIfWhiteSpace(request.Binding.AgentKey));
            writer.WriteString("modelId", NullIfWhiteSpace(request.Binding.ModelId));
            writer.WriteString("modelOverrideId", NullIfWhiteSpace(request.Binding.ModelOverrideId));
            writer.WriteString("instructions", NullIfWhiteSpace(request.Binding.Instructions));
            if (request.WorkspaceId is { } workspaceId)
            {
                writer.WriteString("workspaceId", workspaceId);
            }
            else
            {
                writer.WriteNull("workspaceId");
            }

            writer.WriteEndObject();
        }

        return _protector.ComputeRequestFingerprint(canonical.WrittenSpan);
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
