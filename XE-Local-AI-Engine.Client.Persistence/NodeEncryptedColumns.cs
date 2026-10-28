namespace XE_Local_AI_Engine.Client.Persistence;

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Every interceptor-encrypted column with its AAD identity, the one registration both encryption interceptors walk. A
///     required column always encrypts; an optional one only when present.
/// </summary>
/// <remarks>
///     Three AAD conventions run through the registrations. A node-scoped row binds <c>Guid.Empty</c> in the conversation slot
///     plus its own id and the column name; a child row binds its OWNER's id in the conversation slot, so a database writer who
///     re-parents the row fails the tag check instead of reading it back as the new owner's; and every encrypted column of a row
///     gets a distinct AAD column name, so no blob is presentable as another column's. See <see cref="NodePayloadProtector" />.
/// </remarks>
internal static class NodeEncryptedColumns
{
    /// <summary>In save order: a save encrypts every tracked entry of one entity type before the next type.</summary>
    internal static readonly EncryptedEntity[] Entities =
    [
        // Conversation-scoped: the conversation's own id fills both AAD slots. Title is optional — null until the first user message is received.
        Entity<NodeConversation>(entity => entity.ConversationId, entity => entity.ConversationId)
            .Optional(entity => entity.Title, "title"),

        // The only encrypted columns with legacy plaintext rows on disk, so they carry the versioned read-both envelope (NodeChatContentProtection) instead of
        // the bare protector — which is what keeps EF-tracked saves and reads byte-compatible with the raw-ADO persistence path.
        Entity<NodeMessage>(entity => entity.ConversationId, entity => entity.MessageId)
            .Required(entity => entity.Content, "content", ColumnCipher.ReadBothEnvelope)
            .Optional(entity => entity.MetadataJson, "metadata_json", ColumnCipher.ReadBothEnvelope),

        Entity<NodeToolEvent>(entity => entity.ConversationId, entity => entity.ToolCallId)
            .Optional(entity => entity.PlaintextArgs, "plaintext_args")
            .Optional(entity => entity.PlaintextResult, "plaintext_result"),

        // Selected folders are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // folder's own id plus the column name.
        NodeScoped<NodeSelectedFolder>(entity => entity.Id)
            .Required(entity => entity.HostPath, "host_path"),

        // Development templates and the provenance of repositories materialized from them both carry host paths, so they get exactly the selected-folder
        // treatment: node-scoped AAD.
        NodeScoped<DevelopmentTemplate>(entity => entity.Id)
            .Required(entity => entity.HostPath, "host_path"),

        NodeScoped<DevelopmentTemplateMaterialization>(entity => entity.SelectedFolderId)
            .Required(entity => entity.TemplatePath, "template_path"),

        // Agent definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the definition's own id plus the column name — same layout as selected folders.
        NodeScoped<AgentDefinition>(entity => entity.Id)
            .Required(entity => entity.Instructions, "instructions")
            .Optional(entity => entity.Description, "description")
            .Optional(entity => entity.GenerationMetadataJson, "generation_metadata_json"),

        // Node-scoped, same layout as agent definitions. Both the description and the SKILL.md body are required encrypted columns.
        NodeScoped<AgentSkill>(entity => entity.Id)
            .Required(entity => entity.Description, "description")
            .Required(entity => entity.Body, "body")
            .Optional(entity => entity.FrontmatterJson, "frontmatter_json")
            .Optional(entity => entity.GenerationMetadataJson, "generation_metadata_json"),

        // With only the row id bound, a database WRITER (not a reader) could point a resource row at another skill and inject its content into a different
        // agent's context without forging a tag, so the skill id takes the conversation slot and the name the column name. Renames go delete-and-reinsert.
        Entity<AgentSkillResource>(entity => entity.SkillId, entity => entity.Id)
            .Required(entity => entity.Content, entity => AgentSkillResource.ContentColumnName(entity.Name)),

        // Node-scoped, same layout as agent skills. Description is required; so is the kind-specific config, which carries the secret header/env values and
        // takes a distinct AAD column name so a config blob can never be substituted for a description blob.
        NodeScoped<CustomTool>(entity => entity.Id)
            .Required(entity => entity.Description, "description")
            .Required(entity => entity.ConfigJson, "custom_tool_config_json"),

        // Node-scoped, same layout as agent definitions. Behavior is required; the optional trigger condition only encrypts when present.
        NodeScoped<PlaybookAction>(entity => entity.Id)
            .Required(entity => entity.Behavior, "behavior")
            .Optional(entity => entity.TriggerCondition, "trigger_condition"),

        // Node-scoped, same layout as playbook actions. InputTurns is required; the optional assertion/rubric only encrypt when present.
        NodeScoped<GoldenConversation>(entity => entity.Id)
            .Required(entity => entity.InputTurns, "input_turns")
            .Optional(entity => entity.Assertion, "assertion")
            .Optional(entity => entity.Rubric, "rubric"),

        // MCP server registrations are node-scoped, so the AAD binds the empty conversation id to the registration's
        // own id plus the column name. The secret-bearing columns (args, env, headers, description) are all optional.
        NodeScoped<McpServerRegistration>(entity => entity.Id)
            .Optional(entity => entity.ArgumentsJson, "arguments")
            .Optional(entity => entity.EnvJson, "env")
            .Optional(entity => entity.HeadersJson, "headers")
            .Optional(entity => entity.Description, "description"),

        NodeScoped<SlashCommand>(entity => entity.Id)
            .Optional(entity => entity.Description, entity => SlashCommand.DescriptionColumnName(entity.Name))
            .Required(entity => entity.ActionConfiguration, entity => SlashCommand.ActionConfigurationColumnName(entity.Name)),

        // Node-scoped singleton row. The stored value is a one-way SHA-256 digest, not the key, so the AAD-bound AEAD protects no secret from a reader: it stops
        // a database-file WRITER dropping in the hash of a key they chose and authenticating against the agent-execution surface. Required, so it always encrypts.
        NodeScoped<McpServerApiKey>(entity => entity.Id)
            .Required(entity => entity.KeyHash, "mcp_api_key_hash"),

        // The inbound model-proxy bearer credential on the same terms as the MCP key above: a WRITER who substituted the hash of a key they chose would take over
        // the model-proxy surface. Required, so it always encrypts, and a distinct AAD column name binds it to this table.
        NodeScoped<LocalModelProxyApiKey>(entity => entity.Id)
            .Required(entity => entity.KeyHash, "local_model_proxy_api_key_hash"),

        // The external-integration bearer credentials on the same terms as the two singleton key rows above: a WRITER who substituted their own digest would invoke
        // every trigger on the node. Required, own AAD column name; unlike those two, a node holds MANY of these rows, so the record id is the row's own id.
        NodeScoped<IntegrationApiKey>(entity => entity.Id)
            .Required(entity => entity.KeyHash, "integration_api_key_hash"),

        // Node-scoped. Only the opaque job parameters are encrypted, and they are optional, so they encrypt only when present.
        NodeScoped<ScheduledJobDefinition>(entity => entity.Id)
            .Optional(entity => entity.ParameterJson, "parameter_json"),

        // Scheduled job runs are node-scoped, so the AAD binds the empty conversation id to the run's own id plus the
        // column name. Only the structured run detail is encrypted; it is optional.
        NodeScoped<ScheduledJobRun>(entity => entity.Id)
            .Optional(entity => entity.DetailsJson, "details_json"),

        // Scheduled job run events are node-scoped, so the AAD binds the empty conversation id to the event's own id
        // plus the column name. Only the structured event payload is encrypted; it is optional.
        NodeScoped<ScheduledJobRunEvent>(entity => entity.Id)
            .Optional(entity => entity.DataJson, "data_json"),

        // Model-fit snapshots are node-scoped, so the AAD binds the empty conversation id to the snapshot's own id plus
        // the column name. The raw utility output, stderr excerpt and detailed diagnostics are sensitive and optional.
        NodeScoped<ModelFitSnapshot>(entity => entity.Id)
            .Optional(entity => entity.RawJson, "raw_json")
            .Optional(entity => entity.StderrExcerpt, "stderr_excerpt")
            .Optional(entity => entity.DiagnosticsJson, "diagnostics_json"),

        // Node-scoped. Distinct AAD column names (bench_*) avoid cross-entity collision with the snapshot columns; the raw benchmark output and diagnostics are
        // sensitive and optional.
        NodeScoped<ModelFitBenchmark>(entity => entity.Id)
            .Optional(entity => entity.RawJson, "bench_raw_json")
            .Optional(entity => entity.DiagnosticsJson, "bench_diagnostics_json"),

        // Conversation-scoped, same layout as conversation titles. Only the display name is encrypted — the durable bytes and extracted text are encrypted on disk
        // by the file store. NodeChatDbContext.EncryptUploadedFileName gives the raw-SQL write path the identical protector and AAD, so the two are interchangeable.
        Entity<ConversationUploadedFile>(entity => entity.ConversationId, entity => entity.FileId)
            .Required(entity => entity.OriginalFileName, "original_file_name"),

        // Node-scoped, same layout as agent definitions. The prompt is required, the negative prompt encrypts only when present, and distinct AAD column names
        // (image_*) avoid cross-entity collision.
        NodeScoped<ImageJob>(entity => entity.Id)
            .Required(entity => entity.Prompt, "image_prompt")
            .Optional(entity => entity.NegativePrompt, "image_negative_prompt"),

        Entity<DevelopmentProject>(entity => entity.Id, entity => entity.Id)
            .Required(entity => entity.Objective, "development_objective"),

        Entity<DevelopmentTask>(entity => entity.ProjectId, entity => entity.Id)
            .Required(entity => entity.Title, "development_task_title")
            .Required(entity => entity.Requirements, "development_task_requirements")
            .Required(entity => entity.AcceptanceCriteriaJson, "development_acceptance_criteria_json"),

        Entity<DevelopmentArtifact>(entity => entity.ProjectId, entity => entity.Id)
            .Optional(entity => entity.ContentJson, "development_artifact_content_json")
            .Optional(entity => entity.InputArtifactIdsJson, "development_artifact_input_ids_json"),

        Entity<DevelopmentEvent>(entity => entity.ProjectId, entity => entity.Id)
            .Optional(entity => entity.DetailJson, "development_event_detail_json")
            .Optional(entity => entity.ResultMetadataJson, "development_event_result_json"),

        NodeScoped<BenchmarkProject>(entity => entity.Id)
            .Required(entity => entity.CoreTaskJson, "benchmark_core_task_json"),

        // A distinct AAD column name per payload, as everywhere else: a verifier config carries expected answers and
        // a generator config carries the parameters that produce them, so neither may be presentable as the prompt.
        NodeScoped<BenchmarkTaskItem>(entity => entity.Id)
            .Required(entity => entity.PromptJson, "benchmark_task_item_prompt_json")
            .Optional(entity => entity.ReferenceAnswerJson, "benchmark_task_item_reference_json")
            .Optional(entity => entity.VerifierConfigJson, "benchmark_task_item_verifier_json")
            .Optional(entity => entity.GeneratorConfigJson, "benchmark_task_item_generator_json"),

        // Review notes are free text that may quote the answer; the scores beside them are plaintext numbers.
        NodeScoped<BenchmarkRunReview>(entity => entity.Id)
            .Optional(entity => entity.NotesJson, "benchmark_run_review_notes_json"),

        // The primary launch-evidence block takes a distinct AAD column name per column so a writer cannot present an
        // environment capture as a receipt; the judge's own evidence lives on its attempt row.
        NodeScoped<BenchmarkRun>(entity => entity.Id)
            .Required(entity => entity.RuntimeSnapshotJson, "benchmark_runtime_snapshot_json")
            .Optional(entity => entity.OutputPartsJson, "benchmark_output_parts_json")
            .Optional(entity => entity.PrimaryLaunchReceiptJson, "benchmark_primary_launch_receipt_json")
            .Optional(entity => entity.PrimaryEnvironmentFactsJson, "benchmark_primary_environment_facts_json"),

        NodeScoped<BenchmarkJudgePolicyRevision>(entity => entity.Id)
            .Required(entity => entity.PolicyJson, "benchmark_judge_policy_json"),

        // Every attempt column is optional: an attempt inserted directly as Failed carries no runtime, and an attempt
        // that never reached readiness carries no receipt. The optional path never dereferences a missing value.
        NodeScoped<BenchmarkJudgeAttempt>(entity => entity.Id)
            .Optional(entity => entity.JudgeRuntimeJson, "benchmark_judge_runtime_json")
            .Optional(entity => entity.ResultJson, "benchmark_judge_attempt_result_json")
            .Optional(entity => entity.LaunchReceiptJson, "benchmark_judge_attempt_launch_receipt_json")
            .Optional(entity => entity.EnvironmentFactsJson, "benchmark_judge_attempt_environment_facts_json"),

        // The fidelity receipt is encrypted for the same reason the launch receipts are: its argv carries host filesystem paths. The measured numbers themselves
        // stay plaintext on the attempt, so the listing sorts without decrypting.
        NodeScoped<BenchmarkFidelityAttempt>(entity => entity.Id)
            .Optional(entity => entity.ReceiptJson, "benchmark_fidelity_receipt_json"),

        // A distinct AAD column name per column, as on the judge attempt: a writer cannot present an environment capture as a receipt, or a rationale as a runtime.
        // The verdict token stays plaintext — it is the rankable signal, and ranking is a SQL sort.
        NodeScoped<BenchmarkJudgeComparison>(entity => entity.Id)
            .Optional(entity => entity.JudgeRuntimeJson, "benchmark_comparison_runtime_json")
            .Optional(entity => entity.ResultJson, "benchmark_comparison_result_json")
            .Optional(entity => entity.LaunchReceiptJson, "benchmark_comparison_launch_receipt_json")
            .Optional(entity => entity.EnvironmentFactsJson, "benchmark_comparison_environment_facts_json"),

        // Training dataset definitions are node-scoped, so the AAD binds the empty conversation id to the definition's
        // own id plus the column name — same layout as benchmark projects.
        NodeScoped<TrainingDatasetDefinition>(entity => entity.Id)
            .Required(entity => entity.DefinitionJson, "training_definition_json"),

        // The dataset's pinned copy of that body. A distinct AAD column name from the definition's own, so a writer
        // cannot present a definition row's ciphertext as a dataset's pinned snapshot (or the reverse).
        NodeScoped<TrainingDataset>(entity => entity.Id)
            .Optional(entity => entity.DefinitionJson, "training_dataset_definition_json"),

        // The skill-resource treatment rather than the flat one: the owning dataset id goes in the conversation slot, so a database WRITER cannot re-parent a sample
        // row onto another dataset and have its content and verdicts fed into a different training run.
        Entity<TrainingDatasetSample>(entity => entity.DatasetId, entity => entity.Id)
            .Required(entity => entity.ContentJson, "training_sample_content_json")
            .Optional(entity => entity.ValidationJson, "training_sample_validation_json"),

        // Node-scoped. The mock body and the verifier's verdict carry distinct AAD column names so a mock blob can never be substituted for a verdict blob — the
        // same separation custom tools draw between their description and their config.
        NodeScoped<ToolMockDefinition>(entity => entity.Id)
            .Required(entity => entity.MockJson, "tool_mock_json")
            .Optional(entity => entity.VerificationJson, "tool_mock_verification_json"),

        // Base artifacts are node-scoped. The file manifest carries host paths and per-file digests; the license blob is
        // what the mandatory gate presents, so a writer must not be able to swap one for the other.
        NodeScoped<TrainingBaseArtifact>(entity => entity.Id)
            .Required(entity => entity.FilesJson, "training_base_files_json")
            .Optional(entity => entity.LicenseJson, "training_base_license_json"),

        // Node-scoped, with a distinct AAD column name per column: the freeze, the options and the license confirmation are the three documents an audit reads
        // back, and a writer must not be able to swap one for another.
        NodeScoped<TrainingRun>(entity => entity.Id)
            .Required(entity => entity.FreezeJson, "training_run_freeze_json")
            .Required(entity => entity.OptionsJson, "training_run_options_json")
            .Optional(entity => entity.LicenseConfirmationJson, "training_run_license_confirmation_json")
            .Optional(entity => entity.ProgressJson, "training_run_progress_json")
            .Optional(entity => entity.LogTail, "training_run_log_tail")
            .Optional(entity => entity.LaunchReceiptJson, "training_run_launch_receipt_json"),

        // Evaluations and comparison reports are node-scoped, same flat layout as runs. Membership and verdicts carry distinct AAD column names: membership is what
        // makes a comparison's two sides comparable, and the verdicts are what the deltas are computed from.
        NodeScoped<TrainingEvaluationRun>(entity => entity.Id)
            .Required(entity => entity.MembershipJson, "training_evaluation_membership_json")
            .Optional(entity => entity.ResultsJson, "training_evaluation_results_json")
            .Optional(entity => entity.ExecutionProvenanceJson, "training_evaluation_execution_provenance_json"),

        NodeScoped<TrainingComparisonReport>(entity => entity.Id)
            .Required(entity => entity.DeltasJson, "training_comparison_deltas_json"),

        NodeScoped<TrainingArtifact>(entity => entity.Id)
            .Optional(entity => entity.QualityDecisionJson, "training_artifact_quality_decision_json"),

        // The Development layout exactly: the session's own id fills both AAD slots on the session row, and the owning session id the conversation slot on every
        // child row, so a writer cannot re-parent a task, finding, checkpoint or event onto another session and have its text read back as that session's work.
        Entity<AgentWorkSession>(entity => entity.Id, entity => entity.Id)
            .Required(entity => entity.Objective, "work_session_objective"),

        Entity<AgentWorkSessionTask>(entity => entity.SessionId, entity => entity.Id)
            .Required(entity => entity.Title, "work_session_task_title")
            .Optional(entity => entity.Detail, "work_session_task_detail")
            .Optional(entity => entity.BlockedReason, "work_session_task_blocked_reason"),

        Entity<AgentWorkSessionFinding>(entity => entity.SessionId, entity => entity.Id)
            .Required(entity => entity.Text, "work_session_finding_text")
            .Optional(entity => entity.SourceRef, "work_session_finding_source_ref"),

        Entity<AgentWorkSessionCheckpoint>(entity => entity.SessionId, entity => entity.Id)
            .Optional(entity => entity.Summary, "work_session_checkpoint_summary")
            .Required(entity => entity.StateJson, "work_session_checkpoint_state_json"),

        Entity<AgentWorkSessionEvent>(entity => entity.SessionId, entity => entity.Id)
            .Optional(entity => entity.DetailJson, "work_session_event_detail_json"),

        // The work-session layout: a root row's own id fills both AAD slots, and the owning id the conversation slot on every child row, so a writer cannot
        // re-parent a run onto another work item, or a node-run onto another run, and have the blob read back as that owner's.
        Entity<DevWorkflowWorkItem>(entity => entity.Id, entity => entity.Id)
            .Required(entity => entity.Request, "dev_workflow_work_item_request"),

        // The definition is node-scoped, so the empty conversation id plus its own id — but under an AAD column name
        // distinct from the run's pinned copy below, so neither ciphertext is presentable as the other.
        NodeScoped<DevWorkflowDefinition>(entity => entity.Id)
            .Required(entity => entity.GraphJson, "dev_workflow_definition_graph_json"),

        // A rule set is node-scoped like the definition, so the empty conversation id plus its own id — and its own AAD
        // column name, so a rule body can never be read back as a definition's graph.
        NodeScoped<DevWorkflowRuleSet>(entity => entity.Id)
            .Required(entity => entity.Body, "dev_workflow_rule_set_body"),

        // The owning work item sits in the conversation slot, so a run row re-parented onto another work item
        // fails the tag check rather than reading back as that work item's executed graph.
        Entity<DevWorkflowRun>(entity => entity.WorkItemId, entity => entity.Id)
            .Required(entity => entity.GraphJson, "dev_workflow_run_graph_json"),

        // Three distinct AAD column names on one row, and not cosmetically: a Gate node's decision is a function of output_json, so if these shared a name a
        // database writer could swap a policy or input blob into the output column and flip a gate without forging a ciphertext or a tag.
        Entity<DevWorkflowNodeRun>(entity => entity.RunId, entity => entity.Id)
            .Optional(entity => entity.InputJson, "dev_workflow_node_run_input_json")
            .Optional(entity => entity.OutputJson, "dev_workflow_node_run_output_json")
            .Optional(entity => entity.PolicyResolutionJson, "dev_workflow_node_run_policy_json"),

        // The payload is the structured, machine-consumed half of a decision; the comment is free text. Distinct names
        // so the one that a runtime acts on can never be substituted from the one a human typed.
        Entity<DevWorkflowDecision>(entity => entity.RunId, entity => entity.Id)
            .Optional(entity => entity.Comment, "dev_workflow_decision_comment")
            .Optional(entity => entity.PayloadJson, "dev_workflow_decision_payload_json"),

        Entity<DevWorkflowRunEvent>(entity => entity.RunId, entity => entity.Id)
            .Optional(entity => entity.DetailJson, "dev_workflow_run_event_detail_json"),

        // The one column in the integration family that holds real content: an external.output event carries the tool's payload verbatim, under the owning
        // execution's id. Optional — phase events carry no detail, which is what lets the raw-ADO accept path skip encryption entirely.
        Entity<IntegrationExecutionEvent>(entity => entity.ExecutionId, entity => entity.Id)
            .Optional(entity => entity.DetailJson, "integration_execution_event_detail_json"),

        // Graph Workflows take the same layout: the definition is node-scoped, the run binds its DEFINITION, and node runs and events bind their run, so a
        // re-parented row fails authenticated decryption rather than reading back as another owner's.
        NodeScoped<GraphWorkflowDefinition>(entity => entity.Id)
            .Required(entity => entity.GraphJson, "graph_workflow_definition_graph_json"),

        Entity<GraphWorkflowRun>(entity => entity.DefinitionId, entity => entity.Id)
            .Required(entity => entity.GraphJson, "graph_workflow_run_graph_json")
            .Optional(entity => entity.InputJson, "graph_workflow_run_input_json")
            .Optional(entity => entity.OutputJson, "graph_workflow_run_output_json")
            .Optional(entity => entity.PreviousJson, "graph_workflow_run_previous_json"),

        // Five distinct AAD column names on one row, and not cosmetically: an edge condition routes on output_json, so if these shared a name a database writer
        // could swap an input, an error or a decider into the output column and reroute a run without forging a ciphertext or a tag.
        Entity<GraphWorkflowNodeRun>(entity => entity.RunId, entity => entity.Id)
            .Optional(entity => entity.InputJson, "graph_workflow_node_run_input_json")
            .Optional(entity => entity.OutputJson, "graph_workflow_node_run_output_json")
            .Optional(entity => entity.Error, "graph_workflow_node_run_error")
            .Optional(entity => entity.DecidedBySubject, "graph_workflow_node_run_decided_by")
            .Optional(entity => entity.SteeringJson, "graph_workflow_node_run_steering_json"),

        Entity<GraphWorkflowRunEvent>(entity => entity.RunId, entity => entity.Id)
            .Optional(entity => entity.DetailJson, "graph_workflow_run_event_detail_json"),

        // An external app's configured variables are the user's own credentials — a database password, an API key — and the one encrypted column in that family.
        // The instance's own id fills BOTH AAD slots, so a copied row cannot hand another instance its secrets. Required: no declared variables stores an empty object.
        Entity<ExternalAppInstance>(entity => entity.Id, entity => entity.Id)
            .Required(entity => entity.VariablesJson, "external_app_instance_variables_json")
            // Optional, not required, for one row shape only: an instance installed before the bridge existed
            // carries no token. Every row this engine writes carries one, minted at install.
            .Optional(entity => entity.BridgeToken, "external_app_instance_bridge_token"),

        // The two dev-workflow artifact tables are absent on purpose: the bytes live on disk under the blob store's own AAD
        // column (dev_workflow_artifact_blob), and every column that stays in the row is structural.

        // Session-scoped: the session's own id fills both AAD slots, the same self-consistent layout a conversation title uses. The config blob is required (a
        // session with no options stores an empty object); title and the error pair only encrypt when present.
        Entity<TranscriptionSession>(entity => entity.Id, entity => entity.Id)
            .Optional(entity => entity.Title, "transcription_session_title")
            .Required(entity => entity.ConfigJson, "transcription_session_config_json")
            .Optional(entity => entity.ErrorCode, "transcription_session_error_code")
            .Optional(entity => entity.ErrorMessage, "transcription_session_error_message"),

        // A transcript row binds (sessionId, segmentId), so a ciphertext moved to another row — or to another session —
        // fails its tag check instead of reading back as that session's transcript.
        Entity<TranscriptSegment>(entity => entity.SessionId, entity => entity.Id)
            .Required(entity => entity.Text, "transcript_segment_text")
    ];

