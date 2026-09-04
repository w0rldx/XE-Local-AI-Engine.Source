namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed record class BenchmarkRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_runtime_snapshot_json</c>.
    /// </summary>
    public byte[] RuntimeSnapshotJson { get; set; } = [];

    public string PrimaryModelName { get; set; } = string.Empty;
    public LocalModelOrigin? PrimaryModelOrigin { get; set; }
    public string ModelContentFingerprint { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public long AgentVersion { get; set; }
    public int RequestedContextTokens { get; set; }

    /// <summary>
    ///     The generation timeout frozen onto this run from the project, or <see langword="null" /> for the frozen
    ///     default. Copied at freeze rather than read from the project at execution so a run replays with the budget it
    ///     was started under, exactly like <see cref="RequestedContextTokens" />.
    /// </summary>
    public int? InvocationTimeoutSeconds { get; set; }

    public BenchmarkPrimaryStatus PrimaryStatus { get; set; }
    public int? EffectiveContextTokens { get; set; }
    public long? DurationMs { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>
    ///     Decode throughput (tg) when the runtime reported <see cref="GenerationTokens" />/<see cref="GenerationMs" />,
    ///     otherwise the legacy blended <c>total_tokens / duration_ms</c>. Kept under its original name and column so
    ///     every existing reader is unaffected.
    /// </summary>
    public double? TokensPerSecond { get; set; }

    /// <summary>
    ///     The separated throughput measurement: time to first token (client-side wall clock), and the prompt-processing
    ///     (pp) versus generation (tg) split of tokens and milliseconds as the runtime itself timed them. All null for
    ///     runs frozen before these columns existed and for any runtime that reports no per-request timings — never
    ///     inferred from the blended numbers. Display only: throughput is not a ranking input.
    /// </summary>
    public double? TtftMs { get; set; }

    public int? PromptTokens { get; set; }
    public double? PromptMs { get; set; }
    public int? GenerationTokens { get; set; }
    public double? GenerationMs { get; set; }
    public int? CachedPromptTokens { get; set; }

    /// <summary>How many provider requests the turn made; above 1 means the sums above span a tool-calling loop.</summary>
    public int? SegmentCount { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_output_parts_json</c>.
    /// </summary>
    public byte[]? OutputPartsJson { get; set; }

    public long LastStreamSequence { get; set; }
    public int? UserScore { get; set; }

    /// <summary>
    ///     The repeat group this run belongs to, or <see langword="null" /> for a single run. Every run a batch of
    ///     repeats created shares one id, so a reader can tell "three measurements of one launch" from "three
    ///     unrelated runs that happen to name the same model".
    /// </summary>
    public Guid? RepeatGroupId { get; set; }

    /// <summary>
    ///     Position inside <see cref="RepeatGroupId" />: <c>0</c> is the warm-up run (only when one was requested),
    ///     and the measured repeats are <c>1..N</c>. Null exactly when <see cref="RepeatGroupId" /> is null.
    /// </summary>
    public int? RepeatIndex { get; set; }

    /// <summary>
    ///     A warm-up run: measured and stored like any other, but never ranked and never counted in a group's
    ///     statistics. Its whole purpose is to absorb the first-launch costs the runs after it should not pay for.
    /// </summary>
    public bool IsWarmup { get; set; }

    /// <summary>
    ///     What the run's repeat group is measuring. Every run recorded before this column existed was a throughput
    ///     repeat — that is what the frozen deterministic sampling made it — so the default is historically true
    ///     rather than invented.
    /// </summary>
    public BenchmarkRepeatMode RepeatMode { get; set; }

    /// <summary>
    ///     The seed this run was frozen with, as the string the snapshot carries (a seed is an unconstrained 64-bit
    ///     value). Plaintext, and duplicated out of the encrypted snapshot on purpose: the listing and the CSV export
    ///     never decrypt a payload, and a group of answer-variance runs is unreadable without the one input that
    ///     differs between them. Null on runs frozen before the column existed.
    /// </summary>
    public string? SamplingSeed { get; set; }

    /// <summary>The temperature this run was frozen with, duplicated out of the snapshot for the same reason.</summary>
    public double? SamplingTemperature { get; set; }

    /// <summary>
    ///     Which LEAF task item this run answered. <see langword="null" /> on a run frozen before task items existed,
    ///     which reads as the project's item 0. Stamped at freeze and never recomputed.
    /// </summary>
    public Guid? TaskItemId { get; set; }

    /// <summary>The item's display index, denormalized so the flat-column ranking read never opens an item row.</summary>
    public int? TaskItemIndex { get; set; }

    /// <summary>
    ///     The measurement cell this run's per-item score aggregates into, stamped at freeze and NEVER null: a null
    ///     would put every ungrouped run of a project into one anonymous bucket and average their scores together,
    ///     silently. A run that is its own cell carries a singleton key derived from its own id.
    /// </summary>
    public string CellKey { get; set; } = string.Empty;

    /// <summary>
    ///     A copy of the leaf item's <see cref="BenchmarkTaskItem.InputHash" /> at freeze — exactly what this run was
    ///     asked. A run whose stamp no longer matches its item answered a question that no longer exists, and the
    ///     ranking read excludes it. Runs frozen before task items existed carry the legacy constant on both hash
    ///     columns and are compared against the same constant, so they are never stale.
    /// </summary>
    public string TaskInputHash { get; set; } = string.Empty;

    /// <summary>
    ///     A copy of the project's <see cref="BenchmarkProject.TaskItemSetHash" /> at freeze — what the whole question
    ///     set was when this cell was measured. The only stamp that can answer "was this cell complete WHEN it was
    ///     measured": completeness against the current item set turns a two-of-three cell into a two-of-two cell the
    ///     moment the third item is deleted.
    /// </summary>
    public string TaskItemSetHash { get; set; } = string.Empty;

    /// <summary>The judge attempt whose verdict this run currently shows. Null until the first attempt is enqueued.</summary>
    public Guid? CurrentJudgeAttemptId { get; set; }

    /// <summary>
    ///     The quant-fidelity projection: a denormalized copy of the LATEST succeeded
    ///     <see cref="BenchmarkFidelityAttempt" /> of this run, so the listing stays a flat-column scan and never
    ///     decrypts. Plaintext numerics, same posture as <see cref="TokensPerSecond" />. Display only — perplexity and
    ///     KL-divergence are never ranking inputs. <see cref="FidelityAttemptId" /> is both the audit link back to the
    ///     attempt these numbers came from and the CAS target the refresh guards on.
    /// </summary>
    public Guid? FidelityAttemptId { get; set; }

    public double? PerplexityMean { get; set; }
    public double? PerplexityStdErr { get; set; }
    public int? PerplexityChunks { get; set; }

    /// <summary>The window perplexity was measured at — pinned to 512, recorded so a future change is visible.</summary>
    public int? PerplexityContextTokens { get; set; }

    public string? PerplexityCorpusId { get; set; }
    public double? KldMean { get; set; }
    public double? KldP99 { get; set; }
    public double? TopTokenAgreement { get; set; }

    /// <summary>The base model's content fingerprint. Evidence, NOT the comparability gate — the next column is.</summary>
    public string? KldBaseFingerprint { get; set; }

    /// <summary>
    ///     The comparability gate, copied from the attempt's <c>BaseLogitsDigest</c>: the digest over the WHOLE
    ///     base-logit cache key. A KLD figure is displayed, and two are compared, only while this equals the digest
    ///     recomputed from the project's current KLD settings — the base fingerprint alone would pass a number
    ///     measured on 50 chunks off as comparable with one measured on 200. A mismatch renders a stale badge, never
    ///     a number.
    /// </summary>
    public string? KldBaseLogitsDigest { get; set; }

    /// <summary><c>queued</c>/<c>running</c>/<c>succeeded</c>/<c>failed</c>/<c>cancelled</c>/<c>skipped</c>.</summary>
    public string? FidelityStatus { get; set; }

    public string? FidelityErrorMessage { get; set; }

    /// <summary>
    ///     What freeze INTENDED this run to launch, per phase. All null for rows created before launch evidence
    ///     existed (they are displayed as "—", never inferred).
    /// </summary>
    public string? PrimaryVariant { get; set; }

    public string? PrimaryKvCacheType { get; set; }
    public string? PrimaryKvCacheTypeSource { get; set; }
    public string? PrimaryKvAutoReason { get; set; }
    public string? PrimaryFlashAttentionMode { get; set; }
    public string? PrimaryIntendedLaunchIdentity { get; set; }
    public string? PrimaryIntendedExecutableSha256 { get; set; }

    /// <summary>
    ///     The launch-identity SCHEME <see cref="PrimaryIntendedLaunchIdentity" /> was computed under. NULL on a row
    ///     frozen before the scheme was recorded, which reads as scheme 1.
    /// </summary>
    public int? PrimaryLaunchIdentityScheme { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_primary_launch_receipt_json</c>. Written once, before inference, and never overwritten.
    /// </summary>
    public byte[]? PrimaryLaunchReceiptJson { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_primary_environment_facts_json</c>.
    /// </summary>
    public byte[]? PrimaryEnvironmentFactsJson { get; set; }

    public string? PrimaryReceiptHash { get; set; }
    public string? PrimaryEnvironmentFactsHash { get; set; }
    public string? PrimaryEffectiveLaunchIdentity { get; set; }
    public string? PrimaryEffectiveBackend { get; set; }
    public int? PrimaryPlacementOffloaded { get; set; }
    public int? PrimaryPlacementTotal { get; set; }
    public string? PrimaryLaunchExecutableSha256 { get; set; }
    public bool? PrimaryLaunchHasAuxAssets { get; set; }
    public string? PrimaryLaunchKvCacheTypeSource { get; set; }

    /// <summary>
    ///     Why the primary generation stopped, verbatim from the provider (<c>stop</c>, <c>length</c>,
    ///     <c>tool_calls</c>, <c>content_filter</c>). Plaintext, not sensitive. Null on runs frozen before this column
    ///     existed and on any run whose provider reported no finish reason — never inferred from the status.
    /// </summary>
    public string? PrimaryStopReason { get; set; }

    public string? PrimaryErrorMessage { get; set; }
    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? PrimaryCompletedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
