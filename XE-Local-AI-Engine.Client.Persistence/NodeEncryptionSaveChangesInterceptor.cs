namespace XE_Local_AI_Engine.Client.Persistence;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeEncryptionSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<TrackedEncryptedProperty>> _pendingRestores = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        EncryptTrackedPayloads(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EncryptTrackedPayloads(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        RestoreTrackedPayloads(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreTrackedPayloads(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        RestoreTrackedPayloads(eventData.Context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreTrackedPayloads(eventData.Context);
        return Task.CompletedTask;
    }

    private void EncryptTrackedPayloads(DbContext? context)
    {
        if (context is not NodeChatDbContext nodeContext)
        {
            return;
        }

        var trackedProperties = new List<TrackedEncryptedProperty>();

        // Conversation titles are conversation-scoped: AAD binds the conversation's own id as both the
        // conversation id and the record id, plus the column name. Title is optional (null until the first
        // user message is received).
        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeConversation>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Title), entry.Entity.ConversationId, entry.Entity.ConversationId, "title", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeMessage>())
        {
            // Content and metadata are the only encrypted columns with legacy plaintext rows on disk, so they carry the
            // versioned read-both envelope (NodeChatContentProtection) instead of the bare protector. This keeps an
            // EF-tracked save byte-compatible with the raw-ADO persistence path.
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Content), entry.Entity.ConversationId, entry.Entity.MessageId, "content", trackedProperties,
                NodeChatContentProtection.Protect);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.MetadataJson), entry.Entity.ConversationId, entry.Entity.MessageId, "metadata_json", trackedProperties,
                NodeChatContentProtection.Protect);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeToolEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.PlaintextArgs), entry.Entity.ConversationId, entry.Entity.ToolCallId, "plaintext_args", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.PlaintextResult), entry.Entity.ConversationId, entry.Entity.ToolCallId, "plaintext_result", trackedProperties);
        }

        // Selected folders are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // folder's own id plus the column name. See NodePayloadProtector for the associated-data layout.
        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeSelectedFolder>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.HostPath), Guid.Empty, entry.Entity.Id, "host_path", trackedProperties);
        }

        // Development templates and the provenance of repositories materialized from them both carry host paths, so
        // they get exactly the selected-folder treatment: node-scoped AAD binding the empty conversation id to the
        // row's own id plus the column name.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTemplate>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.HostPath), Guid.Empty, entry.Entity.Id, "host_path", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTemplateMaterialization>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.TemplatePath), Guid.Empty, entry.Entity.SelectedFolderId, "template_path", trackedProperties);
        }

        // Agent definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the definition's own id plus the column name — same layout as selected folders.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentDefinition>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Instructions), Guid.Empty, entry.Entity.Id, "instructions", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.GenerationMetadataJson), Guid.Empty, entry.Entity.Id, "generation_metadata_json", trackedProperties);
        }

        // Canvas workflows are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // workflow's own id plus the column name — same layout as agent definitions. The serialized graph (which carries
        // agent instructions and Start text) is a required encrypted column.
        foreach (var entry in nodeContext.ChangeTracker.Entries<CanvasWorkflow>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), Guid.Empty, entry.Entity.Id, "graph_json", trackedProperties);
        }

        // Agent skills are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // skill's own id plus the column name — same layout as agent definitions. Both the description and the SKILL.md
        // body are required encrypted columns.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentSkill>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Body), Guid.Empty, entry.Entity.Id, "body", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.FrontmatterJson), Guid.Empty, entry.Entity.Id, "frontmatter_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.GenerationMetadataJson), Guid.Empty, entry.Entity.Id, "generation_metadata_json", trackedProperties);
        }

        // Skill resources are the one place in this schema where the row id alone is the wrong AAD binding. The threat
        // is a database WRITER, not a reader (same reasoning as the MCP key hash below): with only the row id bound,
        // anyone who could edit the file could point an existing resource row at another skill and have its content
        // injected into a different agent's context, without forging a ciphertext or a tag. So the skill id takes the
        // conversation slot and the resource name rides in the column name — moving a row, or renaming it underneath
        // its ciphertext, now fails the tag check. Renames therefore go through delete-and-reinsert in the store.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentSkillResource>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Content), entry.Entity.SkillId, entry.Entity.Id, AgentSkillResource.ContentColumnName(entry.Entity.Name), trackedProperties);
        }

        // Custom tools are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // tool's own id plus the column name — same layout as agent skills. Description is required; the kind-specific
        // config (which carries the secret header/env values) is a required encrypted column under a distinct AAD column
        // name so a config blob can never be substituted for a description blob.
        foreach (var entry in nodeContext.ChangeTracker.Entries<CustomTool>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.ConfigJson), Guid.Empty, entry.Entity.Id, "custom_tool_config_json", trackedProperties);
        }

        // Playbook actions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // action's own id plus the column name — same layout as agent definitions. Behavior is required; the optional
        // trigger condition only encrypts when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<PlaybookAction>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Behavior), Guid.Empty, entry.Entity.Id, "behavior", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.TriggerCondition), Guid.Empty, entry.Entity.Id, "trigger_condition", trackedProperties);
        }

        // Golden conversations are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the case's own id plus the column name — same layout as playbook actions. InputTurns is required; the optional
        // assertion/rubric only encrypt when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<GoldenConversation>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.InputTurns), Guid.Empty, entry.Entity.Id, "input_turns", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Assertion), Guid.Empty, entry.Entity.Id, "assertion", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Rubric), Guid.Empty, entry.Entity.Id, "rubric", trackedProperties);
        }

        // MCP server registrations are node-scoped, so the AAD binds the empty conversation id to the registration's
        // own id plus the column name. The secret-bearing columns (args, env, description) are all optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<McpServerRegistration>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ArgumentsJson), Guid.Empty, entry.Entity.Id, "arguments", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.EnvJson), Guid.Empty, entry.Entity.Id, "env", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<SlashCommand>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, SlashCommand.DescriptionColumnName(entry.Entity.Name), trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.ActionConfiguration), Guid.Empty, entry.Entity.Id,
                SlashCommand.ActionConfigurationColumnName(entry.Entity.Name), trackedProperties);
        }

        // The inbound-MCP bearer credential is node-scoped, so the AAD binds the empty conversation id to the row's own
        // (constant, singleton) id plus the column name. The stored value is a one-way SHA-256 digest, not the key, so
        // this is not protecting a secret from a reader — it is protecting the digest from a WRITER. Without the
        // AAD-bound AEAD, anyone who could edit the database file could drop in the hash of a key they chose and
        // authenticate against an agent-execution surface. The hash is required — a row without it would authenticate
        // nothing — so it always encrypts.
        foreach (var entry in nodeContext.ChangeTracker.Entries<McpServerApiKey>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.KeyHash), Guid.Empty, entry.Entity.Id, "mcp_api_key_hash", trackedProperties);
        }

        // The inbound model-proxy bearer credential is guarded on the same terms as the MCP key above: the stored value
        // is a one-way SHA-256 digest, so the AAD-bound AEAD protects the digest from a database-file WRITER who would
        // otherwise substitute the hash of a key they chose and take over the model-proxy surface. Required, so it
        // always encrypts. A distinct AAD column name binds it to this table.
        foreach (var entry in nodeContext.ChangeTracker.Entries<LocalModelProxyApiKey>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.KeyHash), Guid.Empty, entry.Entity.Id, "local_model_proxy_api_key_hash", trackedProperties);
        }

        // The external-integration bearer credentials are guarded on the same terms as the two singleton key rows
        // above: the stored value is a one-way SHA-256 digest, so the AAD-bound AEAD protects the digest from a
        // database-file WRITER who would otherwise substitute the hash of a key they chose and invoke every trigger
        // on the node. Required, so it always encrypts. A distinct AAD column name binds it to this table; unlike
        // the two above, a node holds MANY of these rows, so the record id is the row's own id rather than a
        // singleton's.
        foreach (var entry in nodeContext.ChangeTracker.Entries<IntegrationApiKey>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.KeyHash), Guid.Empty, entry.Entity.Id, "integration_api_key_hash", trackedProperties);
        }

        // Scheduled job definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation
        // id to the definition's own id plus the column name. Only the opaque job parameters are encrypted; they are
        // optional, so they encrypt only when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobDefinition>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ParameterJson), Guid.Empty, entry.Entity.Id, "parameter_json", trackedProperties);
        }

        // Scheduled job runs are node-scoped, so the AAD binds the empty conversation id to the run's own id plus the
        // column name. Only the structured run detail is encrypted; it is optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobRun>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailsJson), Guid.Empty, entry.Entity.Id, "details_json", trackedProperties);
        }

        // Scheduled job run events are node-scoped, so the AAD binds the empty conversation id to the event's own id
        // plus the column name. Only the structured event payload is encrypted; it is optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobRunEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DataJson), Guid.Empty, entry.Entity.Id, "data_json", trackedProperties);
        }

        // Model-fit snapshots are node-scoped, so the AAD binds the empty conversation id to the snapshot's own id plus
        // the column name. The raw utility output, stderr excerpt and detailed diagnostics are sensitive and optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ModelFitSnapshot>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.RawJson), Guid.Empty, entry.Entity.Id, "raw_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.StderrExcerpt), Guid.Empty, entry.Entity.Id, "stderr_excerpt", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DiagnosticsJson), Guid.Empty, entry.Entity.Id, "diagnostics_json", trackedProperties);
        }

        // Model-fit benchmark rows are node-scoped, so the AAD binds the empty conversation id to the row's own id plus
        // the column name. Distinct AAD column names (bench_*) avoid cross-entity collision with the snapshot columns.
        // The raw benchmark output and diagnostics are sensitive and optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ModelFitBenchmark>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.RawJson), Guid.Empty, entry.Entity.Id, "bench_raw_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DiagnosticsJson), Guid.Empty, entry.Entity.Id, "bench_diagnostics_json", trackedProperties);
        }

        // Uploaded files are conversation-scoped: the AAD binds the owning conversation id to the file's own id plus the
        // column name — same layout as conversation titles. Only the display name is encrypted (the durable bytes and
        // extracted text are encrypted on disk by the file store, not in a column). The store's raw-SQL write path uses
        // NodeChatDbContext.EncryptUploadedFileName with the identical protector/AAD, so an EF save (used by tests) and
        // the raw-SQL write are interchangeable; this guard keeps the column encrypted for any EF-tracked save.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ConversationUploadedFile>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.OriginalFileName), entry.Entity.ConversationId, entry.Entity.FileId, "original_file_name", trackedProperties);
        }

        // Image jobs are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the job's
        // own id plus the column name — same layout as agent definitions. The prompt is required; the negative prompt
        // only encrypts when present. Distinct AAD column names (image_*) avoid cross-entity collision.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ImageJob>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Prompt), Guid.Empty, entry.Entity.Id, "image_prompt", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.NegativePrompt), Guid.Empty, entry.Entity.Id, "image_negative_prompt", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentProject>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Objective), entry.Entity.Id, entry.Entity.Id, "development_objective", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTask>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Title), entry.Entity.ProjectId, entry.Entity.Id, "development_task_title", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Requirements), entry.Entity.ProjectId, entry.Entity.Id, "development_task_requirements", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.AcceptanceCriteriaJson), entry.Entity.ProjectId, entry.Entity.Id, "development_acceptance_criteria_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentArtifact>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ContentJson), entry.Entity.ProjectId, entry.Entity.Id, "development_artifact_content_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.InputArtifactIdsJson), entry.Entity.ProjectId, entry.Entity.Id, "development_artifact_input_ids_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.ProjectId, entry.Entity.Id, "development_event_detail_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ResultMetadataJson), entry.Entity.ProjectId, entry.Entity.Id, "development_event_result_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkProject>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.CoreTaskJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_core_task_json",
                trackedProperties);
        }

        // A distinct AAD column name per payload, as everywhere else: a verifier config carries expected answers and
        // a generator config carries the parameters that produce them, so neither may be presentable as the prompt.
        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkTaskItem>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.PromptJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_task_item_prompt_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ReferenceAnswerJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_task_item_reference_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.VerifierConfigJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_task_item_verifier_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.GeneratorConfigJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_task_item_generator_json",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkRun>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.RuntimeSnapshotJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_runtime_snapshot_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.OutputPartsJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_output_parts_json",
                trackedProperties);
            // The primary launch-evidence block. A distinct AAD column name per column so a writer cannot present an
            // environment capture as a receipt; the judge's own evidence lives on its attempt row.
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.PrimaryLaunchReceiptJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_primary_launch_receipt_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.PrimaryEnvironmentFactsJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_primary_environment_facts_json",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkJudgePolicyRevision>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.PolicyJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_judge_policy_json",
                trackedProperties);
        }

        // Every attempt column is optional: an attempt inserted directly as Failed carries no runtime, and an attempt
        // that never reached readiness carries no receipt. The optional path never dereferences a missing value.
        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkJudgeAttempt>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.JudgeRuntimeJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_judge_runtime_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ResultJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_judge_attempt_result_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LaunchReceiptJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_judge_attempt_launch_receipt_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.EnvironmentFactsJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_judge_attempt_environment_facts_json",
                trackedProperties);
        }

        // The fidelity receipt is encrypted for the same reason the launch receipts are: its argv carries host
        // filesystem paths. The measured numbers themselves stay plaintext on the attempt, so the listing sorts
        // without decrypting.
        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkFidelityAttempt>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ReceiptJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_fidelity_receipt_json",
                trackedProperties);
        }

        // A distinct AAD column name per column, as on the judge attempt: a writer cannot present an environment
        // capture as a receipt, or a rationale as a runtime. The verdict token stays plaintext — it is the rankable
        // signal, and ranking is a SQL sort.
        foreach (var entry in nodeContext.ChangeTracker.Entries<BenchmarkJudgeComparison>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.JudgeRuntimeJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_comparison_runtime_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ResultJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_comparison_result_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LaunchReceiptJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_comparison_launch_receipt_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.EnvironmentFactsJson),
                Guid.Empty,
                entry.Entity.Id,
                "benchmark_comparison_environment_facts_json",
                trackedProperties);
        }

        // Training dataset definitions are node-scoped, so the AAD binds the empty conversation id to the definition's
        // own id plus the column name — same layout as benchmark projects.
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingDatasetDefinition>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.DefinitionJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_definition_json",
                trackedProperties);
        }

        // The dataset's pinned copy of that body. A distinct AAD column name from the definition's own, so a writer
        // cannot present a definition row's ciphertext as a dataset's pinned snapshot (or the reverse).
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingDataset>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.DefinitionJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_dataset_definition_json",
                trackedProperties);
        }

        // Dataset samples take the skill-resource treatment rather than the flat one: the owning dataset id goes in the
        // conversation slot so a database WRITER cannot re-parent a sample row onto another dataset and have its content
        // and verdicts fed into a different training run. Moving a row now fails the tag check.
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingDatasetSample>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.ContentJson),
                entry.Entity.DatasetId,
                entry.Entity.Id,
                "training_sample_content_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ValidationJson),
                entry.Entity.DatasetId,
                entry.Entity.Id,
                "training_sample_validation_json",
                trackedProperties);
        }

        // Tool mocks are node-scoped. The mock body and the verifier's verdict carry distinct AAD column names so a mock
        // blob can never be substituted for a verdict blob — the same separation custom tools draw between their
        // description and their config.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ToolMockDefinition>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.MockJson),
                Guid.Empty,
                entry.Entity.Id,
                "tool_mock_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.VerificationJson),
                Guid.Empty,
                entry.Entity.Id,
                "tool_mock_verification_json",
                trackedProperties);
        }

        // Base artifacts are node-scoped. The file manifest carries host paths and per-file digests; the license blob is
        // what the mandatory gate presents, so a writer must not be able to swap one for the other.
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingBaseArtifact>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.FilesJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_base_files_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LicenseJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_base_license_json",
                trackedProperties);
        }

        // Runs are node-scoped, so the AAD binds the empty conversation id to the run's own id plus the column name.
        // Every column gets a distinct name: the freeze, the options and the license confirmation are the three
        // documents an audit reads back, and a writer must not be able to swap one for another.
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingRun>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.FreezeJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_freeze_json",
                trackedProperties);
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.OptionsJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_options_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LicenseConfirmationJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_license_confirmation_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ProgressJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_progress_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LogTail),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_log_tail",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.LaunchReceiptJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_run_launch_receipt_json",
                trackedProperties);
        }

        // Evaluations and comparison reports are node-scoped, same flat layout as runs. The membership and the verdicts
        // carry distinct AAD column names so a writer cannot present one as the other: the membership is what makes a
        // comparison's two sides comparable, and the verdicts are what the deltas are computed from.
        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingEvaluationRun>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.MembershipJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_evaluation_membership_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ResultsJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_evaluation_results_json",
                trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.ExecutionProvenanceJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_evaluation_execution_provenance_json",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingComparisonReport>())
        {
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.DeltasJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_comparison_deltas_json",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<TrainingArtifact>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.QualityDecisionJson),
                Guid.Empty,
                entry.Entity.Id,
                "training_artifact_quality_decision_json",
                trackedProperties);
        }

        // Work sessions take the Development layout exactly: the session's own id fills both AAD slots on the session
        // row, and the owning session id fills the conversation slot on every child row so a writer cannot re-parent a
        // task, finding, checkpoint or event onto another session and have its text read back as that session's work.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentWorkSession>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Objective), entry.Entity.Id, entry.Entity.Id, "work_session_objective", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentWorkSessionTask>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Title), entry.Entity.SessionId, entry.Entity.Id, "work_session_task_title", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Detail), entry.Entity.SessionId, entry.Entity.Id, "work_session_task_detail", trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.BlockedReason),
                entry.Entity.SessionId,
                entry.Entity.Id,
                "work_session_task_blocked_reason",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentWorkSessionFinding>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Text), entry.Entity.SessionId, entry.Entity.Id, "work_session_finding_text", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.SourceRef), entry.Entity.SessionId, entry.Entity.Id, "work_session_finding_source_ref", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentWorkSessionCheckpoint>())
        {
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.Summary),
                entry.Entity.SessionId,
                entry.Entity.Id,
                "work_session_checkpoint_summary",
                trackedProperties);
            EncryptRequiredProperty(entry,
                entry.Property(entity => entity.StateJson),
                entry.Entity.SessionId,
                entry.Entity.Id,
                "work_session_checkpoint_state_json",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentWorkSessionEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.SessionId, entry.Entity.Id, "work_session_event_detail_json", trackedProperties);
        }

        // Dev workflows take the work-session layout: the row's own id fills both AAD slots on a root row, and the
        // owning id fills the conversation slot on every child row so a writer cannot re-parent a run onto another work
        // item, or a node-run onto another run, and have the blob read back as that owner's.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowWorkItem>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Request), entry.Entity.Id, entry.Entity.Id, "dev_workflow_work_item_request", trackedProperties);
        }

        // The definition is node-scoped, so the empty conversation id plus its own id — but under an AAD column name
        // distinct from the run's pinned copy below, so neither ciphertext is presentable as the other.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowDefinition>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), Guid.Empty, entry.Entity.Id, "dev_workflow_definition_graph_json", trackedProperties);
        }

        // A rule set is node-scoped like the definition, so the empty conversation id plus its own id — and its own AAD
        // column name, so a rule body can never be read back as a definition's graph.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowRuleSet>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Body), Guid.Empty, entry.Entity.Id, "dev_workflow_rule_set_body", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowRun>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), entry.Entity.WorkItemId, entry.Entity.Id, "dev_workflow_run_graph_json", trackedProperties);
        }

        // Three distinct AAD column names on one row, and not cosmetically: a Gate node's decision is a function of
        // output_json, so if these shared a name a database writer could swap a policy or input blob into the output
        // column and flip a gate without forging a ciphertext or a tag.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowNodeRun>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.InputJson), entry.Entity.RunId, entry.Entity.Id, "dev_workflow_node_run_input_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.OutputJson), entry.Entity.RunId, entry.Entity.Id, "dev_workflow_node_run_output_json", trackedProperties);
            EncryptOptionalProperty(entry,
                entry.Property(entity => entity.PolicyResolutionJson),
                entry.Entity.RunId,
                entry.Entity.Id,
                "dev_workflow_node_run_policy_json",
                trackedProperties);
        }

        // The payload is the structured, machine-consumed half of a decision; the comment is free text. Distinct names
        // so the one that a runtime acts on can never be substituted from the one a human typed.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowDecision>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Comment), entry.Entity.RunId, entry.Entity.Id, "dev_workflow_decision_comment", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.PayloadJson), entry.Entity.RunId, entry.Entity.Id, "dev_workflow_decision_payload_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevWorkflowRunEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.RunId, entry.Entity.Id, "dev_workflow_run_event_detail_json", trackedProperties);
        }

        // The one column in the integration family that holds real content: an external.output event carries the
        // tool's payload verbatim. The owning execution fills the conversation slot of the AAD, so a re-parented
        // event row fails its tag check instead of reading back as another execution's output. Optional — phase
        // events carry no detail, which is what lets the raw-ADO accept path skip encryption entirely.
        foreach (var entry in nodeContext.ChangeTracker.Entries<IntegrationExecutionEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.ExecutionId, entry.Entity.Id, "integration_execution_event_detail_json",
                trackedProperties);
        }

        // Graph Workflows take the same layout. The definition is node-scoped, so the empty conversation id plus its
        // own id; the run binds its DEFINITION, and node runs and events bind their run, so a re-parented row fails
        // authenticated decryption rather than reading back as another owner's.
        foreach (var entry in nodeContext.ChangeTracker.Entries<GraphWorkflowDefinition>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), Guid.Empty, entry.Entity.Id, "graph_workflow_definition_graph_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<GraphWorkflowRun>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), entry.Entity.DefinitionId, entry.Entity.Id, "graph_workflow_run_graph_json",
                trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.InputJson), entry.Entity.DefinitionId, entry.Entity.Id, "graph_workflow_run_input_json",
                trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.OutputJson), entry.Entity.DefinitionId, entry.Entity.Id, "graph_workflow_run_output_json",
                trackedProperties);
        }

        // Four distinct AAD column names on one row, and not cosmetically: an edge condition routes on output_json, so
        // if these shared a name a database writer could swap an input, an error or a decider into the output column
        // and reroute a run without forging a ciphertext or a tag.
        foreach (var entry in nodeContext.ChangeTracker.Entries<GraphWorkflowNodeRun>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.InputJson), entry.Entity.RunId, entry.Entity.Id, "graph_workflow_node_run_input_json",
                trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.OutputJson), entry.Entity.RunId, entry.Entity.Id, "graph_workflow_node_run_output_json",
                trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Error), entry.Entity.RunId, entry.Entity.Id, "graph_workflow_node_run_error", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DecidedBySubject), entry.Entity.RunId, entry.Entity.Id, "graph_workflow_node_run_decided_by",
                trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<GraphWorkflowRunEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.RunId, entry.Entity.Id, "graph_workflow_run_event_detail_json",
                trackedProperties);
        }

        // The two artifact tables add nothing here on purpose: the bytes live on disk under the blob store's own AAD
        // column (dev_workflow_artifact_blob), and every column that stays in the row is structural.

        if (trackedProperties.Count > 0)
        {
            _pendingRestores[nodeContext] = trackedProperties;
        }
    }

    private void RestoreTrackedPayloads(DbContext? context)
    {
        if (context is null || !_pendingRestores.TryRemove(context, out var trackedProperties))
        {
            return;
        }

        foreach (var trackedProperty in trackedProperties)
        {
            if (trackedProperty.PropertyEntry.EntityEntry.State == EntityState.Detached)
            {
                continue;
            }

            trackedProperty.PropertyEntry.CurrentValue = trackedProperty.Plaintext.ToArray();
            trackedProperty.PropertyEntry.OriginalValue = trackedProperty.Plaintext.ToArray();
            trackedProperty.PropertyEntry.IsModified = false;
        }
    }

    // The at-rest transform for one column. Both the bare protector (NodePayloadProtector.Encrypt, the default for
    // every column that shipped encrypted from creation) and the versioned read-both envelope
    // (NodeChatContentProtection.Protect, used for message content/metadata) match this shape.
    private delegate byte[] PayloadProtector(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName);

    private static void EncryptRequiredProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties)
        where TEntity : class
    {
        EncryptRequiredProperty(entry, propertyEntry, conversationId, recordId, columnName, trackedProperties, NodePayloadProtector.Encrypt);
    }

    private static void EncryptRequiredProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties,
        PayloadProtector protect)
        where TEntity : class
    {
        if (entry.State is not EntityState.Added and not EntityState.Modified)
        {
            return;
        }

        if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
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

        trackedProperties.Add(new TrackedEncryptedProperty(propertyEntry, plaintextCopy));
    }

    private static void EncryptOptionalProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]?> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties)
        where TEntity : class
    {
        EncryptOptionalProperty(entry, propertyEntry, conversationId, recordId, columnName, trackedProperties, NodePayloadProtector.Encrypt);
    }

    private static void EncryptOptionalProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]?> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties,
        PayloadProtector protect)
        where TEntity : class
    {
        if (entry.State is not EntityState.Added and not EntityState.Modified)
        {
            return;
        }

        if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
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

        trackedProperties.Add(new TrackedEncryptedProperty(propertyEntry, plaintextCopy));
    }

    private sealed record TrackedEncryptedProperty(PropertyEntry PropertyEntry, byte[] Plaintext);
}
