namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class IntegrationApiKeyConfiguration : IEntityTypeConfiguration<IntegrationApiKey>
{
    public void Configure(EntityTypeBuilder<IntegrationApiKey> builder)
    {
        builder.ToTable("integration_api_keys");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.PrincipalId).HasColumnName("principal_id");
        builder.Property(entity => entity.KeyPrefix).HasColumnName("key_prefix").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.KeyHash).HasColumnName("key_hash").IsRequired();
        builder.Property(entity => entity.Label).HasColumnName("label").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.AllowedTriggerIdsJson).HasColumnName("allowed_trigger_ids_json");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.LastUsedAtUtc).HasColumnName("last_used_at_utc");
        builder.Property(entity => entity.RevokedAtUtc).HasColumnName("revoked_at_utc");

        // The auth lookup, and the row the accept transaction re-reads for revocation. No index on principal_id: no
        // caller lists keys by principal.
        builder.HasIndex(entity => entity.KeyPrefix).IsUnique().HasDatabaseName("ux_integration_api_keys_prefix");
    }
}
