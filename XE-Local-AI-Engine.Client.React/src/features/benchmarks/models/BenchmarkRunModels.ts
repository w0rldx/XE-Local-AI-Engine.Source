import { ApiError } from "@/core/api/errors/ApiError";
import { type KvCacheType, kvCacheTypes } from "@/core/models/KvCacheTypes";
import type { BenchmarkRunFidelity } from "@/features/benchmarks/models/BenchmarkFidelityModels";
import type { BenchmarkOrigin } from "@/features/benchmarks/models/BenchmarkProjectModels";

/**
 * The judging's own lifecycle, verbatim from the wire (lowercase). It belongs to the current judge ATTEMPT, not to the
 * run: `none` means this run has no attempt at all, and every other value is that attempt's state. A judging that
 * failed never downgrades the primary result.
 */
const benchmarkJudgeStates = ["none", "queued", "running", "succeeded", "failed", "cancelled"] as const;
export type BenchmarkJudgeState = (typeof benchmarkJudgeStates)[number];
/** Fail-closed: an unknown state reads as "no judging", never as a verdict. */
export const toBenchmarkJudgeState = (value: unknown): BenchmarkJudgeState =>
	benchmarkJudgeStates.find((state) => state === value) ?? "none";

/**
 * Why the node left a run out of the ranked cohort. Server-derived and exhaustive: `null` means the run IS ranked.
 * Every member maps to an operator hint and an action in the runs table — a rank that is simply missing, with no
 * reason, would be unactionable.
 */
export const benchmarkRankExclusionReasons = [
	"no-score",
	"judge-pending",
	"judge-failed",
	"judge-cancelled",
	"policy-outdated",
	"generation-stale",
	"execution-key-mismatch",
	"execution-identity-incomplete",
	"truncated",
	"incomplete",
	"warmup",
	"item-incomplete",
	"item-revised",
	"item-set-revised",
	"verifier-unavailable",
	"override-unmatched",
	// Pairwise: a fitted score is read THROUGH the active fit, so every way that read can fail is its own reason.
	"pairwise-pending",
	"pairwise-insufficient",
	"pairwise-cap",
	"pairwise-stale",
	"pairwise-unfitted",
	"pairwise-cross-case",
	"pairwise-execution-mismatch",
	"pairwise-execution-identity-incomplete",
] as const;
export type BenchmarkRankExclusionReason = (typeof benchmarkRankExclusionReasons)[number];
export const toBenchmarkRankExclusionReason = (value: unknown): BenchmarkRankExclusionReason | null =>
	benchmarkRankExclusionReasons.find((reason) => reason === value) ?? null;

/** Which side produced `qualityScore`: the operator's override wins over the judge, and `none` means unscored. */
export type BenchmarkQualityScoreSource = "user" | "judge" | "pairwise" | "none";
export const toBenchmarkQualityScoreSource = (value: unknown): BenchmarkQualityScoreSource =>
	value === "user" || value === "judge" || value === "pairwise" ? value : "none";

export interface BenchmarkOutputPart {
	kind: string;
	content?: string | null;
	toolCallId?: string | null;
	toolName?: string | null;
	arguments?: string | null;
	result?: string | null;
	isError?: boolean | null;
}

const benchmarkPrimaryStatuses = ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"] as const;
export type BenchmarkPrimaryStatus = (typeof benchmarkPrimaryStatuses)[number];

/**
 * What a repeat GROUP measures. `Throughput` is the default and the historical behaviour: temperature 0 and one fixed
 * seed, so every repeat produces the identical answer and only the machine varies. `AnswerVariance` samples at a
 * temperature and varies the seed per repeat, so the repeats differ in what they SAY — the spread then describes the
 * model, not the box, and the two must never be averaged together.
 */
