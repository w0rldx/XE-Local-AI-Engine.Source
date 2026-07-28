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

internal sealed class DevelopmentTemplateMaterializationConfiguration : IEntityTypeConfiguration<DevelopmentTemplateMaterialization>
{
    public void Configure(EntityTypeBuilder<DevelopmentTemplateMaterialization> builder)
    {
        builder.ToTable("development_template_materializations");

        // The selected folder IS the identity: one materialization produces one folder, and re-materializing into an
        // existing registered folder is not a thing the create path allows.
        builder.HasKey(entity => entity.SelectedFolderId);
        builder.Property(entity => entity.SelectedFolderId).HasColumnName("selected_folder_id");
        builder.Property(entity => entity.TemplateId).HasColumnName("template_id");
        builder.Property(entity => entity.TemplateAlias).HasColumnName("template_alias").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.TemplatePath).HasColumnName("template_path").IsRequired();
        builder.Property(entity => entity.TemplateCommit).HasColumnName("template_commit").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        // Deliberately no foreign key to development_templates: removing a template from the registry must not delete,
        // block, or rewrite the provenance of repositories already created from it.
        builder.HasOne<NodeSelectedFolder>()
               .WithMany()
               .HasForeignKey(entity => entity.SelectedFolderId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
