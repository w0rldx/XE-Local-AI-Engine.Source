namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowWorkItemConfiguration : IEntityTypeConfiguration<DevWorkflowWorkItem>
{
    public void Configure(EntityTypeBuilder<DevWorkflowWorkItem> builder)
    {
        builder.ToTable("dev_workflow_work_items");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");

        // Deliberately PLAINTEXT: the list page sorts and filters on the title. The request beside it is the
        // privacy-sensitive half and is encrypted through NodeEncryptionSaveChangesInterceptor.
        builder.Property(entity => entity.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Request).HasColumnName("request").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);

        // No foreign key on development_project_id: the Development family purges on its own terms and a work item
        // whose project is gone must read back as recoverable state rather than fail the delete.
        builder.Property(entity => entity.DevelopmentProjectId).HasColumnName("development_project_id");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.UpdatedAtUtc
        }).HasDatabaseName("ix_dev_workflow_work_items_status_updated");
        builder.HasIndex(entity => entity.DevelopmentProjectId).HasDatabaseName("ix_dev_workflow_work_items_project");
    }
}
