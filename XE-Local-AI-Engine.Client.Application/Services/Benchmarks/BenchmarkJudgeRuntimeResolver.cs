namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>One phase's frozen launch vector plus the intent scalars the row records beside it.</summary>
public sealed record BenchmarkFrozenLaunch(BenchmarkLlamaRuntimeSnapshotV1 Runtime, BenchmarkRunLaunchIntent Intent);

// The KV-cache type a launch will actually use: the effective type, whether it was picked explicitly or resolved by
// Auto, and — for Auto — the reason it degraded (null when it did not).
internal sealed record KvCacheResolution(string Effective, string Source, string? Reason);

/// <summary>
///     Resolves what one benchmark phase will launch with: the profile replay, the KV-cache type it will run with, and
///     the launch identity that vector is INTENDED to produce. Shared by the primary freeze and the judge, because both
///     phases launch the same binary and must agree about what it accepts.
/// </summary>
public interface IBenchmarkPhaseLaunchResolver
{
    /// <summary>
    ///     What the llama-server binary this node would launch accepts, or <see langword="null" /> when it could not be
    ///     acquired — recorded as "not inspected" rather than failing, so Auto stays on f16 and the spawn reports the
    ///     real acquisition failure.
    /// </summary>
    Task<LlamaServerLaunchCapabilities?> InspectAsync(CancellationToken cancellationToken);

    /// <summary>The variant the inspection settled on, or the selector's answer when nothing was inspected.</summary>
    Task<GpuVariant> SelectVariantAsync(LlamaServerLaunchCapabilities? capabilities, CancellationToken cancellationToken);

    /// <param name="requestedKvCacheType">The type the caller asked for, or <see langword="null" /> for Auto.</param>
    Task<BenchmarkFrozenLaunch> ResolveAsync(string modelName,
        int requiredContextTokens,
        string? requestedKvCacheType,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class BenchmarkPhaseLaunchResolver(
    IInferenceProfileResolver inferenceProfiles,
    IGpuVariantSelector variantSelector,
    ILlamaServerLaunchCapabilityInspector launchCapabilities,
    ILlamaServerLaunchFallbackStore launchFallbackStore,
    ILlamaServerLaunchPolicy launchPolicy,
    LlamaServerLaunchPolicyOptions launchPolicyOptions) : IBenchmarkPhaseLaunchResolver
{
    /// <summary>Auto stayed on f16 because the node selected a CPU llama.cpp build.</summary>
    public const string AutoReasonCpuVariant = "cpu-variant";

    /// <summary>Auto stayed on f16 because the selected binary could not be interrogated.</summary>
    public const string AutoReasonProbeUnavailable = "probe-unavailable";

    /// <summary>Auto stayed on f16 because the selected binary does not advertise the quantized vector.</summary>
    public const string AutoReasonManifestUnsupported = "manifest-unsupported";

    /// <summary>Auto stayed on f16 because the optimized config was previously recorded as unable to start here.</summary>
    public const string AutoReasonFallbackDisabled = "fallback-disabled";

    private readonly IInferenceProfileResolver _inferenceProfiles = inferenceProfiles ?? throw new ArgumentNullException(nameof(inferenceProfiles));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    private readonly ILlamaServerLaunchCapabilityInspector _launchCapabilities =
        launchCapabilities ?? throw new ArgumentNullException(nameof(launchCapabilities));

    private readonly ILlamaServerLaunchFallbackStore _launchFallbackStore =
        launchFallbackStore ?? throw new ArgumentNullException(nameof(launchFallbackStore));

    private readonly ILlamaServerLaunchPolicy _launchPolicy = launchPolicy ?? throw new ArgumentNullException(nameof(launchPolicy));

    private readonly LlamaServerLaunchPolicyOptions _launchPolicyOptions =
        launchPolicyOptions ?? throw new ArgumentNullException(nameof(launchPolicyOptions));

    public async Task<LlamaServerLaunchCapabilities?> InspectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _launchCapabilities.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException)
        {
            return null;
        }
    }

    public async Task<GpuVariant> SelectVariantAsync(LlamaServerLaunchCapabilities? capabilities, CancellationToken cancellationToken) =>
        capabilities?.Variant ?? await _variantSelector.SelectVariantAsync(cancellationToken).ConfigureAwait(false);

