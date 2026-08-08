namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class McpServerApiKeyConfiguration : IEntityTypeConfiguration<McpServerApiKey>
{
    public void Configure(EntityTypeBuilder<McpServerApiKey> builder)
    {
        builder.ToTable("mcp_server_api_keys");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Prefix)
               .HasColumnName("prefix");

        builder.Property(entity => entity.KeyHash)
               .HasColumnName("key_hash");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.LastUsedAtUtc)
               .HasColumnName("last_used_at_utc");
    }
}
