namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ExternalAppInstanceConfiguration : IEntityTypeConfiguration<ExternalAppInstance>
{
    public void Configure(EntityTypeBuilder<ExternalAppInstance> builder)
    {
        builder.ToTable("external_app_instances");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id").HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.ManifestVersion).HasColumnName("manifest_version");
        builder.Property(entity => entity.ManifestSnapshotJson).HasColumnName("manifest_snapshot_json").IsRequired();
        builder.Property(entity => entity.DisplayName).HasColumnName("display_name").HasMaxLength(128).IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.DesiredState).HasColumnName("desired_state").HasConversion<string>().HasMaxLength(16);
        builder.Property(entity => entity.RuntimeOverride).HasColumnName("runtime_override").HasMaxLength(16);
        builder.Property(entity => entity.RuntimeProvider).HasColumnName("runtime_provider").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.VariablesJson).HasColumnName("variables_json").IsRequired();
        builder.Property(entity => entity.BridgeToken).HasColumnName("bridge_token");
        builder.Property(entity => entity.PublishedPortsJson).HasColumnName("published_ports_json").IsRequired();
        builder.Property(entity => entity.StoragePath).HasColumnName("storage_path").HasMaxLength(512).IsRequired();
        builder.Property(entity => entity.FailureCategory).HasColumnName("failure_category").HasConversion<string>().HasMaxLength(48);
        builder.Property(entity => entity.FailureSummary).HasColumnName("failure_summary").HasMaxLength(512);
        builder.Property(entity => entity.NeedsRecreate).HasColumnName("needs_recreate").HasDefaultValue(false);
        builder.Property(entity => entity.InstalledAtUtc).HasColumnName("installed_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.StoppedAtUtc).HasColumnName("stopped_at_utc");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(entity => entity.LastSequence).HasColumnName("last_sequence");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        // NOT unique. The schema stays N:1 (decision D13) so a later release can host two instances of one application without a migration; the one-per-application
        // rule is the install gate's, which holds the instance lease across the already-installed check and the insert and is therefore race-free without an index.
        builder.HasIndex(entity => entity.ApplicationId).HasDatabaseName("ix_external_app_instances_application");

        // Leading `status` serves the boot reconciler's and the observer's sweeps as well as the instance list.
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.InstalledAtUtc
        }).HasDatabaseName("ix_external_app_instances_status");
    }
}