export const benchmarkRepeatModes = ["Throughput", "AnswerVariance"] as const;
export type BenchmarkRepeatMode = (typeof benchmarkRepeatModes)[number];
/** Fail-safe: an unknown mode reads as the deterministic one, never as "these numbers vary for an unknown reason". */
export const toBenchmarkRepeatMode = (value: unknown): BenchmarkRepeatMode =>
	benchmarkRepeatModes.find((mode) => mode === value) ?? "Throughput";

/** `BenchmarkRunFreezeService.DefaultAnswerVarianceTemperature` and its ceiling, mirrored for the launch controls. */
export const benchmarkAnswerVarianceTemperature = { default: 0.7, max: 2 } as const;

/**
 * One server-side criterion's evidence: what the verifier checked and whether the answer passed it. Present only for
 * verifiable criteria, and only on a detail response — a pointwise LLM criterion has a rationale instead.
 */
export interface BenchmarkRunVerifier {
	/** The criterion id this evidence belongs to. */
	id: string;
	kind: string;
	passed: boolean;
	/** The node's own sentence about what it checked. Evidence, not a verdict to re-derive. */
	detail: string;
}

export interface BenchmarkJudgeCriterionScore {
	id: string;
	/** 0..10 per criterion; the weighted roll-up is the 0..100 `score` on the judging itself. */
	score: number;
	rationale: string;
}

/**
 * The judging of one run as the node reports it. `policyCurrent`/`executionCurrent` are the two halves of "may this
 * score be ranked": a score produced under an older policy revision, or under a judge runtime other than the cohort's
 * reference, stays visible but unranked.
 */
export interface BenchmarkRunJudge {
	state: BenchmarkJudgeState;
	score: number | null;
	policyRevision: number | null;
	attemptSequence: number | null;
	cohortGeneration: number | null;
	executionKey: string | null;
	policyCurrent: boolean;
	executionCurrent: boolean;
	errorMessage: string | null;
	summary: string | null;
	/** Server-side verifier evidence, one entry per verifiable criterion. Detail-only, like the criteria themselves. */
	verifiers: BenchmarkRunVerifier[];
	/** Detail-only; the list projection omits it. */
	criteria: BenchmarkJudgeCriterionScore[];
}

export const noBenchmarkRunJudge: BenchmarkRunJudge = {
	state: "none",
	score: null,
	policyRevision: null,
	attemptSequence: null,
	cohortGeneration: null,
	executionKey: null,
	policyCurrent: false,
	executionCurrent: false,
	errorMessage: null,
	summary: null,
	verifiers: [],
	criteria: [],
};

/** The machine-readable `code` extension of a benchmark ProblemDetails body, or null for any other failure. */
export function benchmarkErrorCode(error: unknown): string | null {
	if (!(error instanceof ApiError)) {
		return null;
	}
	const code = (error.apiProblemDetails as unknown as Record<string, unknown> | undefined)?.["code"];
	return typeof code === "string" ? code : null;
}

/**
 * True when the node refused the requested KV cache type specifically, read off the ProblemDetails `code` extension.
 * The status alone is not enough: an ineligible model or agent answers 422 too, and local hey-api response validation
 * reuses 422 as its own status — neither is fixed by picking f16.
 */
export const isUnsupportedKvCacheTypeError = (error: unknown): boolean =>
	error instanceof ApiError && error.statusCode === 422 && benchmarkErrorCode(error) === "UnsupportedKvCacheType";

/**
 * Context the node keeps for the prompt when it checks a reasoning budget against an output budget
 * (`BenchmarkFrozenPolicies.MinimumPromptReserveTokens`). Mirrored so the form refuses what the node would refuse.
 */
export const benchmarkPromptReserveTokens = 512;

export const benchmarkKvCacheTypes = kvCacheTypes;
export type BenchmarkKvCacheType = KvCacheType;
/** Was the type picked by the operator, or derived at freeze from the binary's manifest? */
export type BenchmarkKvCacheTypeSource = "explicit" | "auto";
export type BenchmarkFlashAttentionMode = "auto" | "on";

/**
 * What a run intended to launch and what actually launched, as flat facts — never a verdict. Legacy rows carry null in
 * every member and render "—". Present on the primary side of every run summary; the judge's own launch evidence lives
 * on its attempt and is not projected onto the run.
 */