    private static readonly FrozenDictionary<Type, EncryptedEntity> EntitiesByType = Entities.ToFrozenDictionary(entity => entity.EntityType);

    /// <summary>The registration for a materialized instance's type or its nearest registered base type, as a type pattern would match a subclass or proxy.</summary>
    internal static bool TryGet(Type entityType, [NotNullWhen(true)] out EncryptedEntity? entity)
    {
        for (var type = entityType; type is not null; type = type.BaseType)
        {
            if (EntitiesByType.TryGetValue(type, out entity))
            {
                return true;
            }
        }

        entity = null;
        return false;
    }

    private static EncryptedEntity<TEntity> Entity<TEntity>(Func<TEntity, Guid> conversationId, Func<TEntity, Guid> recordId)
        where TEntity : class
    {
        return new EncryptedEntity<TEntity>(conversationId, recordId);
    }

    private static EncryptedEntity<TEntity> NodeScoped<TEntity>(Func<TEntity, Guid> recordId)
        where TEntity : class
    {
        return new EncryptedEntity<TEntity>(static _ => Guid.Empty, recordId);
    }

    /// <summary>
    ///     The at-rest transform for one column. The bare protector (<see cref="NodePayloadProtector" />) and the versioned
    ///     read-both envelope (<see cref="NodeChatContentProtection" />) both match this shape in each direction.
    /// </summary>
    private delegate byte[] PayloadTransform(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName);

