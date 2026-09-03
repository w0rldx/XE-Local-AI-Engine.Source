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
            case NodeConversation conversation:
                conversation.Title = DecryptIfPresent(conversation.Title, context.NodeEncryptionKey.Span, conversation.ConversationId, conversation.ConversationId, "title");
                break;
            case NodeMessage message:
                // Content and metadata use the read-both envelope (they have legacy plaintext rows on disk), so an EF
                // read of a raw-path-written or legacy row decrypts identically to the raw read path.
                message.Content = NodeChatContentProtection.Unprotect(message.Content, context.NodeEncryptionKey.Span, message.ConversationId, message.MessageId, "content");
                message.MetadataJson = message.MetadataJson is null
                    ? null
                    : NodeChatContentProtection.Unprotect(message.MetadataJson, context.NodeEncryptionKey.Span, message.ConversationId, message.MessageId, "metadata_json");
                break;
            case NodeToolEvent toolEvent:
                toolEvent.PlaintextArgs = DecryptIfPresent(toolEvent.PlaintextArgs, context.NodeEncryptionKey.Span, toolEvent.ConversationId, toolEvent.ToolCallId, "plaintext_args");
                toolEvent.PlaintextResult = DecryptIfPresent(toolEvent.PlaintextResult, context.NodeEncryptionKey.Span, toolEvent.ConversationId, toolEvent.ToolCallId, "plaintext_result");
                break;
            case NodeSelectedFolder selectedFolder:
                selectedFolder.HostPath = NodePayloadProtector.Decrypt(selectedFolder.HostPath, context.NodeEncryptionKey.Span, Guid.Empty, selectedFolder.Id, "host_path");
                break;
            case DevelopmentTemplate template:
                template.HostPath = NodePayloadProtector.Decrypt(template.HostPath, context.NodeEncryptionKey.Span, Guid.Empty, template.Id, "host_path");
                break;
            case DevelopmentTemplateMaterialization materialization:
                materialization.TemplatePath =
                    NodePayloadProtector.Decrypt(materialization.TemplatePath, context.NodeEncryptionKey.Span, Guid.Empty, materialization.SelectedFolderId, "template_path");
                break;
            case AgentDefinition definition:
                definition.Instructions = NodePayloadProtector.Decrypt(definition.Instructions, context.NodeEncryptionKey.Span, Guid.Empty, definition.Id, "instructions");
                definition.Description = DecryptIfPresent(definition.Description, context.NodeEncryptionKey.Span, Guid.Empty, definition.Id, "description");
                definition.GenerationMetadataJson =
                    DecryptIfPresent(definition.GenerationMetadataJson, context.NodeEncryptionKey.Span, Guid.Empty, definition.Id, "generation_metadata_json");
                break;
            case CanvasWorkflow canvas:
                canvas.GraphJson = NodePayloadProtector.Decrypt(canvas.GraphJson, context.NodeEncryptionKey.Span, Guid.Empty, canvas.Id, "graph_json");
                break;
            case AgentSkill skill:
                skill.Description = NodePayloadProtector.Decrypt(skill.Description, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "description");
                skill.Body = NodePayloadProtector.Decrypt(skill.Body, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "body");
                skill.FrontmatterJson = DecryptIfPresent(skill.FrontmatterJson, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "frontmatter_json");
                skill.GenerationMetadataJson = DecryptIfPresent(skill.GenerationMetadataJson, context.NodeEncryptionKey.Span, Guid.Empty, skill.Id, "generation_metadata_json");
                break;
            case AgentSkillResource resource:
                // Skill id in the conversation slot and the resource name in the column name — see the matching block in
                // NodeEncryptionSaveChangesInterceptor. A row re-parented onto another skill, or renamed underneath its
                // ciphertext, fails the tag check here rather than reaching a model as that skill's content.
                resource.Content = NodePayloadProtector.Decrypt(resource.Content, context.NodeEncryptionKey.Span, resource.SkillId, resource.Id,
                    AgentSkillResource.ContentColumnName(resource.Name));
                break;
            case CustomTool customTool:
                customTool.Description = NodePayloadProtector.Decrypt(customTool.Description, context.NodeEncryptionKey.Span, Guid.Empty, customTool.Id, "description");
                customTool.ConfigJson = NodePayloadProtector.Decrypt(customTool.ConfigJson, context.NodeEncryptionKey.Span, Guid.Empty, customTool.Id, "custom_tool_config_json");
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
            case SlashCommand command:
                command.Description = DecryptIfPresent(command.Description, context.NodeEncryptionKey.Span, Guid.Empty, command.Id, SlashCommand.DescriptionColumnName(command.Name));
                command.ActionConfiguration = NodePayloadProtector.Decrypt(command.ActionConfiguration, context.NodeEncryptionKey.Span, Guid.Empty, command.Id,
                    SlashCommand.ActionConfigurationColumnName(command.Name));
                break;
            case McpServerApiKey apiKey:
                apiKey.KeyHash = NodePayloadProtector.Decrypt(apiKey.KeyHash, context.NodeEncryptionKey.Span, Guid.Empty, apiKey.Id, "mcp_api_key_hash");
                break;
            case LocalModelProxyApiKey proxyApiKey:
                proxyApiKey.KeyHash = NodePayloadProtector.Decrypt(proxyApiKey.KeyHash, context.NodeEncryptionKey.Span, Guid.Empty, proxyApiKey.Id, "local_model_proxy_api_key_hash");
                break;
            case IntegrationApiKey integrationApiKey:
                integrationApiKey.KeyHash =
                    NodePayloadProtector.Decrypt(integrationApiKey.KeyHash, context.NodeEncryptionKey.Span, Guid.Empty, integrationApiKey.Id, "integration_api_key_hash");
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
            case ConversationUploadedFile uploaded:
                uploaded.OriginalFileName = NodePayloadProtector.Decrypt(uploaded.OriginalFileName, context.NodeEncryptionKey.Span, uploaded.ConversationId, uploaded.FileId, "original_file_name");
                break;
            case ImageJob imageJob:
                imageJob.Prompt = NodePayloadProtector.Decrypt(imageJob.Prompt, context.NodeEncryptionKey.Span, Guid.Empty, imageJob.Id, "image_prompt");
                imageJob.NegativePrompt = DecryptIfPresent(imageJob.NegativePrompt, context.NodeEncryptionKey.Span, Guid.Empty, imageJob.Id, "image_negative_prompt");
                break;
            case DevelopmentProject project:
                project.Objective = NodePayloadProtector.Decrypt(project.Objective, context.NodeEncryptionKey.Span, project.Id, project.Id, "development_objective");
                break;
            case DevelopmentTask task:
                task.Title = NodePayloadProtector.Decrypt(task.Title, context.NodeEncryptionKey.Span, task.ProjectId, task.Id, "development_task_title");
                task.Requirements = NodePayloadProtector.Decrypt(task.Requirements, context.NodeEncryptionKey.Span, task.ProjectId, task.Id, "development_task_requirements");
                task.AcceptanceCriteriaJson =
                    NodePayloadProtector.Decrypt(task.AcceptanceCriteriaJson, context.NodeEncryptionKey.Span, task.ProjectId, task.Id, "development_acceptance_criteria_json");
                break;
            case DevelopmentArtifact artifact:
                artifact.ContentJson = DecryptIfPresent(artifact.ContentJson, context.NodeEncryptionKey.Span, artifact.ProjectId, artifact.Id, "development_artifact_content_json");
                artifact.InputArtifactIdsJson = DecryptIfPresent(artifact.InputArtifactIdsJson, context.NodeEncryptionKey.Span, artifact.ProjectId, artifact.Id, "development_artifact_input_ids_json");
                break;
            case DevelopmentEvent developmentEvent:
                developmentEvent.DetailJson = DecryptIfPresent(developmentEvent.DetailJson, context.NodeEncryptionKey.Span, developmentEvent.ProjectId, developmentEvent.Id,
                    "development_event_detail_json");
                developmentEvent.ResultMetadataJson = DecryptIfPresent(developmentEvent.ResultMetadataJson, context.NodeEncryptionKey.Span, developmentEvent.ProjectId, developmentEvent.Id,
                    "development_event_result_json");
                break;
            case BenchmarkProject benchmarkProject:
                benchmarkProject.CoreTaskJson = NodePayloadProtector.Decrypt(benchmarkProject.CoreTaskJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    benchmarkProject.Id,
                    "benchmark_core_task_json");
                break;
            case BenchmarkTaskItem taskItem:
                taskItem.PromptJson = NodePayloadProtector.Decrypt(taskItem.PromptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    taskItem.Id,
                    "benchmark_task_item_prompt_json");
                taskItem.ReferenceAnswerJson = DecryptIfPresent(taskItem.ReferenceAnswerJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    taskItem.Id,
                    "benchmark_task_item_reference_json");
                taskItem.VerifierConfigJson = DecryptIfPresent(taskItem.VerifierConfigJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    taskItem.Id,
                    "benchmark_task_item_verifier_json");
                taskItem.GeneratorConfigJson = DecryptIfPresent(taskItem.GeneratorConfigJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    taskItem.Id,
                    "benchmark_task_item_generator_json");
                break;
            case BenchmarkRun benchmarkRun:
                benchmarkRun.RuntimeSnapshotJson = NodePayloadProtector.Decrypt(benchmarkRun.RuntimeSnapshotJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    benchmarkRun.Id,
                    "benchmark_runtime_snapshot_json");
                benchmarkRun.OutputPartsJson = DecryptIfPresent(benchmarkRun.OutputPartsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    benchmarkRun.Id,
                    "benchmark_output_parts_json");
                benchmarkRun.PrimaryLaunchReceiptJson = DecryptIfPresent(benchmarkRun.PrimaryLaunchReceiptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    benchmarkRun.Id,
                    "benchmark_primary_launch_receipt_json");
                benchmarkRun.PrimaryEnvironmentFactsJson = DecryptIfPresent(benchmarkRun.PrimaryEnvironmentFactsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    benchmarkRun.Id,
                    "benchmark_primary_environment_facts_json");
                break;
            case BenchmarkJudgePolicyRevision policyRevision:
                policyRevision.PolicyJson = NodePayloadProtector.Decrypt(policyRevision.PolicyJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    policyRevision.Id,
                    "benchmark_judge_policy_json");
                break;
            case BenchmarkJudgeAttempt judgeAttempt:
                judgeAttempt.JudgeRuntimeJson = DecryptIfPresent(judgeAttempt.JudgeRuntimeJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    judgeAttempt.Id,
                    "benchmark_judge_runtime_json");
                judgeAttempt.ResultJson = DecryptIfPresent(judgeAttempt.ResultJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    judgeAttempt.Id,
                    "benchmark_judge_attempt_result_json");
                judgeAttempt.LaunchReceiptJson = DecryptIfPresent(judgeAttempt.LaunchReceiptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    judgeAttempt.Id,
                    "benchmark_judge_attempt_launch_receipt_json");
                judgeAttempt.EnvironmentFactsJson = DecryptIfPresent(judgeAttempt.EnvironmentFactsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    judgeAttempt.Id,
                    "benchmark_judge_attempt_environment_facts_json");
                break;
            case BenchmarkFidelityAttempt fidelityAttempt:
                fidelityAttempt.ReceiptJson = DecryptIfPresent(fidelityAttempt.ReceiptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    fidelityAttempt.Id,
                    "benchmark_fidelity_receipt_json");
                break;
            case BenchmarkJudgeComparison comparison:
                comparison.JudgeRuntimeJson = DecryptIfPresent(comparison.JudgeRuntimeJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    comparison.Id,
                    "benchmark_comparison_runtime_json");
                comparison.ResultJson = DecryptIfPresent(comparison.ResultJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    comparison.Id,
                    "benchmark_comparison_result_json");
                comparison.LaunchReceiptJson = DecryptIfPresent(comparison.LaunchReceiptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    comparison.Id,
                    "benchmark_comparison_launch_receipt_json");
                comparison.EnvironmentFactsJson = DecryptIfPresent(comparison.EnvironmentFactsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    comparison.Id,
                    "benchmark_comparison_environment_facts_json");
                break;
            case TrainingDatasetDefinition datasetDefinition:
                datasetDefinition.DefinitionJson = NodePayloadProtector.Decrypt(datasetDefinition.DefinitionJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    datasetDefinition.Id,
                    "training_definition_json");
                break;
            case TrainingDataset dataset:
                dataset.DefinitionJson = DecryptIfPresent(dataset.DefinitionJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    dataset.Id,
                    "training_dataset_definition_json");
                break;
            case TrainingDatasetSample sample:
                // Dataset id in the conversation slot — see the matching block in NodeEncryptionSaveChangesInterceptor.
                // A sample re-parented onto another dataset fails the tag check here rather than reaching a training run.
                sample.ContentJson = NodePayloadProtector.Decrypt(sample.ContentJson,
                    context.NodeEncryptionKey.Span,
                    sample.DatasetId,
                    sample.Id,
                    "training_sample_content_json");
                sample.ValidationJson = DecryptIfPresent(sample.ValidationJson,
                    context.NodeEncryptionKey.Span,
                    sample.DatasetId,
                    sample.Id,
                    "training_sample_validation_json");
                break;
            case ToolMockDefinition toolMock:
                toolMock.MockJson = NodePayloadProtector.Decrypt(toolMock.MockJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    toolMock.Id,
                    "tool_mock_json");
                toolMock.VerificationJson = DecryptIfPresent(toolMock.VerificationJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    toolMock.Id,
                    "tool_mock_verification_json");
                break;
            case TrainingBaseArtifact baseArtifact:
                baseArtifact.FilesJson = NodePayloadProtector.Decrypt(baseArtifact.FilesJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    baseArtifact.Id,
                    "training_base_files_json");
                baseArtifact.LicenseJson = DecryptIfPresent(baseArtifact.LicenseJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    baseArtifact.Id,
                    "training_base_license_json");
                break;
            case TrainingRun run:
                run.FreezeJson = NodePayloadProtector.Decrypt(run.FreezeJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_freeze_json");
                run.OptionsJson = NodePayloadProtector.Decrypt(run.OptionsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_options_json");
                run.LicenseConfirmationJson = DecryptIfPresent(run.LicenseConfirmationJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_license_confirmation_json");
                run.ProgressJson = DecryptIfPresent(run.ProgressJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_progress_json");
                run.LogTail = DecryptIfPresent(run.LogTail,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_log_tail");
                run.LaunchReceiptJson = DecryptIfPresent(run.LaunchReceiptJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    run.Id,
                    "training_run_launch_receipt_json");
                break;
            case TrainingEvaluationRun evaluation:
                evaluation.MembershipJson = NodePayloadProtector.Decrypt(evaluation.MembershipJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    evaluation.Id,
                    "training_evaluation_membership_json");
                evaluation.ResultsJson = DecryptIfPresent(evaluation.ResultsJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    evaluation.Id,
                    "training_evaluation_results_json");
                evaluation.ExecutionProvenanceJson = DecryptIfPresent(evaluation.ExecutionProvenanceJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    evaluation.Id,
                    "training_evaluation_execution_provenance_json");
                break;
            case TrainingComparisonReport comparison:
                comparison.DeltasJson = NodePayloadProtector.Decrypt(comparison.DeltasJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    comparison.Id,
                    "training_comparison_deltas_json");
                break;
            case TrainingArtifact artifact:
                artifact.QualityDecisionJson = DecryptIfPresent(artifact.QualityDecisionJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    artifact.Id,
                    "training_artifact_quality_decision_json");
                break;
            case AgentWorkSession workSession:
                workSession.Objective = NodePayloadProtector.Decrypt(workSession.Objective, context.NodeEncryptionKey.Span, workSession.Id, workSession.Id, "work_session_objective");
                break;
            case AgentWorkSessionTask workSessionTask:
                // The owning session id sits in the conversation slot, so a task row re-parented onto another session
                // fails the tag check here rather than surfacing in that session's plan.
                workSessionTask.Title = NodePayloadProtector.Decrypt(workSessionTask.Title,
                    context.NodeEncryptionKey.Span,
                    workSessionTask.SessionId,
                    workSessionTask.Id,
                    "work_session_task_title");
                workSessionTask.Detail = DecryptIfPresent(workSessionTask.Detail,
                    context.NodeEncryptionKey.Span,
                    workSessionTask.SessionId,
                    workSessionTask.Id,
                    "work_session_task_detail");
                workSessionTask.BlockedReason = DecryptIfPresent(workSessionTask.BlockedReason,
                    context.NodeEncryptionKey.Span,
                    workSessionTask.SessionId,
                    workSessionTask.Id,
                    "work_session_task_blocked_reason");
                break;
            case AgentWorkSessionFinding workSessionFinding:
                workSessionFinding.Text = NodePayloadProtector.Decrypt(workSessionFinding.Text,
                    context.NodeEncryptionKey.Span,
                    workSessionFinding.SessionId,
                    workSessionFinding.Id,
                    "work_session_finding_text");
                workSessionFinding.SourceRef = DecryptIfPresent(workSessionFinding.SourceRef,
                    context.NodeEncryptionKey.Span,
                    workSessionFinding.SessionId,
                    workSessionFinding.Id,
                    "work_session_finding_source_ref");
                break;
            case AgentWorkSessionCheckpoint workSessionCheckpoint:
                workSessionCheckpoint.Summary = DecryptIfPresent(workSessionCheckpoint.Summary,
                    context.NodeEncryptionKey.Span,
                    workSessionCheckpoint.SessionId,
                    workSessionCheckpoint.Id,
                    "work_session_checkpoint_summary");
                workSessionCheckpoint.StateJson = NodePayloadProtector.Decrypt(workSessionCheckpoint.StateJson,
                    context.NodeEncryptionKey.Span,
                    workSessionCheckpoint.SessionId,
                    workSessionCheckpoint.Id,
                    "work_session_checkpoint_state_json");
                break;
            case AgentWorkSessionEvent workSessionEvent:
                workSessionEvent.DetailJson = DecryptIfPresent(workSessionEvent.DetailJson,
                    context.NodeEncryptionKey.Span,
                    workSessionEvent.SessionId,
                    workSessionEvent.Id,
                    "work_session_event_detail_json");
                break;
            case DevWorkflowWorkItem workItem:
                workItem.Request = NodePayloadProtector.Decrypt(workItem.Request, context.NodeEncryptionKey.Span, workItem.Id, workItem.Id, "dev_workflow_work_item_request");
                break;
            case DevWorkflowDefinition definitionGraph:
                definitionGraph.GraphJson = NodePayloadProtector.Decrypt(definitionGraph.GraphJson,
                    context.NodeEncryptionKey.Span,
                    Guid.Empty,
                    definitionGraph.Id,
                    "dev_workflow_definition_graph_json");
                break;
            case DevWorkflowRuleSet ruleSet:
                ruleSet.Body = NodePayloadProtector.Decrypt(ruleSet.Body, context.NodeEncryptionKey.Span, Guid.Empty, ruleSet.Id, "dev_workflow_rule_set_body");
                break;
            case DevWorkflowRun workflowRun:
                // The owning work item sits in the conversation slot, so a run row re-parented onto another work item
                // fails the tag check here rather than reading back as that work item's executed graph.
                workflowRun.GraphJson = NodePayloadProtector.Decrypt(workflowRun.GraphJson,
                    context.NodeEncryptionKey.Span,
                    workflowRun.WorkItemId,
                    workflowRun.Id,
                    "dev_workflow_run_graph_json");
                break;
            case DevWorkflowNodeRun nodeRun:
                nodeRun.InputJson = DecryptIfPresent(nodeRun.InputJson, context.NodeEncryptionKey.Span, nodeRun.RunId, nodeRun.Id, "dev_workflow_node_run_input_json");
                nodeRun.OutputJson = DecryptIfPresent(nodeRun.OutputJson, context.NodeEncryptionKey.Span, nodeRun.RunId, nodeRun.Id, "dev_workflow_node_run_output_json");
                nodeRun.PolicyResolutionJson =
                    DecryptIfPresent(nodeRun.PolicyResolutionJson, context.NodeEncryptionKey.Span, nodeRun.RunId, nodeRun.Id, "dev_workflow_node_run_policy_json");
                break;
            case DevWorkflowDecision decision:
                decision.Comment = DecryptIfPresent(decision.Comment, context.NodeEncryptionKey.Span, decision.RunId, decision.Id, "dev_workflow_decision_comment");
                decision.PayloadJson = DecryptIfPresent(decision.PayloadJson, context.NodeEncryptionKey.Span, decision.RunId, decision.Id, "dev_workflow_decision_payload_json");
                break;
            case DevWorkflowRunEvent workflowEvent:
                workflowEvent.DetailJson =
                    DecryptIfPresent(workflowEvent.DetailJson, context.NodeEncryptionKey.Span, workflowEvent.RunId, workflowEvent.Id, "dev_workflow_run_event_detail_json");
                break;
            case IntegrationExecutionEvent executionEvent:
                executionEvent.DetailJson = DecryptIfPresent(executionEvent.DetailJson,
                    context.NodeEncryptionKey.Span,
                    executionEvent.ExecutionId,
                    executionEvent.Id,
                    "integration_execution_event_detail_json");
                break;
        }

        return entity;
    }

    private static byte[]? DecryptIfPresent(byte[]? payload, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName)
    {
        return payload is null ? null : NodePayloadProtector.Decrypt(payload, key, conversationId, recordId, columnName);
    }
}
