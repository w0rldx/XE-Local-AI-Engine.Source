namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowArtifactUseConfiguration : IEntityTypeConfiguration<DevWorkflowArtifactUse>
{
    public void Configure(EntityTypeBuilder<DevWorkflowArtifactUse> builder)
    {
        builder.ToTable("dev_workflow_artifact_uses");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.NodeRunId).HasColumnName("node_run_id");
        builder.Property(entity => entity.ArtifactId).HasColumnName("artifact_id");
        builder.Property(entity => entity.RecordedSequence).HasColumnName("recorded_sequence");

        builder.HasOne<DevWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        // Ids and a long only — nothing here is encrypted, because nothing here is content.
        builder.HasIndex(entity => new
        {
            entity.NodeRunId,
            entity.ArtifactId
        }).IsUnique().HasDatabaseName("ux_dev_workflow_artifact_uses_node_artifact");

        // The propagation lookup direction: given a superseded artifact, who consumed it?
        builder.HasIndex(entity => entity.ArtifactId).HasDatabaseName("ix_dev_workflow_artifact_uses_artifact");
    }
}
