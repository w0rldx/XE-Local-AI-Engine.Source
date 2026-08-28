namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowDefinitionConfiguration : IEntityTypeConfiguration<DevWorkflowDefinition>
{
    public void Configure(EntityTypeBuilder<DevWorkflowDefinition> builder)
    {
        builder.ToTable("dev_workflow_definitions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();

        // BLOB-ness comes from the CLR byte[] type; the blob is encrypted at rest by the node encryption interceptors
        // under its own AAD column name, so a definition graph can never be presented as a run's pinned graph.
        builder.Property(entity => entity.GraphJson).HasColumnName("graph_json").IsRequired();
        builder.Property(entity => entity.GraphHash).HasColumnName("graph_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.NodeCount).HasColumnName("node_count");
        builder.Property(entity => entity.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.SeedSlug).HasColumnName("seed_slug").HasMaxLength(128);
        builder.Property(entity => entity.Archived).HasColumnName("archived");
        builder.Property(entity => entity.Version).HasColumnName("version");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name).HasDatabaseName("ix_dev_workflow_definitions_name");

        // The seed slug is the idempotency key for a re-seed, so it is unique — but only among seeded rows that carry
        // one (manual rows leave it null), hence the filtered unique index.
        builder.HasIndex(entity => entity.SeedSlug)
               .IsUnique()
               .HasFilter("\"seed_slug\" IS NOT NULL")
               .HasDatabaseName("ux_dev_workflow_definitions_seed_slug");

        builder.HasIndex(entity => new
        {
            entity.Archived,
            entity.Name
        }).HasDatabaseName("ix_dev_workflow_definitions_archived");
    }
}
