namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentArtifactConfiguration : IEntityTypeConfiguration<DevelopmentArtifact>
{
    public void Configure(EntityTypeBuilder<DevelopmentArtifact> builder)
    {
        builder.ToTable("development_artifacts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ProjectId).HasColumnName("project_id");
        builder.Property(entity => entity.TaskId).HasColumnName("task_id");
        builder.Property(entity => entity.AttemptId).HasColumnName("attempt_id");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(64);
        builder.Property(entity => entity.SchemaVersion).HasColumnName("schema_version");
        builder.Property(entity => entity.ContentJson).HasColumnName("content_json");
        builder.Property(entity => entity.ManagedReference).HasColumnName("managed_reference").HasMaxLength(255);
        builder.Property(entity => entity.ContentHash).HasColumnName("content_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ByteCount).HasColumnName("byte_count");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.BaseCommit).HasColumnName("base_commit").HasMaxLength(128);
        builder.Property(entity => entity.SubjectHash).HasColumnName("subject_hash").HasMaxLength(128);
        builder.Property(entity => entity.ChangedFilesManifestHash).HasColumnName("changed_files_manifest_hash").HasMaxLength(128);
        builder.Property(entity => entity.InputArtifactIdsJson).HasColumnName("input_artifact_ids_json");
        builder.Property(entity => entity.CommandProfileVersion).HasColumnName("command_profile_version").HasMaxLength(64);

        // A SEPARATE dimension from command_profile_version above, which carries the artifact PROTOCOL version
        // ("development-workspace-v1" / "development-validation-v1" / "development-review-v1"). This column carries the
        // 64-hex digest of the command profile that produced the artifact. A digest and a protocol version cannot share
        // one 64-character column, which is why this is a new column rather than a reuse of the existing one.
        builder.Property(entity => entity.CommandProfileDigest).HasColumnName("command_profile_digest").HasMaxLength(64);
        builder.Property(entity => entity.IsValid).HasColumnName("is_valid");
        builder.HasOne<DevelopmentProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DevelopmentTask>().WithMany().HasForeignKey(entity => entity.TaskId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<DevelopmentAttempt>().WithMany().HasForeignKey(entity => entity.AttemptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(entity => new
        {
            entity.TaskId,
            entity.Kind,
            entity.IsValid
        }).HasDatabaseName("ix_development_artifacts_task_kind_valid");
        builder.HasIndex(entity => entity.AttemptId).HasDatabaseName("ix_development_artifacts_attempt_id");
    }
}
