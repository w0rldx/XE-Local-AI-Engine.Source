namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     The allow-listed, content-free projection of ONE llama-server spawn's launch shape: the context window,
///     placement, KV-cache/flash-attention vector, thread and batch sizes, and the role-derived serving flags. It is the
///     single canonical description both the argument-vector builder emits from and a caller hashes, so a launch that
///     was intended and a launch that happened are described by the same values.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Nothing addressable appears here.</strong> No model/executable path, no host, no port — a receipt
///         built around this projection is safe to persist and display. <see cref="TensorSplit" /> and
///         <see cref="OverrideTensor" /> are llama.cpp placement expressions (for example <c>0.6,0.4</c>,
///         <c>exps=CPU</c>), not filesystem locations.
///     </para>
///     <para>
///         <strong>Property order is the canonical serialization order</strong> that <see cref="ComputeIdentity" />
///         hashes. Reordering, renaming, adding or removing a member changes every identity this type has ever
///         produced; the identity pin test in <c>LlamaServerLaunchProjectionTests</c> fails loudly when that happens.
///     </para>
/// </remarks>
/// <param name="AutoFit">Whether the spawn hands placement to llama.cpp auto-fit (<c>--fit on</c>); GPU explore only.</param>
/// <param name="Metrics">Whether the spawn exposes <c>/metrics</c>; every GPU spawn does, a CPU spawn never does.</param>
/// <param name="ContextTokens">The <c>-c</c> value emitted, or <see langword="null" /> when the spawn emits none.</param>
/// <param name="GpuLayers">The replayed <c>--n-gpu-layers</c> value, or <see langword="null" />.</param>
/// <param name="TensorSplit">The replayed <c>-ts</c> expression, or <see langword="null" />.</param>
/// <param name="OverrideTensor">The replayed <c>-ot</c> expression, or <see langword="null" />.</param>
/// <param name="CpuMoe">Whether every Mixture-of-Experts weight is pinned to system RAM (<c>--cpu-moe</c>); GPU explore only, and never together with <see cref="OverrideTensor" />.</param>
/// <param name="KvCacheTypeK">The <c>-ctk</c> element type, or <see langword="null" /> when KV stays at the f16 default.</param>
/// <param name="KvCacheTypeV">The <c>-ctv</c> element type; always equal to <see cref="KvCacheTypeK" /> or null with it.</param>
/// <param name="FlashAttentionMode"><c>on</c> when the fused flash-attention path is pinned, otherwise <c>auto</c> (no flag emitted).</param>
/// <param name="Threads">The CPU generation thread count (<c>-t</c>), or <see langword="null" /> on a GPU build.</param>
/// <param name="ThreadsBatch">The CPU prompt-batch thread count (<c>-tb</c>), or <see langword="null" /> on a GPU build.</param>
/// <param name="BatchSize">The pooled-role logical batch size (<c>-b</c>), or <see langword="null" /> for chat.</param>
/// <param name="UbatchSize">The pooled-role physical micro-batch size (<c>-ub</c>), or <see langword="null" /> for chat.</param>
/// <param name="Parallel">The <c>--parallel</c> slot count (pinned to 1 by the single-slot serving design).</param>
/// <param name="CacheReuse">The chat prompt-prefix reuse window (<c>--cache-reuse</c>), or <see langword="null" /> when unset.</param>
/// <param name="CacheRamMiB">The host prompt-cache budget (<c>--cache-ram</c>); 0 disables it.</param>
/// <param name="Jinja">Whether the chat template/tool grammar is enabled (<c>--jinja</c>); chat role only.</param>
/// <param name="Pooling">The pooled-role <c>--pooling</c> value (<c>mean</c>/<c>rank</c>), or <see langword="null" /> for chat.</param>
public sealed record LlamaServerLaunchProjection(
    bool AutoFit,
    bool Metrics,
    int? ContextTokens,
    int? GpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    bool CpuMoe,
    string? KvCacheTypeK,
    string? KvCacheTypeV,
    string FlashAttentionMode,
    int? Threads,
    int? ThreadsBatch,
    int? BatchSize,
    int? UbatchSize,
    int Parallel,
    int? CacheReuse,
    int CacheRamMiB,
    bool Jinja,
    string? Pooling)
{
    /// <summary>
    ///     The version of the identity SCHEME this type computes. Governed by the same contract as the member list
    ///     above: <strong>the two move together</strong>. A hash computed under one scheme says nothing about a hash
    ///     computed under another, so persisted intents record the scheme they were frozen under and work that
    ///     straddles a change is failed rather than compared. A persisted <see langword="null" /> reads as <c>1</c>.
    /// </summary>
    public const int IdentitySchemeVersion = 2;

    /// <summary>Flash-attention left to llama.cpp (no <c>-fa</c> flag emitted) — the f16 KV default.</summary>
    public const string FlashAttentionAuto = "auto";

    /// <summary>Flash-attention pinned on, which explicit/quantized KV cache types require.</summary>
    public const string FlashAttentionOn = "on";

    /// <summary>
    ///     The canonical serializer behind <see cref="ComputeIdentity" />. Every member is written, including nulls, so
    ///     adding a value to a previously-unset field always changes the identity.
    /// </summary>
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    ///     Projects the launch shape of one spawn. Pure: the same inputs always produce the same projection, which is
    ///     what lets a caller compute the intended shape ahead of the spawn and compare it to what the spawn recorded.
    /// </summary>
    /// <param name="variant">The llama.cpp build the spawn runs on — a CPU build emits no GPU placement or KV args.</param>
    /// <param name="resolved">The spawn's explore/replay launch arguments.</param>
    /// <param name="plan">
    ///     The launch-policy plan layered on top, or <see langword="null" /> for a spawn built with no policy.
    /// </param>
    /// <param name="role">The serving role, which decides the pooling/jinja/batch flags.</param>
    /// <param name="chatCacheReuse">The chat <c>--cache-reuse</c> window; 0 emits no flag.</param>
    /// <param name="chatCacheRamMiB">The chat host prompt-cache budget in MiB.</param>
    public static LlamaServerLaunchProjection From(GpuVariant variant,
        ResolvedLaunchArguments resolved,
        LlamaServerLaunchPlan? plan,
        ModelRole role = ModelRole.Chat,
        int chatCacheReuse = 0,
        int chatCacheRamMiB = 0)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var isGpu = variant != GpuVariant.Cpu;
        var isGpuExplore = isGpu && resolved.ExploreMode;
        var isGpuReplay = isGpu && !resolved.ExploreMode;

        // A GPU replay pins its own -c verbatim; every other mode takes the policy context (and emits none without one).
        var contextTokens = isGpuReplay ? resolved.CtxSize : plan?.RequestedContextTokens;

        // The optimized policy vector (explore) and a frozen profile's own types (replay) are the two independent
        // sources of an explicit KV cache type; both imply the fused flash-attention path.
        string? kvCacheTypeK = null;
        string? kvCacheTypeV = null;
        if (isGpuExplore && plan is { UseKvCacheQuantization: true } quantizedPlan && !string.IsNullOrWhiteSpace(quantizedPlan.KvCacheType))
        {
            kvCacheTypeK = quantizedPlan.KvCacheType;
            kvCacheTypeV = quantizedPlan.KvCacheType;
        }
        else if (isGpuReplay && !string.IsNullOrWhiteSpace(resolved.KvTypeK) && !string.IsNullOrWhiteSpace(resolved.KvTypeV))
        {
            kvCacheTypeK = resolved.KvTypeK;
            kvCacheTypeV = resolved.KvTypeV;
        }

        // A pooled role sizes -b/-ub to whichever context this spawn actually emits, so anything that fits the
        // advertised window survives one physical micro-batch.
        var isPooledRole = role is ModelRole.Embedding or ModelRole.Reranker;
        var pooledBatch = plan?.RequestedContextTokens ?? resolved.CtxSize;
        int? batchSize = isPooledRole && pooledBatch > 0 ? pooledBatch : null;

        var isChat = role == ModelRole.Chat;

        return new LlamaServerLaunchProjection(isGpuExplore,
            isGpu,
            contextTokens,
            isGpuReplay ? resolved.NGpuLayers : null,
            isGpuReplay ? NullIfBlank(resolved.TensorSplit) : null,
            isGpuReplay ? NullIfBlank(resolved.OverrideTensor) : null,
            isGpuExplore && plan?.CpuMoe == true,
            kvCacheTypeK,
            kvCacheTypeV,
            kvCacheTypeK is null ? FlashAttentionAuto : FlashAttentionOn,
            isGpu ? null : plan?.CpuThreads,
            isGpu ? null : plan?.CpuThreadsBatch,
            batchSize,
            batchSize,
            Parallel: 1,
            isChat && chatCacheReuse > 0 ? chatCacheReuse : null,
            isChat ? chatCacheRamMiB : 0,
            isChat,
            role switch
            {
                ModelRole.Embedding => "mean",
                ModelRole.Reranker => "rank",
                _ => null
            });
    }

    /// <summary>
    ///     The EFFECTIVE launch shape, read back out of the argument vector the process was actually started with.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="From" /> describes what a spawn INTENDED before the capability gate ran. The gate can drop
    ///         optional flags a runtime does not advertise (<c>--cache-reuse</c>, <c>--metrics</c>, <c>-lv</c>), and an
    ///         operator's per-model extra arguments are appended after it — so the intended shape can name a flag the
    ///         process never received, or miss one it did. Re-reading the final argv is the only description that is a
    ///         fact rather than a derivation. Last-wins for a repeated scalar flag, matching llama.cpp itself.
    ///     </para>
    ///     <para>
    ///         Tolerant and pure: an unknown argument is ignored, and a malformed value for an allow-listed numeric flag
    ///         returns <see langword="null" /> so the caller can fall back rather than record a wrong fact. Only the
    ///         allow-listed flags below are read; nothing addressable (<c>-m</c>, <c>--host</c>, <c>--port</c>) is.
    ///     </para>
    /// </remarks>
    /// <param name="arguments">The final argument vector handed to the process.</param>
    /// <returns>The effective projection, or <see langword="null" /> when the vector could not be parsed.</returns>
    public static LlamaServerLaunchProjection? TryFromArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var autoFit = false;
        var metrics = false;
        var jinja = false;
        var cpuMoe = false;
        int? contextTokens = null;
        int? gpuLayers = null;
        string? tensorSplit = null;
        string? overrideTensor = null;
        string? kvCacheTypeK = null;
        string? kvCacheTypeV = null;
        string? flashAttention = null;
        int? threads = null;
        int? threadsBatch = null;
        int? batchSize = null;
        int? ubatchSize = null;
        var parallel = 1;
        int? cacheReuse = null;
        var cacheRamMiB = 0;
        string? pooling = null;

        var index = 0;
        while (index < arguments.Count)
        {
            var argument = arguments[index];
            index++;
            switch (argument)
            {
                case "--jinja":
                    jinja = true;
                    continue;
                case "--metrics":
                    metrics = true;
                    continue;
                case "--cpu-moe" or "-cmoe":
                    cpuMoe = true;
                    continue;
            }

            // Everything below is a value flag. A trailing flag with no value is a vector this method cannot describe.
            if (!IsAllowListedValueOption(argument))
            {
                continue;
            }

            if (index >= arguments.Count)
            {
                return null;
            }

            var value = arguments[index];
            index++;
            if (value.StartsWith('-') && !TryParseInt(value, out _))
            {
                // A value flag followed by another flag is a vector this method cannot describe truthfully.
                return null;
            }

            switch (argument)
            {
                case "--fit":
                    autoFit = string.Equals(value, "on", StringComparison.Ordinal);
                    break;
                case "-c" or "--ctx-size":
                    if (!TryParseInt(value, out contextTokens))
                    {
                        return null;
                    }

                    break;
                case "-ngl" or "--n-gpu-layers":
                    if (!TryParseInt(value, out gpuLayers))
                    {
                        return null;
                    }

                    break;
                case "-ts" or "--tensor-split":
                    tensorSplit = NullIfBlank(value);
                    break;
                case "-ot" or "--override-tensor":
                    overrideTensor = NullIfBlank(value);
                    break;
                case "-ctk" or "--cache-type-k":
                    kvCacheTypeK = NullIfBlank(value);
                    break;
                case "-ctv" or "--cache-type-v":
                    kvCacheTypeV = NullIfBlank(value);
                    break;
                case "-fa" or "--flash-attn":
                    flashAttention = NullIfBlank(value);
                    break;
                case "-t" or "--threads":
                    if (!TryParseInt(value, out threads))
                    {
                        return null;
                    }

                    break;
                case "-tb" or "--threads-batch":
                    if (!TryParseInt(value, out threadsBatch))
                    {
                        return null;
                    }

                    break;
                case "-b" or "--batch-size":
                    if (!TryParseInt(value, out batchSize))
                    {
                        return null;
                    }

                    break;
                case "-ub" or "--ubatch-size":
                    if (!TryParseInt(value, out ubatchSize))
                    {
                        return null;
                    }

                    break;
                case "--parallel" or "-np":
                    if (!TryParseInt(value, out var parsedParallel) || parsedParallel is null)
                    {
                        return null;
                    }

                    parallel = parsedParallel.Value;
                    break;
                case "--cache-reuse":
                    if (!TryParseInt(value, out cacheReuse))
                    {
                        return null;
                    }

                    break;
                case "--cache-ram":
                    if (!TryParseInt(value, out var parsedCacheRam) || parsedCacheRam is null)
                    {
                        return null;
                    }

                    cacheRamMiB = parsedCacheRam.Value;
                    break;
                case "--pooling":
                    pooling = NullIfBlank(value);
                    break;
                default:
                    break;
            }
        }

        return new LlamaServerLaunchProjection(autoFit,
            metrics,
            contextTokens,
            gpuLayers,
            tensorSplit,
            overrideTensor,
            cpuMoe,
            kvCacheTypeK,
            kvCacheTypeV,
            flashAttention ?? FlashAttentionAuto,
            threads,
            threadsBatch,
            batchSize,
            ubatchSize,
            parallel,
            cacheReuse is > 0 ? cacheReuse : null,
            cacheRamMiB,
            jinja,
            pooling);
    }

    /// <summary>
    ///     The deterministic identity of this launch shape: lowercase SHA-256 hex over the canonical JSON above. Two
    ///     projections with equal values always hash equally; any differing field produces a different hash.
    /// </summary>
    public string ComputeIdentity()
    {
        var canonical = JsonSerializer.Serialize(this, CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The value-taking flags <see cref="TryFromArguments" /> reads; every other argument is ignored.</summary>
    private static bool IsAllowListedValueOption(string argument) =>
        argument switch
        {
            "--fit" or "-c" or "--ctx-size" or "-ngl" or "--n-gpu-layers" or "-ts" or "--tensor-split"
                or "-ot" or "--override-tensor" or "-ctk" or "--cache-type-k" or "-ctv" or "--cache-type-v"
                or "-fa" or "--flash-attn" or "-t" or "--threads" or "-tb" or "--threads-batch"
                or "-b" or "--batch-size" or "-ub" or "--ubatch-size" or "--parallel" or "-np"
                or "--cache-reuse" or "--cache-ram" or "--pooling" => true,
            _ => false
        };

    private static bool TryParseInt(string value, out int? parsed)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            parsed = result;
            return true;
        }

        parsed = null;
        return false;
    }
}
