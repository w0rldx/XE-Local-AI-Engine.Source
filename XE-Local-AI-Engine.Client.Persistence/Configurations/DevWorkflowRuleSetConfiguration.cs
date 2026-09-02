namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevWorkflowRuleSetConfiguration : IEntityTypeConfiguration<DevWorkflowRuleSet>
{
    public void Configure(EntityTypeBuilder<DevWorkflowRuleSet> builder)
    {
        builder.ToTable("dev_workflow_rule_sets");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(entity => entity.ScopeJson).HasColumnName("scope_json").IsRequired();
        builder.Property(entity => entity.Enabled).HasColumnName("enabled");

        // Encrypted at rest under its own AAD purpose, so a rule-set body can never be presented as a definition graph.
        builder.Property(entity => entity.Body).HasColumnName("body").IsRequired();
        builder.Property(entity => entity.ContentSha256).HasColumnName("content_sha256").HasMaxLength(128).IsRequired();

        // The same concurrency token the definition carries, for the same reason: two edits that both read version N
        // pass the store's read-then-check together, and without the token the later one would silently win.
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");

        // The resolver's working set is "every enabled rule set". No scope index: the scope is a JSON document with no
        // single column to index on, and the filter that follows runs in memory over a handful of rows.
        builder.HasIndex(entity => entity.Enabled).HasDatabaseName("ix_dev_workflow_rule_sets_enabled");
    }
}
