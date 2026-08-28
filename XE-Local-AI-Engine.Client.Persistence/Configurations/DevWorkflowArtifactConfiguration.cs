namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowArtifactConfiguration : IEntityTypeConfiguration<DevWorkflowArtifact>
{
    public void Configure(EntityTypeBuilder<DevWorkflowArtifact> builder)
    {
        builder.ToTable("dev_workflow_artifacts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.LineageId).HasColumnName("lineage_id");

        // Node key, name, media type and digest stay plaintext: the first two are the lineage key, and the digest is
        // compared. The bytes themselves never reach this table — they live encrypted under IDevWorkflowArtifactBlobStore.
        builder.Property(entity => entity.ProducingNodeKey).HasColumnName("producing_node_key").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ProducedByNodeRunId).HasColumnName("produced_by_node_run_id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.MediaType).HasColumnName("media_type").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ContentSha256).HasColumnName("content_sha256").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.SizeBytes).HasColumnName("size_bytes");
        builder.Property(entity => entity.ManagedReference).HasColumnName("managed_reference").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.IsValid).HasColumnName("is_valid");
        builder.Property(entity => entity.IsStale).HasColumnName("is_stale");
        builder.Property(entity => entity.StaleSinceSequence).HasColumnName("stale_since_sequence");
        builder.Property(entity => entity.StaleBecauseArtifactId).HasColumnName("stale_because_artifact_id");
        builder.Property(entity => entity.StaleReason).HasColumnName("stale_reason").HasMaxLength(255);
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne<DevWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        // The lineage is the version key. IsLatest is derived (max version per lineage) and never stored: a flag would
        // be a second write that can disagree with the rows it summarizes.
        builder.HasIndex(entity => new
        {
            entity.LineageId,
            entity.Version
        }).IsUnique().HasDatabaseName("ux_dev_workflow_artifacts_lineage_version");

        // The lineage-resolution index: (run, producing node key, name) is the lineage identity.
        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.ProducingNodeKey,
            entity.Name
        }).HasDatabaseName("ix_dev_workflow_artifacts_run_node_name");

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Sequence
        }).HasDatabaseName("ix_dev_workflow_artifacts_run_sequence");
        builder.HasIndex(entity => entity.ProducedByNodeRunId).HasDatabaseName("ix_dev_workflow_artifacts_producer");
    }
}
