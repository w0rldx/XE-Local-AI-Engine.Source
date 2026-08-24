namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class AgentWorkSessionArtifactConfiguration : IEntityTypeConfiguration<AgentWorkSessionArtifact>
{
    public void Configure(EntityTypeBuilder<AgentWorkSessionArtifact> builder)
    {
        builder.ToTable("agent_work_session_artifacts");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.Sequence).HasColumnName("sequence");
        builder.Property(entity => entity.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);

        // Name, media type and digest stay plaintext: the name is the replace key, and the digest is compared. The
        // bytes themselves never reach this table — they live encrypted under IWorkSessionArtifactBlobStore.
        builder.Property(entity => entity.Name).HasColumnName("name").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.MediaType).HasColumnName("media_type").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.ContentSha256).HasColumnName("content_sha256").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.SizeBytes).HasColumnName("size_bytes");
        builder.Property(entity => entity.IsValid).HasColumnName("is_valid");
        builder.Property(entity => entity.ManagedReference).HasColumnName("managed_reference").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.CreatedStep).HasColumnName("created_step");
        builder.HasOne<AgentWorkSession>().WithMany().HasForeignKey(entity => entity.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Sequence
        }).HasDatabaseName("ix_agent_work_session_artifacts_session_sequence");
        builder.HasIndex(entity => new
        {
            entity.SessionId,
            entity.Name
        }).IsUnique().HasDatabaseName("ux_agent_work_session_artifacts_session_name");
    }
}