    public async Task<BenchmarkFrozenLaunch> ResolveAsync(string modelName,
        int requiredContextTokens,
        string? requestedKvCacheType,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken)
    {
        var resolved = await _inferenceProfiles.ResolveAsync(modelName, ModelRole.Chat, variant, cancellationToken).ConfigureAwait(false);
        if (resolved.ExploreMode)
        {
            resolved = ResolvedLaunchArguments.Replay(requiredContextTokens);
        }

        if (resolved.CtxSize < requiredContextTokens)
        {
            throw new BenchmarkEligibilityException("The resolved llama.cpp runtime context is smaller than the benchmark requirement.");
        }

        // Auto is the only decision the fallback store participates in: an explicit pick is answered from the
        // manifest alone, so an operator can always retry a config a previous host state disabled.
        // The store is keyed per (backend, KV type) and this class has no type of its own — it is the code CHOOSING
        // one. It asks about the node's selected type, i.e. the type Auto would otherwise pick, which preserves the
        // pre-slice meaning exactly: Auto avoids a config this host has proven cannot reach readiness.
        var optimizedDisabled = requestedKvCacheType is null
                                && variant != GpuVariant.Cpu
                                && await _launchFallbackStore.IsOptimizedConfigDisabledAsync(variant, _launchPolicyOptions.KvCacheType, cancellationToken).ConfigureAwait(false);
        var (effective, source, reason) = ResolveKvCacheType(requestedKvCacheType, variant, capabilities, optimizedDisabled);
        var applied = BenchmarkKvCacheType.Apply(resolved, effective);

        // The plan the supervisor will build for this spawn: a benchmark launch applies no launch policy, so a GPU
        // replay gets a null plan and a CPU replay the two args a CPU build can honour.
        var plan = variant == GpuVariant.Cpu ? _launchPolicy.ResolveCpuReplayPlan(applied) : (LlamaServerLaunchPlan?)null;
        var policy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1;
        var intendedIdentity = LlamaServerLaunchProjection.From(variant, applied, plan, ModelRole.Chat, policy.ChatCacheReuse, policy.ChatCacheRamMiB)
                                                          .ComputeIdentity();
        return new BenchmarkFrozenLaunch(new BenchmarkLlamaRuntimeSnapshotV1(variant,
                applied.CtxSize,
                applied.NGpuLayers,
                applied.TensorSplit,
                applied.OverrideTensor,
                applied.KvTypeK,
                applied.KvTypeV,
                applied.FlashAttn,
                policy),
            new BenchmarkRunLaunchIntent(BenchmarkLaunchBackend.VariantName(variant),
                effective,
                source,
                reason,
                BenchmarkKvCacheType.IsQuantized(effective) ? LlamaServerLaunchProjection.FlashAttentionOn : LlamaServerLaunchProjection.FlashAttentionAuto,
                intendedIdentity,
                capabilities?.ManifestSha256,
                // Stamped once, here, at freeze. Never recomputed at execution: the snapshot carries no CPU thread
                // inputs, so re-projecting would adopt the executing box's conditions as historical intent.
                LlamaServerLaunchProjection.IdentitySchemeVersion));
    }

    /// <summary>
    ///     The KV-cache type this launch will actually use. Auto degrades to <c>f16</c> with a recorded reason; an
    ///     explicit quantized pick the selected binary cannot be shown to accept is refused (422) rather than
    ///     discovered as a failed spawn.
    /// </summary>
    private static KvCacheResolution ResolveKvCacheType(string? requested,
        GpuVariant variant,
        LlamaServerLaunchCapabilities? capabilities,
        bool optimizedDisabled)
    {
        var probed = capabilities is { ProbeSucceeded: true };
        var isGpu = variant != GpuVariant.Cpu;
        if (requested is null)
        {
            if (!isGpu)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonCpuVariant);
            }

