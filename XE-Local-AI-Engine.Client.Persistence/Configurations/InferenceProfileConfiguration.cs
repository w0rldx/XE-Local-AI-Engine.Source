namespace XE_Local_AI_Engine.Client.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed class InferenceProfileConfiguration : IEntityTypeConfiguration<InferenceProfile>
{
    public void Configure(EntityTypeBuilder<InferenceProfile> builder)
    {
        builder.ToTable("inference_profiles");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.MachineKey)
               .HasColumnName("machine_key");

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name");

        builder.Property(entity => entity.Role)
               .HasColumnName("role");

        builder.Property(entity => entity.Backend)
               .HasColumnName("backend");

        builder.Property(entity => entity.LlamacppBuild)
               .HasColumnName("llamacpp_build");

        builder.Property(entity => entity.Quant)
               .HasColumnName("quant");

        builder.Property(entity => entity.CtxSize)
               .HasColumnName("ctx_size");

        builder.Property(entity => entity.NGpuLayers)
               .HasColumnName("n_gpu_layers");

        builder.Property(entity => entity.TensorSplit)
               .HasColumnName("tensor_split");

        builder.Property(entity => entity.OverrideTensor)
               .HasColumnName("override_tensor");

        builder.Property(entity => entity.KvTypeK)
               .HasColumnName("kv_type_k");

        builder.Property(entity => entity.KvTypeV)
               .HasColumnName("kv_type_v");

        builder.Property(entity => entity.FlashAttn)
               .HasColumnName("flash_attn");

        builder.Property(entity => entity.NParams)
               .HasColumnName("n_params");

        builder.Property(entity => entity.IsMoe)
               .HasColumnName("is_moe");

        builder.Property(entity => entity.ExpertCount)
               .HasColumnName("expert_count");

        builder.Property(entity => entity.LaunchPolicyFingerprintVersion)
               .HasColumnName("launch_policy_fingerprint_version");

        builder.Property(entity => entity.LaunchPolicyFingerprint)
               .HasColumnName("launch_policy_fingerprint");

        builder.Property(entity => entity.GlobalFreeVramAtFreezeBytes)
               .HasColumnName("global_free_vram_at_freeze_bytes");
        builder.Property(entity => entity.ProcessBudgetVramAtFreezeBytes)
               .HasColumnName("process_budget_vram_at_freeze_bytes");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.BenchmarkSnapshotId)
               .HasColumnName("benchmark_snapshot_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // One live config per natural key (machine_key, model_name, role, backend): the upsert relies on this being
        // unique so re-exploring the same model overwrites the single config rather than inserting a duplicate.
        builder.HasIndex(entity => new
               {
                   entity.MachineKey,
                   entity.ModelName,
                   entity.Role,
                   entity.Backend
               })
               .IsUnique();

        // Supports the invalidation sweep and status-filtered lists.
        builder.HasIndex(entity => entity.Status);

        // A profile references its justifying benchmark snapshot without an enforced FK: snapshots outlive profiles
        // (same intentional no-FK precedent as model_fit_snapshots -> scheduler run).
    }
}
