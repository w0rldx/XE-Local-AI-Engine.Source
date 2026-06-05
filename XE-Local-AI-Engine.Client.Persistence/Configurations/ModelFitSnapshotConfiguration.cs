namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class ModelFitSnapshotConfiguration : IEntityTypeConfiguration<ModelFitSnapshot>
{
    public void Configure(EntityTypeBuilder<ModelFitSnapshot> builder)
    {
        builder.ToTable("model_fit_snapshots");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.ApprovedImageId)
               .HasColumnName("approved_image_id");

        builder.Property(entity => entity.Operation)
               .HasColumnName("operation");

        builder.Property(entity => entity.UseCase)
               .HasColumnName("use_case");

        builder.Property(entity => entity.ProviderName)
               .HasColumnName("provider_name");

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.StartedAtUtc)
               .HasColumnName("started_at_utc");

        builder.Property(entity => entity.CompletedAtUtc)
               .HasColumnName("completed_at_utc");

        builder.Property(entity => entity.DurationMs)
               .HasColumnName("duration_ms");

        builder.Property(entity => entity.ExitCode)
               .HasColumnName("exit_code");

        builder.Property(entity => entity.RawJson)
               .HasColumnName("raw_json");

        builder.Property(entity => entity.StderrExcerpt)
               .HasColumnName("stderr_excerpt");

        builder.Property(entity => entity.DiagnosticsJson)
               .HasColumnName("diagnostics_json");

        builder.Property(entity => entity.IsLatestSuccessful)
               .HasColumnName("is_latest_successful");

        builder.Property(entity => entity.CreatedByRunId)
               .HasColumnName("created_by_run_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        // Supports the latest-successful lookup keyed on (operation, use_case, provider_name, model_name) filtered to
        // is_latest_successful.
        builder.HasIndex(entity => new { entity.Operation, entity.UseCase, entity.ProviderName, entity.ModelName, entity.IsLatestSuccessful });

        // A snapshot intentionally has NO enforced FK to its scheduler run (created_by_run_id): runs outlive
        // definitions — same intentional no-FK precedent as scheduled_job_runs -> definition.
    }
}
