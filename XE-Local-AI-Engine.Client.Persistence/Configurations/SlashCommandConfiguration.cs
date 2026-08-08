namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class SlashCommandConfiguration : IEntityTypeConfiguration<SlashCommand>
{
    public void Configure(EntityTypeBuilder<SlashCommand> builder)
    {
        builder.ToTable("slash_commands");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").UseCollation("NOCASE").HasMaxLength(64);
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.ActionType).HasColumnName("action_type");
        builder.Property(entity => entity.ActionConfiguration).HasColumnName("action_configuration").IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.HasIndex(entity => entity.Name).IsUnique();
    }
}