export interface BenchmarkLaunchFacts {
	variant: string | null;
	kvCacheType: string | null;
	kvCacheTypeSource: BenchmarkKvCacheTypeSource | null;
	kvAutoReason: string | null;
	flashAttentionMode: BenchmarkFlashAttentionMode | null;
	intendedLaunchIdentity: string | null;
	// True when the run was frozen under a launch-identity scheme this build no longer computes, so the intended and
	// effective identities are NOT comparable and a difference between them is not drift. Server-computed; the client
	// never learns the scheme number. Null when the run recorded no launch intent at all.
	launchIdentitySchemeOutdated: boolean | null;
	intendedExecutableSha256: string | null;
	effectiveLaunchIdentity: string | null;
	/** Variant name, or `cpu` / `cpu-fallback` / `metal-unverified` / `unknown`. */
	effectiveBackend: string | null;
	placementOffloaded: number | null;
	placementTotal: number | null;
	executableSha256: string | null;
	hasAuxAssets: boolean | null;
	receiptHash: string | null;
	environmentFactsHash: string | null;
}

/** Every fact absent — what a run frozen before the launch receipt existed maps to, and what the UI renders as "—". */
export const noBenchmarkLaunchFacts: BenchmarkLaunchFacts = {
	variant: null,
	kvCacheType: null,
	kvCacheTypeSource: null,
	kvAutoReason: null,
	flashAttentionMode: null,
	intendedLaunchIdentity: null,
	launchIdentitySchemeOutdated: null,
	intendedExecutableSha256: null,
	effectiveLaunchIdentity: null,
	effectiveBackend: null,
	placementOffloaded: null,
	placementTotal: null,
	executableSha256: null,
	hasAuxAssets: null,
	receiptHash: null,
	environmentFactsHash: null,
};

/**
 * A decoded launch receipt or environment-facts object. Kept opaque on purpose: the UI renders and diffs whatever
 * fields the node recorded as facts, never verdicts, so a contract addition needs no frontend change.
 */
export type BenchmarkEvidenceObject = Readonly<Record<string, unknown>>;

/**
 * The separated throughput facts of one run. `tg` is DECODE speed and `pp` is PREFILL speed; the node measures and
 * reports them apart because the single blended figure they replaced conflated the two, making the same model's runs on
 * a long and a short prompt incomparable. Every member is null for a runtime that reports no per-request timings (every
 * cloud provider) and for runs measured before the node recorded them. Display only — never a ranking input.
 */
export interface BenchmarkRunThroughput {
	/** Milliseconds the caller waited for the first token, measured client-side (network and adapter overhead included). */
	ttftMs: number | null;
	/** Prompt tokens the runtime evaluated, cached ones included. */
	promptTokens: number | null;
	/** Prompt-processing throughput (pp). */
	promptTokensPerSecond: number | null;
	/** Tokens the runtime decoded. */
	generationTokens: number | null;
	/** Decode throughput (tg). Equal to {@link BenchmarkRunSummary.tokensPerSecond} whenever the split exists. */
	generationTokensPerSecond: number | null;
	/**
	 * Prompt tokens served from the prompt cache across ALL of the turn's requests. Above zero means the pp numbers
	 * describe a partially cached prefill — expected once tools are called, since each round re-sends the conversation.
	 */
	cachedPromptTokens: number | null;
	/** How many provider requests the turn made. Above 1 means the sums span a tool-calling loop. */
	segmentCount: number | null;
}

export const noBenchmarkRunThroughput: BenchmarkRunThroughput = {
	ttftMs: null,
	promptTokens: null,
	promptTokensPerSecond: null,
	generationTokens: null,
	generationTokensPerSecond: null,
	cachedPromptTokens: null,
	segmentCount: null,
};

