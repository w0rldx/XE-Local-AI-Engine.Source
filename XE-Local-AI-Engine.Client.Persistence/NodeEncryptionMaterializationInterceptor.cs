namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeEncryptionMaterializationInterceptor : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (materializationData.Context is not NodeChatDbContext context)
        {
            return entity;
        }

        switch (entity)
        {
            case NodeMessage message:
                message.Content = NodePayloadProtector.Decrypt(message.Content, context.NodeEncryptionKey.Span, message.ConversationId, message.MessageId, "content");
                message.MetadataJson = DecryptIfPresent(message.MetadataJson, context.NodeEncryptionKey.Span, message.ConversationId, message.MessageId, "metadata_json");
                break;
            case NodeToolEvent toolEvent:
                toolEvent.PlaintextArgs = DecryptIfPresent(toolEvent.PlaintextArgs, context.NodeEncryptionKey.Span, toolEvent.ConversationId, toolEvent.ToolCallId, "plaintext_args");
                toolEvent.PlaintextResult = DecryptIfPresent(toolEvent.PlaintextResult, context.NodeEncryptionKey.Span, toolEvent.ConversationId, toolEvent.ToolCallId, "plaintext_result");
                break;
            case NodeSelectedFolder selectedFolder:
                selectedFolder.HostPath = NodePayloadProtector.Decrypt(selectedFolder.HostPath, context.NodeEncryptionKey.Span, Guid.Empty, selectedFolder.Id, "host_path");
                break;
            case AgentDefinition definition:
                definition.Instructions = NodePayloadProtector.Decrypt(definition.Instructions, context.NodeEncryptionKey.Span, Guid.Empty, definition.Id, "instructions");
                definition.Description = DecryptIfPresent(definition.Description, context.NodeEncryptionKey.Span, Guid.Empty, definition.Id, "description");
                break;
            case McpServerRegistration registration:
                registration.ArgumentsJson = DecryptIfPresent(registration.ArgumentsJson, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "arguments");
                registration.EnvJson = DecryptIfPresent(registration.EnvJson, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "env");
                registration.Description = DecryptIfPresent(registration.Description, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "description");
                break;
        }

        return entity;
    }

    private static byte[]? DecryptIfPresent(byte[]? payload, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName)
    {
        return payload is null ? null : NodePayloadProtector.Decrypt(payload, key, conversationId, recordId, columnName);
    }
}