    /// <summary>The protect/unprotect pair a column is written and read with.</summary>
    private sealed class ColumnCipher
    {
        /// <summary>The default for every column that shipped encrypted from creation.</summary>
        public static readonly ColumnCipher Bare = new()
        {
            Protect = NodePayloadProtector.Encrypt,
            Unprotect = NodePayloadProtector.Decrypt
        };

        /// <summary>Message content and metadata only: the columns with legacy plaintext rows on disk.</summary>
        public static readonly ColumnCipher ReadBothEnvelope = new()
        {
            Protect = NodeChatContentProtection.Protect,
            Unprotect = NodeChatContentProtection.Unprotect
        };

        public required PayloadTransform Protect { get; init; }

        public required PayloadTransform Unprotect { get; init; }
    }

    /// <summary>One encrypted column's plaintext, held while its tracked property carries ciphertext through a save.</summary>
    internal sealed record TrackedEncryptedProperty
    {
        public required PropertyEntry PropertyEntry { get; init; }

        public required byte[] Plaintext { get; init; }
    }

    /// <summary>One entity type's encrypted columns, walked by both interceptors.</summary>
    internal abstract class EncryptedEntity
    {
        protected EncryptedEntity(Type entityType)
        {
            EntityType = entityType;
        }

        public Type EntityType { get; }