            if (!probed)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonProbeUnavailable);
            }

            if (optimizedDisabled)
            {
                return new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonFallbackDisabled);
            }

            return Accepts(capabilities!, BenchmarkKvCacheType.Q8_0)
                ? new KvCacheResolution(BenchmarkKvCacheType.Q8_0, BenchmarkKvCacheType.SourceAuto, null)
                : new KvCacheResolution(BenchmarkKvCacheType.F16, BenchmarkKvCacheType.SourceAuto, AutoReasonManifestUnsupported);
        }

        if (!BenchmarkKvCacheType.IsQuantized(requested))
        {
            return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
        }

        if (!isGpu)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException($"A {requested} KV cache needs a GPU llama.cpp build, and this node selected the CPU build. Pick f16.");
        }

        if (!probed)
        {
            throw new BenchmarkUnsupportedKvCacheTypeException(
                $"The selected llama.cpp binary could not be inspected, so a {requested} KV cache cannot be confirmed. Pick f16 or repair the llama.cpp runtime.");
        }

        if (!Accepts(capabilities!, requested))
        {
            throw new BenchmarkUnsupportedKvCacheTypeException($"The selected llama.cpp binary does not accept a {requested} KV cache with flash attention. Pick f16.");
        }

        return new KvCacheResolution(requested, BenchmarkKvCacheType.SourceExplicit, null);
    }

    private static bool Accepts(LlamaServerLaunchCapabilities capabilities, string cacheType) =>
        capabilities.SupportsCacheTypeK(cacheType)
        && capabilities.SupportsCacheTypeV(cacheType)
        && capabilities.SupportsFlashAttentionMode(LlamaServerLaunchProjection.FlashAttentionOn);
}

/// <summary>
///     What the judge will actually launch with, resolved once per attempt at enqueue and frozen onto that attempt.
///     Deliberately NOT part of the policy hash: a runtime update changes this underneath the operator, and it must
///     make attempts <em>unranked together</em> (a new cohort key) rather than invalidate the policy.
/// </summary>
public sealed record BenchmarkJudgeRuntimeV1(
    int SchemaVersion,
    BenchmarkInstalledModelSnapshotV1 Model,
    int RequestedContextTokens,
    BenchmarkLlamaRuntimeSnapshotV1 Runtime,
    BenchmarkSamplingSnapshotV1 Sampling)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record BenchmarkJudgeRuntimeResolution(BenchmarkJudgeRuntimeV1 Runtime, BenchmarkRunLaunchIntent Intent);