export interface BenchmarkRunSummary {
	id: string;
	projectId: string;
	primaryModelName: string;
	primaryModelOrigin: BenchmarkOrigin;
	modelContentFingerprint: string;
	/**
	 * The BASE model this run is a build of — the repo id or imported name with the quant tag stripped. Every quant of
	 * one model shares it, so a group is one model and its rows are that model's quants. Use
	 * {@link BenchmarkRunSummary.modelContentFingerprint} when you mean the exact build.
	 */
	modelGroupKey: string;
	/** The repeat group this run belongs to, or null for a plain single run. */
	repeatGroupId: string | null;
	/** Position in the group: 0 is the warm-up (when one was requested), measured repeats are 1..N. */
	repeatIndex: number | null;
	/** A warm-up run: shown, but never ranked and never counted in a group's statistics. */
	isWarmup: boolean;
	/** What this run's group measures. Throughput repeats are deterministic; answer-variance repeats are not. */
	repeatMode: BenchmarkRepeatMode;
	/** The seed the run actually sampled with, verbatim (the node's own string), or null for a legacy row. */
	samplingSeed: string | null;
	/** The temperature the run actually sampled at. Null for legacy rows; 0 for a throughput repeat. */
	samplingTemperature: number | null;
	agentName: string;
	agentVersion: number;
	requestedContextTokens: number;
	primaryStatus: BenchmarkPrimaryStatus;
	judge: BenchmarkRunJudge;
	/** 0..100, the operator override when there is one, otherwise the current judging's score. */
	qualityScore: number | null;
	qualityScoreSource: BenchmarkQualityScoreSource;
	rank: number | null;
	rankExclusionReason: BenchmarkRankExclusionReason | null;
	/**
	 * Why the model stopped generating, verbatim from the node (`stop`, `length`, `tool_calls`, `content_filter`), or
	 * null when the provider reported none. Kept as a free string on purpose: an unrecognized token is shown, not
	 * swallowed. `length` is the one the UI reasons about — see {@link isBenchmarkRunTruncated}.
	 */
	primaryStopReason: string | null;
	effectiveContextTokens: number | null;
	durationMs: number | null;
	totalTokens: number | null;
	/** Decode throughput (tg) when the runtime timed prefill and decode apart, otherwise the blended fallback. */
	tokensPerSecond: number | null;
	throughput: BenchmarkRunThroughput;
	/** Null when the node has no fidelity row for this run at all — a project that never asked for one, or a legacy row. */
	fidelity: BenchmarkRunFidelity | null;
	userScore: number | null;
	lastStreamSequence: number;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
	primaryLaunch: BenchmarkLaunchFacts;
}

export interface BenchmarkRunDetail extends BenchmarkRunSummary {
	primaryLaunchReceipt: BenchmarkEvidenceObject | null;
	primaryEnvironmentFacts: BenchmarkEvidenceObject | null;
	outputParts: BenchmarkOutputPart[];
	primaryErrorMessage: string | null;
	startedAtUtc: number | null;
	primaryCompletedAtUtc: number | null;
	/** The budget this run was frozen with, or null when the project pinned none. */
	reasoningBudgetTokens: number | null;
	/**
	 * Whether the model could actually honour it. False = the run carries a budget the runtime never applied (no
	 * thinking mode), which is why the run can look as if the budget did nothing; null for a legacy row.
	 */
	reasoningBudgetApplicable: boolean | null;
}

/** Everything a write needs to address one run under optimistic concurrency. A detail or a summary row satisfies it. */
export type BenchmarkRunRef = Pick<BenchmarkRunSummary, "id" | "projectId" | "version">;

/**
 * The answer was cut off by the token budget or the context ceiling. The run still SUCCEEDED — the measurement is real —
 * so this is the only signal that separates a finished answer from a fragment, and it is what keeps a truncated run out
 * of the ranked cohort.
 */
/** Mirrors the node's `BenchmarkFrozenPolicies` so the form refuses what the node would reject anyway. */
export const benchmarkInvocationTimeoutLimits = { min: 60, max: 7200, default: 900 } as const;