        /// <summary>Encrypts the columns of every tracked entry of this type in place, adding each original to <paramref name="trackedProperties" />.</summary>
        public abstract void EncryptTracked(NodeChatDbContext context, List<TrackedEncryptedProperty> trackedProperties);

        /// <summary>Decrypts the columns of a freshly materialized instance of this type in place.</summary>
        public abstract void Decrypt(NodeChatDbContext context, object entity);
    }

    private sealed class EncryptedEntity<TEntity> : EncryptedEntity
        where TEntity : class
    {
        private readonly List<EncryptedColumn<TEntity>> _columns = [];
        private readonly Func<TEntity, Guid> _conversationId;
        private readonly Func<TEntity, Guid> _recordId;

        public EncryptedEntity(Func<TEntity, Guid> conversationId, Func<TEntity, Guid> recordId)
            : base(typeof(TEntity))
        {
            _conversationId = conversationId;
            _recordId = recordId;
        }

        public EncryptedEntity<TEntity> Required(Expression<Func<TEntity, byte[]>> property, string columnName, ColumnCipher? cipher = null)
        {
            _columns.Add(new EncryptedColumn<TEntity>(property, required: true, columnName, columnNameOf: null, cipher ?? ColumnCipher.Bare));
            return this;
        }

        public EncryptedEntity<TEntity> Required(Expression<Func<TEntity, byte[]>> property, Func<TEntity, string> columnNameOf)
        {
            _columns.Add(new EncryptedColumn<TEntity>(property, required: true, fixedColumnName: null, columnNameOf, ColumnCipher.Bare));
            return this;
        }

        public EncryptedEntity<TEntity> Optional(Expression<Func<TEntity, byte[]?>> property, string columnName, ColumnCipher? cipher = null)
        {
            _columns.Add(new EncryptedColumn<TEntity>(property, required: false, columnName, columnNameOf: null, cipher ?? ColumnCipher.Bare));
            return this;
        }

        public EncryptedEntity<TEntity> Optional(Expression<Func<TEntity, byte[]?>> property, Func<TEntity, string> columnNameOf)
        {
            _columns.Add(new EncryptedColumn<TEntity>(property, required: false, fixedColumnName: null, columnNameOf, ColumnCipher.Bare));
            return this;
        }

        public override void EncryptTracked(NodeChatDbContext context, List<TrackedEncryptedProperty> trackedProperties)
        {
            foreach (var entry in context.ChangeTracker.Entries<TEntity>())
            {
                foreach (var column in _columns)
                {
                    column.Encrypt(entry, _conversationId(entry.Entity), _recordId(entry.Entity), trackedProperties);
                }
            }
        }

        public override void Decrypt(NodeChatDbContext context, object entity)
        {
            var typed = (TEntity)entity;

            // Read before the first column, as every column reads it: a disposed key holder throws even for a row whose optional columns are all null.
            var key = context.NodeEncryptionKey.Span;

            foreach (var column in _columns)
            {
                column.Decrypt(typed, key, _conversationId(typed), _recordId(typed));
            }
        }
    }