public interface IBenchmarkJudgeRuntimeResolver
{
    /// <summary>
    ///     Resolves the judge launch vector for <paramref name="policy" />. Throws when the policy's model is gone, is
    ///     no longer eligible, or its content fingerprint has moved — the caller records that as a failed attempt.
    /// </summary>
    Task<BenchmarkJudgeRuntimeResolution> ResolveAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class BenchmarkJudgeRuntimeResolver(
    IBenchmarkInstalledModelLeaseProvider installedModels,
    IBenchmarkPhaseLaunchResolver launchResolver) : IBenchmarkJudgeRuntimeResolver
{
    private readonly IBenchmarkInstalledModelLeaseProvider _installedModels = installedModels ?? throw new ArgumentNullException(nameof(installedModels));
    private readonly IBenchmarkPhaseLaunchResolver _launchResolver = launchResolver ?? throw new ArgumentNullException(nameof(launchResolver));

    public async Task<BenchmarkJudgeRuntimeResolution> ResolveAsync(BenchmarkJudgePolicyV1 policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var lease = await _installedModels.AcquireAsync(policy.Model.ModelName, cancellationToken).ConfigureAwait(false);
        BenchmarkModelEligibility.ValidateJudge(lease.Snapshot);
        if (!string.Equals(lease.Snapshot.ModelContentFingerprint, policy.Model.ModelContentFingerprint, StringComparison.Ordinal))
        {
            throw new BenchmarkEligibilityException("The installed judge model changed after the judge policy was created.");
        }

        var model = BenchmarkInstalledModelSnapshotMapper.ToSnapshot(lease.Snapshot);
        var capabilities = await _launchResolver.InspectAsync(cancellationToken).ConfigureAwait(false);
        var variant = await _launchResolver.SelectVariantAsync(capabilities, cancellationToken).ConfigureAwait(false);

        // The judge is scoring, not being measured: it never takes a run's KV pick, only Auto.
        var launch = await _launchResolver.ResolveAsync(model.ModelName,
                                              policy.RequestedContextTokens,
                                              requestedKvCacheType: null,
                                              capabilities,
                                              variant,
                                              cancellationToken)
                                          .ConfigureAwait(false);
        return new BenchmarkJudgeRuntimeResolution(new BenchmarkJudgeRuntimeV1(BenchmarkJudgeRuntimeV1.CurrentSchemaVersion,
                model,
                policy.RequestedContextTokens,
                launch.Runtime,
                BenchmarkFrozenPolicies.DeterministicSampling()),
            launch.Intent);
    }
}

/// <summary>
///     The at-rest form of the judge policy and the per-attempt judge runtime. The policy is stored in its CANONICAL
///     form, so the stored bytes re-hash to the stored <c>PolicyHash</c> and a revision can be verified without a
///     second serializer agreeing with the first.
/// </summary>
public static class BenchmarkJudgeSerialization
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = false
    };

    public static byte[] SerializePolicy(BenchmarkJudgePolicyV1 policy) =>
        Encoding.UTF8.GetBytes(BenchmarkJudgePolicyCanonicalizer.ToCanonicalJson(policy));

    /// <summary>
    ///     The stored policy, validated STRUCTURALLY only. A version constant moving must never make an
    ///     already-stored revision unreadable: when the strict validator ran here, bumping
    ///     <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> made `GET benchmarks/projects/{id}` and the
    ///     project export throw, the whole project header disappeared from the UI, and it took the re-save control
    ///     that heals the revision with it. Write and execution re-validate with <c>strictVersions: true</c>.
    /// </summary>
    public static BenchmarkJudgePolicyV1 DeserializePolicy(ReadOnlySpan<byte> payload)
    {
        try
        {
            var policy = JsonSerializer.Deserialize<BenchmarkJudgePolicyV1>(payload, Options)
                         ?? throw new BenchmarkSnapshotException("The stored judge policy is empty.");
            BenchmarkJudgePolicyValidator.Validate(policy, strictVersions: false);
            return policy;
        }
        catch (JsonException exception)
        {
            throw new BenchmarkSnapshotException("The stored judge policy is invalid.")
            {
                Source = exception.Source
            };
        }
    }

    public static byte[] SerializeResult(BenchmarkJudgeResultV2 result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, Options);

    /// <summary>
    ///     The stored verdict, or <see langword="null" /> when the payload is absent or unreadable. Reads through the
    ///     WRITER's own options: these are camelCase, so a reader that re-derives default options binds every property
    ///     to its default and hands the API a zeroed verdict instead of failing.
    /// </summary>
    public static BenchmarkJudgeResultV2? DeserializeResult(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BenchmarkJudgeResultV2>(value.Span, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] SerializeRuntime(BenchmarkJudgeRuntimeV1 runtime) =>
        JsonSerializer.SerializeToUtf8Bytes(runtime, Options);

    public static BenchmarkJudgeRuntimeV1 DeserializeRuntime(ReadOnlySpan<byte> payload)
    {
        try
        {
            var runtime = JsonSerializer.Deserialize<BenchmarkJudgeRuntimeV1>(payload, Options)
                          ?? throw new BenchmarkSnapshotException("The frozen judge runtime is empty.");
            if (runtime.SchemaVersion != BenchmarkJudgeRuntimeV1.CurrentSchemaVersion
                || runtime.Model is null
                || runtime.Runtime is null
                || runtime.Sampling is null
                || runtime.RequestedContextTokens <= 0)
            {
                throw new BenchmarkSnapshotException("The frozen judge runtime is unsupported.");
            }

            return runtime;
        }
        catch (JsonException exception)
        {
            throw new BenchmarkSnapshotException("The frozen judge runtime is invalid.")
            {
                Source = exception.Source
            };
        }
    }
}

/// <summary>Maps a live installed-model snapshot into the frozen benchmark projection of it.</summary>
internal static class BenchmarkInstalledModelSnapshotMapper
{
    public static BenchmarkInstalledModelSnapshotV1 ToSnapshot(InstalledModelSnapshot source) =>
        new(source.ModelName,
            source.RegistryRevision,
            source.RegistryAliases.Select(static alias => new BenchmarkRegistryAliasSnapshotV1(alias.ModelName, alias.RegistryRevision)).ToArray(),
            source.RegistryAliasSetHash,
            source.Members.Select(static member => new BenchmarkPhysicalMemberSnapshotV1(member.RelativePath,
                      member.Role,
                      member.SizeBytes,
                      member.Sha256,
                      member.OwningAliases.ToArray(),
                      member.Required,
                      member.MetadataSchemaVersion,
                      member.MemberFingerprint))
                  .ToArray(),
            source.PhysicalMemberSetHash,
            source.Origin,
            source.ProviderName!,
            source.ProviderMappingRevision,
            source.RepoId,
            source.SourceRevision,
            Path.GetFileName(source.Members.First(static member => member.Role == InstalledModelPhysicalMemberRole.Weight).RelativePath),
            source.Quantization,
            source.Role switch
            {
                GgufRole.Chat => "chat",
                GgufRole.Embedding => "embedding",
                GgufRole.Draft => "draft",
                _ => "unknown"
            },
            source.ModelContentFingerprint);
}
