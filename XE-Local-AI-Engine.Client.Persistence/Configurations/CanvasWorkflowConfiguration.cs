namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class CanvasWorkflowConfiguration : IEntityTypeConfiguration<CanvasWorkflow>
{
    public void Configure(EntityTypeBuilder<CanvasWorkflow> builder)
    {
        builder.ToTable("canvas_workflows");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name");

        // BLOB-ness comes from the CLR byte[] type; only the column name is mapped here. The blob is encrypted at rest
        // by the node encryption interceptors (AAD column name "graph_json").
        builder.Property(entity => entity.GraphJson)
               .HasColumnName("graph_json");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name);
    }
}
