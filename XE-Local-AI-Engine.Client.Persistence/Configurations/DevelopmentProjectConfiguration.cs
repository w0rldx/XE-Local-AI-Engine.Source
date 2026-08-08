namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class DevelopmentProjectConfiguration : IEntityTypeConfiguration<DevelopmentProject>
{
    public void Configure(EntityTypeBuilder<DevelopmentProject> builder)
    {
        builder.ToTable("development_projects");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.Objective).HasColumnName("objective").IsRequired();
        builder.Property(entity => entity.SelectedFolderId).HasColumnName("selected_folder_id");
        builder.Property(entity => entity.RepositoryIdentityHash).HasColumnName("repository_identity_hash").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.BaseBranch).HasColumnName("base_branch").HasMaxLength(255).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.EgressPolicy).HasColumnName("egress_policy").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.CoderModelId).HasColumnName("coder_model_id").HasMaxLength(255);
        builder.Property(entity => entity.ReviewerModelId).HasColumnName("reviewer_model_id").HasMaxLength(255);
        builder.Property(entity => entity.MaxTokens).HasColumnName("max_tokens");
        builder.Property(entity => entity.MaxDurationSeconds).HasColumnName("max_duration_seconds");

        // Deliberately PLAINTEXT, unlike the encrypted byte[] columns on DevelopmentTask/DevelopmentArtifact that go
        // through NodeEncryptionSaveChangesInterceptor. An encrypted column cannot be indexed, filtered, or
        // digest-compared, and the command profile is non-secret operator-confirmed configuration (executable names,
        // argument vectors, timeouts, glob patterns) — not credentials. Do not "fix" this to encrypted, and do not add
        // it to NodeEncryptionSaveChangesInterceptor / NodeEncryptionMaterializationInterceptor.
        builder.Property(entity => entity.CommandProfileJson).HasColumnName("command_profile_json");
        builder.Property(entity => entity.ConfigurationVersion).HasColumnName("configuration_version");
        builder.Property(entity => entity.TrustedRepositoryAcknowledged).HasColumnName("trusted_repository_acknowledged");
        builder.Property(entity => entity.TrustedRepositoryPolicyVersion).HasColumnName("trusted_repository_policy_version");
        builder.Property(entity => entity.TrustedRepositoryAcknowledgedAtUtc).HasColumnName("trusted_repository_acknowledged_at_utc");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();
        builder.HasIndex(entity => entity.RepositoryIdentityHash).HasDatabaseName("ix_development_projects_repository_identity_hash");
        builder.HasIndex(entity => entity.SelectedFolderId).HasDatabaseName("ix_development_projects_selected_folder_id");
        builder.HasOne<NodeSelectedFolder>()
               .WithMany()
               .HasForeignKey(entity => entity.SelectedFolderId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