    private sealed class EncryptedColumn<TEntity>
        where TEntity : class
    {
        private readonly ColumnCipher _cipher;
        private readonly Func<TEntity, string>? _columnNameOf;
        private readonly string? _fixedColumnName;
        private readonly Func<TEntity, byte[]?> _get;
        private readonly string _propertyName;
        private readonly bool _required;
        private readonly Action<TEntity, byte[]?> _set;

        public EncryptedColumn(LambdaExpression property, bool required, string? fixedColumnName, Func<TEntity, string>? columnNameOf, ColumnCipher cipher)
        {
            if (property.Body is not MemberExpression { Member: PropertyInfo { GetMethod: { } getter, SetMethod: { } setter } info } member
                || member.Expression != property.Parameters[0]
                || info.PropertyType != typeof(byte[]))
            {
                throw new ArgumentException($"'{property}' is not a settable byte[] property of {typeof(TEntity).Name}.", nameof(property));
            }

            _propertyName = info.Name;
            _get = getter.CreateDelegate<Func<TEntity, byte[]?>>();
            _set = setter.CreateDelegate<Action<TEntity, byte[]?>>();
            _required = required;
            _fixedColumnName = fixedColumnName;
            _columnNameOf = columnNameOf;
            _cipher = cipher;
        }

        public void Encrypt(EntityEntry<TEntity> entry, Guid conversationId, Guid recordId, List<TrackedEncryptedProperty> trackedProperties)
        {
            if (_required)
            {
                EncryptRequired(entry, entry.Property<byte[]>(_propertyName), conversationId, recordId, ColumnName(entry.Entity), trackedProperties, _cipher.Protect);
            }
            else
            {
                EncryptOptional(entry, entry.Property<byte[]?>(_propertyName), conversationId, recordId, ColumnName(entry.Entity), trackedProperties, _cipher.Protect);
            }
        }

        /// <summary>A required column passes its value through even when null; an optional one keeps a null as null.</summary>
        public void Decrypt(TEntity entity, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId)
        {
            var payload = _get(entity);

            if (_required)
            {
                _set(entity, _cipher.Unprotect(payload, key, conversationId, recordId, ColumnName(entity)));
            }
            else
            {
                _set(entity, payload is null ? null : _cipher.Unprotect(payload, key, conversationId, recordId, ColumnName(entity)));
            }
        }

        private string ColumnName(TEntity entity)
        {
            return _fixedColumnName ?? _columnNameOf!(entity);
        }

        private static void EncryptRequired(EntityEntry<TEntity> entry,
            PropertyEntry<TEntity, byte[]> propertyEntry,
            Guid conversationId,
            Guid recordId,
            string columnName,
            List<TrackedEncryptedProperty> trackedProperties,
            PayloadTransform protect)
        {
            if (!IsWritten(entry, propertyEntry))
            {
                return;
            }

            var plaintext = propertyEntry.CurrentValue;
            var context = (NodeChatDbContext)entry.Context;
            var plaintextCopy = plaintext.ToArray();

            propertyEntry.CurrentValue = protect(plaintextCopy,
                context.NodeEncryptionKey.Span,
                conversationId,
                recordId,
                columnName);

            trackedProperties.Add(new TrackedEncryptedProperty
            {
                PropertyEntry = propertyEntry,
                Plaintext = plaintextCopy
            });
        }

        private static void EncryptOptional(EntityEntry<TEntity> entry,
            PropertyEntry<TEntity, byte[]?> propertyEntry,
            Guid conversationId,
            Guid recordId,
            string columnName,
            List<TrackedEncryptedProperty> trackedProperties,
            PayloadTransform protect)
        {
            if (!IsWritten(entry, propertyEntry))
            {
                return;
            }

            var plaintext = propertyEntry.CurrentValue;
            if (plaintext is null)
            {
                return;
            }

            var context = (NodeChatDbContext)entry.Context;
            var plaintextCopy = plaintext.ToArray();

            propertyEntry.CurrentValue = protect(plaintextCopy,
                context.NodeEncryptionKey.Span,
                conversationId,
                recordId,
                columnName);

            trackedProperties.Add(new TrackedEncryptedProperty
            {
                PropertyEntry = propertyEntry,
                Plaintext = plaintextCopy
            });
        }

        /// <summary>The save writes this column: the row is inserted, or updated with this property among the modified ones.</summary>
        private static bool IsWritten(EntityEntry<TEntity> entry, PropertyEntry propertyEntry)
        {
            if (entry.State is not EntityState.Added and not EntityState.Modified)
            {
                return false;
            }

            return entry.State != EntityState.Modified || propertyEntry.IsModified;
        }
    }
}
