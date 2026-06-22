namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class PlaybookActionConfiguration : IEntityTypeConfiguration<PlaybookAction>
{
    public void Configure(EntityTypeBuilder<PlaybookAction> builder)
    {
        builder.ToTable("playbook_actions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.Property(entity => entity.State)
               .HasColumnName("state");

        builder.Property(entity => entity.Source)
               .HasColumnName("source");

        // Adaptive-memory typed scope — additive nullable column. Plaintext (an int), not encrypted; null = untyped
        // legacy action. Non-injected metadata (like scope/source), so it never enters the runtime config hash.
        builder.Property(entity => entity.MemoryScope)
               .HasColumnName("memory_scope");

        builder.Property(entity => entity.TriggerCondition)
               .HasColumnName("trigger_condition");

        builder.Property(entity => entity.Behavior)
               .HasColumnName("behavior");

        builder.Property(entity => entity.Scope)
               .HasColumnName("scope");

        // Analysis provenance/confidence — additive nullable columns. Plaintext (ids only / a scalar), not encrypted.
        builder.Property(entity => entity.SourceFeedbackIds)
               .HasColumnName("source_feedback_ids");

        builder.Property(entity => entity.Confidence)
               .HasColumnName("confidence");

        // Eval-gate outcome — additive nullable column. Plaintext (ids + flags + counts only), not encrypted.
        builder.Property(entity => entity.EvalResult)
               .HasColumnName("eval_result");

        builder.Property(entity => entity.Priority)
               .HasColumnName("priority");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Cohort-monitoring clock — additive nullable column. Plaintext (a timestamp), not encrypted.
        builder.Property(entity => entity.EnabledAtUtc)
               .HasColumnName("enabled_at_utc");

        builder.HasIndex(entity => entity.AgentDefinitionId);

        // A playbook action is meaningless without its owning agent, so the FK cascades: deleting an agent removes its
        // actions. (Contrast conversation->definition, which is intentionally no-FK because a conversation outlives its
        // definition.)
        builder.HasOne<AgentDefinition>()
               .WithMany()
               .HasForeignKey(entity => entity.AgentDefinitionId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
