namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GraphWorkflowRunEventConfiguration : IEntityTypeConfiguration<GraphWorkflowRunEvent>
{
    public void Configure(EntityTypeBuilder<GraphWorkflowRunEvent> builder)
    {
        builder.ToTable("graph_workflow_run_events");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.RunId).HasColumnName("run_id");
        builder.Property(entity => entity.Seq).HasColumnName("seq");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.NodeKey).HasColumnName("node_key").HasMaxLength(64);
        builder.Property(entity => entity.DetailJson).HasColumnName("detail_json");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");

        builder.HasOne<GraphWorkflowRun>().WithMany().HasForeignKey(entity => entity.RunId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => new
        {
            entity.RunId,
            entity.Seq
        }).IsUnique().HasDatabaseName("ux_graph_workflow_run_events_run_seq");
    }
}
