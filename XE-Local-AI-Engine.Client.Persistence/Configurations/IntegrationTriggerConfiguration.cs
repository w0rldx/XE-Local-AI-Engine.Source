namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class IntegrationTriggerConfiguration : IEntityTypeConfiguration<IntegrationTrigger>
{
    public void Configure(EntityTypeBuilder<IntegrationTrigger> builder)
    {
        builder.ToTable("integration_triggers");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");

        // Deliberately PLAINTEXT throughout: the name is the external contract a caller addresses, and the display
        // fields are sorted and filtered on — the same rule AgentWorkSession.Title follows.
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.DisplayName).HasColumnName("display_name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description").HasMaxLength(2048);
        builder.Property(entity => entity.Enabled).HasColumnName("enabled");
        builder.Property(entity => entity.TargetKind).HasColumnName("target_kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.TargetAgentDefinitionId).HasColumnName("target_agent_definition_id");
        builder.Property(entity => entity.SessionPolicy).HasColumnName("session_policy").HasConversion<string>().HasMaxLength(32);

        // A [Flags] combination has no stable, length-bounded string form, so this one enum stays an int column while
        // every other enum here converts to a string. See IntegrationInputKinds' own remarks.
        builder.Property(entity => entity.AcceptedInputKinds).HasColumnName("accepted_input_kinds");

        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasIndex(entity => entity.Name).IsUnique().HasDatabaseName("ux_integration_triggers_name");
        builder.HasIndex(entity => entity.Enabled).HasDatabaseName("ix_integration_triggers_enabled");
    }
}
