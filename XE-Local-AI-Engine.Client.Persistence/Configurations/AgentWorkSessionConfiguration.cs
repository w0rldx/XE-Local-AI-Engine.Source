namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionConfiguration : IEntityTypeConfiguration<AgentWorkSession>
{
    public void Configure(EntityTypeBuilder<AgentWorkSession> builder)
    {
        builder.ToTable("agent_work_sessions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");

        // Deliberately PLAINTEXT: the list page sorts and filters on the title. The objective beside it is the
        // privacy-sensitive half and is encrypted through NodeEncryptionSaveChangesInterceptor.
        builder.Property(entity => entity.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Objective).HasColumnName("objective").IsRequired();
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.AgentDefinitionId).HasColumnName("agent_definition_id");
        builder.Property(entity => entity.ConversationId).HasColumnName("conversation_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.CurrentTaskId).HasColumnName("current_task_id");
        builder.Property(entity => entity.StepCount).HasColumnName("step_count");
        builder.Property(entity => entity.LastCheckpointId).HasColumnName("last_checkpoint_id");
        builder.Property(entity => entity.LastSequence).HasColumnName("last_sequence");
        builder.Property(entity => entity.ConfigVersion).HasColumnName("config_version");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        // conversation_id and agent_definition_id carry no foreign key on purpose: the conversation purge path is raw
        // ADO SQL, and a session whose conversation is gone must read back as recoverable state rather than fail the
        // delete. NodeConversation.AgentDefinitionId is the same loose reference.
        builder.HasIndex(entity => entity.ConversationId).IsUnique().HasDatabaseName("ux_agent_work_sessions_conversation_id");
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.UpdatedAtUtc
        }).HasDatabaseName("ix_agent_work_sessions_status_updated");
    }
}
