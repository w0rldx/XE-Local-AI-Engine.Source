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
