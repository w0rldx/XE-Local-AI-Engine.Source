namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Represents node encryption materialization interceptor.
/// </summary>
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
            case CanvasWorkflow canvas:
                canvas.GraphJson = NodePayloadProtector.Decrypt(canvas.GraphJson, context.NodeEncryptionKey.Span, Guid.Empty, canvas.Id, "graph_json");
                break;
            case AgentSkill skill:
                skill.Description = NodePayloadProtector.Decrypt(skill.Description, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "description");
                skill.Body = NodePayloadProtector.Decrypt(skill.Body, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "body");
                break;
            case PlaybookAction action:
                action.Behavior = NodePayloadProtector.Decrypt(action.Behavior, context.NodeEncryptionKey.Span, Guid.Empty, action.Id, "behavior");
                action.TriggerCondition = DecryptIfPresent(action.TriggerCondition, context.NodeEncryptionKey.Span, Guid.Empty, action.Id, "trigger_condition");
                break;
            case GoldenConversation golden:
                golden.InputTurns = NodePayloadProtector.Decrypt(golden.InputTurns, context.NodeEncryptionKey.Span, Guid.Empty, golden.Id, "input_turns");
                golden.Assertion = DecryptIfPresent(golden.Assertion, context.NodeEncryptionKey.Span, Guid.Empty, golden.Id, "assertion");
                golden.Rubric = DecryptIfPresent(golden.Rubric, context.NodeEncryptionKey.Span, Guid.Empty, golden.Id, "rubric");
                break;
            case McpServerRegistration registration:
                registration.ArgumentsJson = DecryptIfPresent(registration.ArgumentsJson, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "arguments");
                registration.EnvJson = DecryptIfPresent(registration.EnvJson, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "env");
                registration.Description = DecryptIfPresent(registration.Description, context.NodeEncryptionKey.Span, Guid.Empty, registration.Id, "description");
                break;
            case ScheduledJobDefinition jobDefinition:
                jobDefinition.ParameterJson = DecryptIfPresent(jobDefinition.ParameterJson, context.NodeEncryptionKey.Span, Guid.Empty, jobDefinition.Id, "parameter_json");
                break;
            case ScheduledJobRun run:
                run.DetailsJson = DecryptIfPresent(run.DetailsJson, context.NodeEncryptionKey.Span, Guid.Empty, run.Id, "details_json");
                break;
            case ScheduledJobRunEvent runEvent:
                runEvent.DataJson = DecryptIfPresent(runEvent.DataJson, context.NodeEncryptionKey.Span, Guid.Empty, runEvent.Id, "data_json");
                break;
            case ModelFitSnapshot snapshot:
                snapshot.RawJson = DecryptIfPresent(snapshot.RawJson, context.NodeEncryptionKey.Span, Guid.Empty, snapshot.Id, "raw_json");
                snapshot.StderrExcerpt = DecryptIfPresent(snapshot.StderrExcerpt, context.NodeEncryptionKey.Span, Guid.Empty, snapshot.Id, "stderr_excerpt");
                snapshot.DiagnosticsJson = DecryptIfPresent(snapshot.DiagnosticsJson, context.NodeEncryptionKey.Span, Guid.Empty, snapshot.Id, "diagnostics_json");
                break;
            case ModelFitBenchmark benchmark:
                benchmark.RawJson = DecryptIfPresent(benchmark.RawJson, context.NodeEncryptionKey.Span, Guid.Empty, benchmark.Id, "bench_raw_json");
                benchmark.DiagnosticsJson = DecryptIfPresent(benchmark.DiagnosticsJson, context.NodeEncryptionKey.Span, Guid.Empty, benchmark.Id, "bench_diagnostics_json");
                break;
        }

        return entity;
    }

    private static byte[]? DecryptIfPresent(byte[]? payload, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName)
    {
        return payload is null ? null : NodePayloadProtector.Decrypt(payload, key, conversationId, recordId, columnName);
    }
}
