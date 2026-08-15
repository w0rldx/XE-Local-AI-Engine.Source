namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class TrainingBaseArtifactConfiguration : IEntityTypeConfiguration<TrainingBaseArtifact>
{
    public void Configure(EntityTypeBuilder<TrainingBaseArtifact> builder)
    {
        builder.ToTable("training_base_artifacts",
            table => table.HasCheckConstraint("CK_training_base_artifacts_total_bytes", "total_bytes >= 0"));
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RepoId).HasColumnName("repo_id").HasMaxLength(255).UseCollation("NOCASE").IsRequired();
        builder.Property(entity => entity.Revision).HasColumnName("revision").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.FilesJson).HasColumnName("files_json").IsRequired();
        builder.Property(entity => entity.TotalBytes).HasColumnName("total_bytes");
        builder.Property(entity => entity.LicenseJson).HasColumnName("license_json");
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message").HasMaxLength(1024);
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // One artifact per (repo, commit): re-selecting the same base checkpoint reuses the existing download.
        builder.HasIndex(entity => new
        {
            entity.RepoId,
            entity.Revision
        }).IsUnique().HasDatabaseName("ux_training_base_artifacts_repo_revision");
    }
}
