namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ToolMockDefinitionConfiguration : IEntityTypeConfiguration<ToolMockDefinition>
{
    public void Configure(EntityTypeBuilder<ToolMockDefinition> builder)
    {
        builder.ToTable("tool_mock_definitions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ToolName).HasColumnName("tool_name").HasMaxLength(200).UseCollation("NOCASE").IsRequired();
        builder.Property(entity => entity.MockJson).HasColumnName("mock_json").IsRequired();
        builder.Property(entity => entity.VerificationJson).HasColumnName("verification_json");
        builder.Property(entity => entity.VerificationState).HasColumnName("verification_state").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.Enabled).HasColumnName("enabled");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Non-unique: a tool may carry several mocks with different match rules, and the engine picks among the
        // verified+enabled ones. Uniqueness here would decide that design in the schema.
        builder.HasIndex(entity => entity.ToolName).HasDatabaseName("ix_tool_mock_definitions_tool_name");
    }
}
