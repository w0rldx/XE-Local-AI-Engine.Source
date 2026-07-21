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
        builder.Property(entity => entity.PayloadJson).HasColumnName("payload_json");
        builder.Property(entity => entity.StorageKey).HasColumnName("storage_key").HasMaxLength(128);
        builder.Property(entity => entity.ContentHash).HasColumnName("content_hash").HasMaxLength(128);
        builder.Property(entity => entity.ByteCount).HasColumnName("byte_count");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.BaseCommitHash).HasColumnName("base_commit_hash").HasMaxLength(128);
        builder.Property(entity => entity.SubjectHash).HasColumnName("subject_hash").HasMaxLength(128);
        builder.Property(entity => entity.ChangedFilesManifestHash).HasColumnName("changed_files_manifest_hash").HasMaxLength(128);
        builder.Property(entity => entity.InputArtifactIdsJson).HasColumnName("input_artifact_ids_json");
        builder.Property(entity => entity.CommandProfileVersion).HasColumnName("command_profile_version").HasMaxLength(128);
        builder.Property(entity => entity.IsValid).HasColumnName("is_valid");
        builder.HasIndex(entity => new { entity.TaskId, entity.Kind, entity.IsValid });
        builder.HasOne<DevelopmentProject>().WithMany().HasForeignKey(entity => entity.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DevelopmentTask>().WithMany().HasForeignKey(entity => entity.TaskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<DevelopmentAttempt>().WithMany().HasForeignKey(entity => entity.AttemptId).OnDelete(DeleteBehavior.SetNull);
    }
}
