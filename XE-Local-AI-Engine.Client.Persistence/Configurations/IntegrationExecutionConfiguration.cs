namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class IntegrationExecutionConfiguration : IEntityTypeConfiguration<IntegrationExecution>
{
    public void Configure(EntityTypeBuilder<IntegrationExecution> builder)
    {
        builder.ToTable("integration_executions");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id");
        builder.Property(entity => entity.TriggerId).HasColumnName("trigger_id");
        builder.Property(entity => entity.SessionId).HasColumnName("session_id");
        builder.Property(entity => entity.PrincipalId).HasColumnName("principal_id");
        builder.Property(entity => entity.RequestId).HasColumnName("request_id");
        builder.Property(entity => entity.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.KeyPrefix).HasColumnName("key_prefix").HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.InvocationId).HasColumnName("invocation_id");
        builder.Property(entity => entity.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
        builder.Property(entity => entity.ReceivedAtUtc).HasColumnName("received_at_utc");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("started_at_utc");
        builder.Property(entity => entity.EndedAtUtc).HasColumnName("ended_at_utc");
        builder.Property(entity => entity.StopRequestedAtUtc).HasColumnName("stop_requested_at_utc");
        builder.Property(entity => entity.FailureCategory).HasColumnName("failure_category").HasMaxLength(64);
        builder.Property(entity => entity.FailureSummary).HasColumnName("failure_summary").HasMaxLength(1024);
        builder.Property(entity => entity.OutputCount).HasColumnName("output_count").HasDefaultValue(0);
        builder.Property(entity => entity.OutputBytes).HasColumnName("output_bytes").HasDefaultValue(0L);
        builder.Property(entity => entity.LastSequence).HasColumnName("last_sequence");
        builder.Property(entity => entity.Version).HasColumnName("version").IsConcurrencyToken();

        // Uniqueness is PER PRINCIPAL, not global (ruling R4-6): one integrator must not be able to preclaim another's
        // request id and force it a permanent 409. principal_id leading is deliberate — this index is also the access
        // path for the accept transaction's per-principal active count, so no second index is added for it.
        builder.HasIndex(entity => new
        {
            entity.PrincipalId,
            entity.RequestId
        }).IsUnique().HasDatabaseName("ux_integration_executions_principal_request");

        builder.HasIndex(entity => entity.SessionId).HasDatabaseName("ix_integration_executions_session");
        builder.HasIndex(entity => entity.TriggerId).HasDatabaseName("ix_integration_executions_trigger");

        // Leading `status` serves the accept transaction's node-wide active count as well as the admin list.
        builder.HasIndex(entity => new
        {
            entity.Status,
            entity.ReceivedAtUtc
        }).HasDatabaseName("ix_integration_executions_status_received");

        // No index on stop_requested_at_utc or output_bytes: every reader of either already holds the execution id.
    }
}
