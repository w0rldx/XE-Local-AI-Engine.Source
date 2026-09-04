namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class GraphWorkflowDefinitionConfiguration : IEntityTypeConfiguration<GraphWorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<GraphWorkflowDefinition> builder)
    {
        builder.ToTable("graph_workflow_definitions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description").HasMaxLength(1024);

        // BLOB-ness comes from the CLR byte[] type; the blob is encrypted at rest by the node encryption interceptors
        // under its own AAD column name, so a definition graph can never be presented as a run's pinned graph.
        builder.Property(entity => entity.GraphJson).HasColumnName("graph_json").IsRequired();
        builder.Property(entity => entity.GraphHash).HasColumnName("graph_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.NodeCount).HasColumnName("node_count");
        builder.Property(entity => entity.SchemaVersion).HasColumnName("schema_version");

        // A concurrency token, exactly as the run row is: the store's read-then-check is a fast answer for the common
        // stale PUT, but two edits that both read the same version pass it together, and without the token the later
        // one would silently overwrite the earlier instead of being told it lost.
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name).HasDatabaseName("ix_graph_workflow_definitions_name");
    }
}
