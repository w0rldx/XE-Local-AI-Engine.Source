namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using System.Buffers;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;

internal sealed class McpAgentRunRequestFingerprint(McpAgentRunPayloadProtector protector)
{
    private const int AgenticCanonicalVersion = 2;
    private const int DelegateCanonicalVersion = 1;
    private readonly McpAgentRunPayloadProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public byte[] Compute(McpAgentRunStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var canonical = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", request.Binding.InboundContext.IsAgentic ? AgenticCanonicalVersion : DelegateCanonicalVersion);
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("task", request.Task);
            writer.WriteString("agentKey", NullIfWhiteSpace(request.Binding.AgentKey));
            writer.WriteString("modelId", NullIfWhiteSpace(request.Binding.ModelId));
            writer.WriteString("modelOverrideId", NullIfWhiteSpace(request.Binding.ModelOverrideId));
            writer.WriteString("instructions", NullIfWhiteSpace(request.Binding.Instructions));
            if (request.Binding.InboundContext.IsAgentic)
            {
                writer.WriteString("mcpScope", request.Binding.InboundContext.Scope.ToString());
                writer.WriteString("mcpKeyPrefix", request.Binding.InboundContext.KeyPrefix);
            }
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

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
