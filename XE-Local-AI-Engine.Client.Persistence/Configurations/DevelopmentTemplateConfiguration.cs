namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentTemplateConfiguration : IEntityTypeConfiguration<DevelopmentTemplate>
{
    public void Configure(EntityTypeBuilder<DevelopmentTemplate> builder)
    {
        builder.ToTable("development_templates");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Alias).HasColumnName("alias").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.HostPath).HasColumnName("host_path").IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasIndex(entity => entity.Alias).IsUnique().HasDatabaseName("ux_development_templates_alias");
    }
}
