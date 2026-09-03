namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentDefinitionConfiguration : IEntityTypeConfiguration<AgentDefinition>
{
    public void Configure(EntityTypeBuilder<AgentDefinition> builder)
    {
        builder.ToTable("agent_definitions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Instructions)
               .HasColumnName("instructions");

        // AI-generation provenance — additive encrypted column, null for every row that was not AI-drafted.
        builder.Property(entity => entity.GenerationMetadataJson)
               .HasColumnName("generation_metadata_json");

        builder.Property(entity => entity.ModelProfile)
               .HasColumnName("model_profile");

        builder.Property(entity => entity.ReasoningEffort)
               .HasColumnName("reasoning_effort");

        builder.Property(entity => entity.Kind)
               .HasColumnName("kind")
               .HasDefaultValue((int)AgentDefinitionKind.Single);

        builder.Property(entity => entity.AllowedToolNamesJson)
               .HasColumnName("allowed_tool_names_json");

        // Per-agent skill picklist — additive structural column. Plaintext (skill ids only), JSON-array shaped; default
        // and backfill '[]' so a pre-skills definition reads as an empty assignment. Mirrors allowed_tool_names_json.
        builder.Property(entity => entity.AllowedSkillIdsJson)
               .HasColumnName("allowed_skill_ids_json")
               .HasDefaultValue("[]");

        builder.Property(entity => entity.ToolApprovalsJson)
               .HasColumnName("tool_approvals_json");

        builder.Property(entity => entity.OrchestrationTopologyJson)
               .HasColumnName("orchestration_topology_json");

        builder.Property(entity => entity.PlaybookEnabled)
               .HasColumnName("playbook_enabled")
               .HasDefaultValue(false);

        // Base-instruction-scaffold opt-out — additive structural column. Plaintext (a bool); default and backfill
        // false so a pre-scaffold definition keeps getting the scaffold (the intended default for every agent).
        // Non-config-affecting (like playbook_enabled): toggling it changes the resolved prompt directly, which
        // already drives the runtime config hash, so it never needs to bump this definition's own Version.
        builder.Property(entity => entity.DisableBaseScaffold)
               .HasColumnName("disable_base_scaffold")
               .HasDefaultValue(false);

        // Tool-relevance opt-out — additive structural column. Plaintext (a bool); default and backfill false so every
        // existing definition follows the node setting. Non-config-affecting: the filter narrows only the provider-bound
        // tools array, never the offer or the resolved prompt, so it never bumps this definition's own Version.
        builder.Property(entity => entity.DisableToolRelevanceFilter)
               .HasColumnName("disable_tool_relevance_filter")
               .HasDefaultValue(false);

        // Per-agent default-temporary-chat flag — additive structural column. Plaintext (a bool); default and backfill
        // false so a pre-feature definition reads as non-temporary. Non-config-affecting (like playbook_enabled): gates
        // post-run memory extraction only, never the runtime config hash.
        builder.Property(entity => entity.DefaultTemporaryChat)
               .HasColumnName("default_temporary_chat")
               .HasDefaultValue(false);

        // Per-agent memory-extraction toggle — additive structural column. Plaintext (a bool); default and backfill
        // true so a pre-feature definition keeps learning from its runs. Non-config-affecting (like playbook_enabled):
        // gates post-run extraction only (false = retrieval-only memory), never the runtime config hash.
        builder.Property(entity => entity.MemoryExtractionEnabled)
               .HasColumnName("memory_extraction_enabled")
               .HasDefaultValue(true);

        // Provenance — additive structural columns. Plaintext (an int + a slug), not encrypted; the seeded import path
        // is the only writer that sets Source=Seeded / SeedSlug, keeping provenance forge-proof.
        builder.Property(entity => entity.Source)
               .HasColumnName("source")
               .HasDefaultValue((int)AgentDefinitionSource.Manual);

        builder.Property(entity => entity.SeedSlug)
               .HasColumnName("seed_slug");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name);

        // The seed slug is the idempotency key for a re-import, so it is unique — but only among seeded rows that
        // actually carry one (manual rows leave it null), hence the filtered unique index. This is the DB-level guard
        // beneath the service-level skip.
        builder.HasIndex(entity => entity.SeedSlug)
               .IsUnique()
               .HasFilter("\"seed_slug\" IS NOT NULL");
    }
}
