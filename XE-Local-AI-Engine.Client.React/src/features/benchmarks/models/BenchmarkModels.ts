import { z } from "zod";

import { ApiError } from "@/core/api/errors/ApiError";

import type { XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin } from "@/core/api/generated";
import type { ChatMessagePart, ToolCallState } from "@/features/chat/models/ChatModels";

const benchmarkPrimaryStatuses = ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"] as const;
export type BenchmarkPrimaryStatus = (typeof benchmarkPrimaryStatuses)[number];

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
] as const;
export type BenchmarkRankExclusionReason = (typeof benchmarkRankExclusionReasons)[number];
export const toBenchmarkRankExclusionReason = (value: unknown): BenchmarkRankExclusionReason | null =>
	benchmarkRankExclusionReasons.find((reason) => reason === value) ?? null;

/** Which side produced `qualityScore`: the operator's override wins over the judge, and `none` means unscored. */
export type BenchmarkQualityScoreSource = "user" | "judge" | "none";
export const toBenchmarkQualityScoreSource = (value: unknown): BenchmarkQualityScoreSource =>
	value === "user" || value === "judge" ? value : "none";

export type BenchmarkOrigin = XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin | null;

/** Mirrors the node's `BenchmarkJudgePolicyVersions` so the editor refuses what the server would reject anyway. */
export const benchmarkRubricLimits = {
	version: 1,
	minCriteria: 1,
	maxCriteria: 8,
	minWeight: 1,
	maxWeight: 100,
	maxIdLength: 32,
	maxTitleLength: 64,
	maxDescriptionLength: 1024,
	maxReferenceAnswerLength: 32768,
} as const;

export interface BenchmarkRubricCriterion {
	id: string;
	title: string;
	description: string;
	weight: number;
}

export interface BenchmarkRubric {
	version: number;
	criteria: BenchmarkRubricCriterion[];
}

/** Server-side criterion ids are `[a-z0-9-_]{1,32}`; the editor derives one from the title until the operator edits it. */
export function toBenchmarkCriterionId(title: string): string {
	return title
		.toLowerCase()
		.replace(/[^a-z0-9\-_]+/g, "-")
		.replace(/-{2,}/g, "-")
		.replace(/^-+|-+$/g, "")
		.slice(0, benchmarkRubricLimits.maxIdLength);
}

/** The project's current judge policy revision, or a disabled judge. Read-only: it is edited through a draft. */
export interface BenchmarkJudgePolicy {
	enabled: boolean;
	policyRevision: number | null;
	policyHash: string | null;
	modelName: string | null;
	requestedContextTokens: number | null;
	rubric: BenchmarkRubric | null;
	referenceAnswer: string | null;
	cohortGeneration: number | null;
	referenceExecutionKey: string | null;
	/**
	 * True when the stored revision carries a judge prompt version this build no longer judges under. The project
	 * still reads and existing scores stay ranked; new judgings refuse until the operator re-saves the judge.
	 */
	promptVersionOutdated: boolean;
}

export interface BenchmarkProjectSummary {
	id: string;
	name: string;
	contextTokens: number;
	/** Per-run output-token budget (`n_predict`), or null when generation is only limited by the context window. */
	maxOutputTokens: number | null;
	/** Per-run thinking budget, or null for "as much as the window allows". Additive with the output budget. */
	reasoningBudgetTokens: number | null;
	/** Seconds one run's generation may take before the node cancels it, or null for the node default (900). */
	invocationTimeoutSeconds: number | null;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	runCount: number;
	isFrozen: boolean;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface BenchmarkProjectDetail extends BenchmarkProjectSummary {
	coreTask: string;
	judge: BenchmarkJudgePolicy;
}

/** What the project form edits. A null `rubric` means "use the node's default rubric", never "no rubric". */
export interface BenchmarkProjectDraft {
	name: string;
	coreTask: string;
	contextTokens: number;
	maxOutputTokens: number | null;
	reasoningBudgetTokens: number | null;
	invocationTimeoutSeconds: number | null;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	judgeModelName: string | null;
	judgeContextTokens: number | null;
	rubric: BenchmarkRubric | null;
	referenceAnswer: string | null;
}

export interface BenchmarkEligibleModel {
	modelName: string;
	maxContextTokens: number | null;
	effectiveContextTokens: number | null;
	origin: BenchmarkOrigin;
	modelContentFingerprint: string;
	supportsTools: boolean;
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
	criteria: [],
};

/** What the project's ranking is computed against, so the table can say "n of m ranked" honestly. */
export interface BenchmarkRankCohort {
	policyRevision: number | null;
	executionKey: string | null;
	cohortGeneration: number | null;
	rankedCount: number;
	totalScored: number;
}

export interface BenchmarkOutputPart {
	kind: string;
	content?: string | null;
	toolCallId?: string | null;
	toolName?: string | null;
	arguments?: string | null;
	result?: string | null;
	isError?: boolean | null;
}

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

export const benchmarkKvCacheTypes = ["f16", "q8_0", "q4_0"] as const;
export type BenchmarkKvCacheType = (typeof benchmarkKvCacheTypes)[number];
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
}

/** Everything a write needs to address one run under optimistic concurrency. A detail or a summary row satisfies it. */
export type BenchmarkRunRef = Pick<BenchmarkRunSummary, "id" | "projectId" | "version">;

const benchmarkRunEventKinds = [
	"OutputDelta",
	"ReasoningDelta",
	"ToolCall",
	"ToolResult",
	"PrimaryState",
	"JudgeState",
	"Metrics",
	"TerminalSnapshotAvailable",
] as const;

export const benchmarkRunEventSchema = z.object({
	runId: z.string(),
	sequence: z.number().int().nonnegative(),
	kind: z.enum(benchmarkRunEventKinds),
	payload: z.object({
		content: z.string().nullish(),
		state: z.string().nullish(),
		toolCallId: z.string().nullish(),
		toolName: z.string().nullish(),
		arguments: z.string().nullish(),
		result: z.string().nullish(),
		isError: z.boolean().nullish(),
		effectiveContextTokens: z.number().int().nullish(),
		durationMs: z.number().int().nullish(),
		totalTokens: z.number().int().nullish(),
		tokensPerSecond: z.number().nullish(),
		runVersion: z.number().int().nullish(),
		ttftMs: z.number().nullish(),
		promptTokens: z.number().int().nullish(),
		promptTokensPerSecond: z.number().nullish(),
		generationTokens: z.number().int().nullish(),
		generationTokensPerSecond: z.number().nullish(),
		cachedPromptTokens: z.number().int().nullish(),
		segmentCount: z.number().int().nullish(),
	}),
});
export type BenchmarkRunEvent = z.infer<typeof benchmarkRunEventSchema>;

export const benchmarkReplayResetSchema = z.object({
	runId: z.string(),
	latestSequence: z.number().int().nonnegative(),
	runVersion: z.number().int().nonnegative(),
});
export type BenchmarkReplayReset = z.infer<typeof benchmarkReplayResetSchema>;

/**
 * The live corrections the hub has streamed since the last authoritative read. Kept as an overlay rather than written
 * into the query cache: the durable HTTP snapshot stays the authority, and a `TerminalSnapshotAvailable` refetch drops
 * the overlay again.
 */
export interface BenchmarkRunLiveOverlay {
	judgeState: BenchmarkJudgeState | null;
	effectiveContextTokens: number | null;
	durationMs: number | null;
	totalTokens: number | null;
	tokensPerSecond: number | null;
	/** Null until a `Metrics` event has arrived; the durable snapshot's own breakdown stays authoritative until then. */
	throughput: BenchmarkRunThroughput | null;
}

export const noBenchmarkRunLiveOverlay: BenchmarkRunLiveOverlay = {
	judgeState: null,
	effectiveContextTokens: null,
	durationMs: null,
	totalTokens: null,
	tokensPerSecond: null,
	throughput: null,
};

/** Applies the streamed corrections on top of the durable snapshot. Absent members leave the snapshot untouched. */
export function applyBenchmarkLiveOverlay<T extends BenchmarkRunSummary>(run: T, overlay: BenchmarkRunLiveOverlay): T {
	return {
		...run,
		judge: overlay.judgeState === null ? run.judge : { ...run.judge, state: overlay.judgeState },
		effectiveContextTokens: overlay.effectiveContextTokens ?? run.effectiveContextTokens,
		durationMs: overlay.durationMs ?? run.durationMs,
		totalTokens: overlay.totalTokens ?? run.totalTokens,
		tokensPerSecond: overlay.tokensPerSecond ?? run.tokensPerSecond,
		throughput: overlay.throughput ?? run.throughput,
	};
}

/** The live corrections one hub event carries, or null when it carries none. */
export function benchmarkLiveOverlayPatch(event: BenchmarkRunEvent): Partial<BenchmarkRunLiveOverlay> | null {
	if (event.kind === "JudgeState") {
		return { judgeState: toBenchmarkJudgeState(event.payload.state) };
	}
	if (event.kind === "Metrics") {
		return {
			effectiveContextTokens: event.payload.effectiveContextTokens ?? null,
			durationMs: event.payload.durationMs ?? null,
			totalTokens: event.payload.totalTokens ?? null,
			tokensPerSecond: event.payload.tokensPerSecond ?? null,
			throughput: {
				ttftMs: event.payload.ttftMs ?? null,
				promptTokens: event.payload.promptTokens ?? null,
				promptTokensPerSecond: event.payload.promptTokensPerSecond ?? null,
				generationTokens: event.payload.generationTokens ?? null,
				generationTokensPerSecond: event.payload.generationTokensPerSecond ?? null,
				cachedPromptTokens: event.payload.cachedPromptTokens ?? null,
				segmentCount: event.payload.segmentCount ?? null,
			},
		};
	}
	return null;
}

function mergeTextPart(parts: BenchmarkOutputPart[], kind: "output" | "reasoning", content: string): BenchmarkOutputPart[] {
	if (!content) {
		return parts;
	}
	const next = [...parts];
	const last = next.at(-1);
	if (last?.kind === kind) {
		next[next.length - 1] = { ...last, content: `${last.content ?? ""}${content}` };
	} else {
		next.push({ kind, content });
	}
	return next;
}

export function applyBenchmarkEvent(parts: BenchmarkOutputPart[], event: BenchmarkRunEvent): BenchmarkOutputPart[] {
	if (event.kind === "OutputDelta") {
		return mergeTextPart(parts, "output", event.payload.content ?? "");
	}
	if (event.kind === "ReasoningDelta") {
		return mergeTextPart(parts, "reasoning", event.payload.content ?? "");
	}
	if (event.kind === "ToolCall") {
		return [
			...parts,
			{
				kind: "tool_call",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				arguments: event.payload.arguments,
			},
		];
	}
	if (event.kind === "ToolResult") {
		return [
			...parts,
			{
				kind: "tool_result",
				toolCallId: event.payload.toolCallId,
				toolName: event.payload.toolName,
				result: event.payload.result,
				isError: event.payload.isError,
			},
		];
	}
	return parts;
}

function toolState(isError: boolean | null | undefined): ToolCallState {
	return isError ? "failed" : "received";
}

export function toChatMessageParts(parts: readonly BenchmarkOutputPart[]): ChatMessagePart[] {
	const rendered: ChatMessagePart[] = [];
	const tools = new Map<string, number>();
	let sequence = 0;
	for (const part of parts) {
		sequence += 1;
		if ((part.kind === "output" || part.kind === "text") && part.content) {
			rendered.push({ kind: "text", id: `benchmark-text-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "reasoning" && part.content) {
			rendered.push({ kind: "reasoning", id: `benchmark-reasoning-${sequence}`, sequence, text: part.content });
			continue;
		}
		if (part.kind === "tool_call") {
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			tools.set(id, rendered.length);
			rendered.push({
				kind: "tool",
				id,
				sequence,
				name: part.toolName || "tool",
				state: "requesting",
				args: part.arguments ?? undefined,
			});
			continue;
		}
		if (part.kind === "tool_result") {
			const id = part.toolCallId || `benchmark-tool-${sequence}`;
			const index = tools.get(id);
			if (index !== undefined) {
				const existing = rendered[index];
				if (existing?.kind === "tool") {
					rendered[index] = {
						...existing,
						state: toolState(part.isError),
						result: part.result ?? undefined,
					};
				}
			} else {
				rendered.push({
					kind: "tool",
					id,
					sequence,
					name: part.toolName || "tool",
					state: toolState(part.isError),
					result: part.result ?? undefined,
				});
			}
		}
	}
	return rendered.sort((left, right) => left.sequence - right.sequence);
}

/**
 * The answer was cut off by the token budget or the context ceiling. The run still SUCCEEDED — the measurement is real —
 * so this is the only signal that separates a finished answer from a fragment, and it is what keeps a truncated run out
 * of the ranked cohort.
 */
/** Mirrors the node's `BenchmarkFrozenPolicies` so the form refuses what the node would reject anyway. */
export const benchmarkInvocationTimeoutLimits = { min: 60, max: 7200, default: 900 } as const;

/**
 * Cut off by a budget. Mirrors the node's `BenchmarkStopReasons.IsTruncated`, which counts BOTH tokens: `length` is the
 * OpenAI-compatible one for a full window or an exhausted `n_predict`, and `reasoning-length` is the node's narrowing
 * of the same fact for a run that spent the budget thinking. The node EXCLUDES both as `truncated`, so a UI that knew
 * only `length` would leave a reasoning-truncated run rank-excluded with no badge saying why.
 */
export const isBenchmarkRunTruncated = (run: Pick<BenchmarkRunSummary, "primaryStopReason">): boolean => {
	const reason = run.primaryStopReason?.toLowerCase();
	return reason === "length" || reason === "reasoning-length";
};

/**
 * Truncated INSIDE the reasoning: not one visible answer token was emitted. Truncated as far as ranking is concerned,
 * but it names the reasoning budget as the thing to raise rather than the output budget — the whole difference between
 * a run the operator can fix and one they cannot explain.
 */
export const isBenchmarkRunReasoningExhausted = (run: Pick<BenchmarkRunSummary, "primaryStopReason">): boolean =>
	run.primaryStopReason?.toLowerCase() === "reasoning-length";

/**
 * Stopped cleanly and answered NOTHING — an unanswered tool call, or only reasoning emitted. Distinct from truncated:
 * no budget ran out, so raising one changes nothing. The node excludes it under its own reason for the same cause
 * truncation is excluded for: there is no answer for a rubric to grade.
 */
export const isBenchmarkRunIncomplete = (run: Pick<BenchmarkRunSummary, "primaryStopReason">): boolean =>
	run.primaryStopReason?.toLowerCase() === "incomplete";

/**
 * The base model a row belongs to, for DISPLAY. The server key is lowercased for Hugging Face models so two casings of
 * one repo cannot split a group; this keeps the operator's own capitalisation for the header by deriving it from the
 * run's name instead. Grouping still keys off {@link BenchmarkRunSummary.modelGroupKey} — never off this.
 */
export function benchmarkBaseModelLabel(modelName: string): string {
	const separator = modelName.lastIndexOf(":");
	return separator <= 0 || separator === modelName.length - 1 ? modelName : modelName.slice(0, separator);
}

/** The quant tag an operator picked, which rides on the model name after the last colon. Empty when it carries none. */
export function benchmarkQuantTag(modelName: string): string {
	const separator = modelName.lastIndexOf(":");
	return separator < 0 || separator === modelName.length - 1 ? "" : modelName.slice(separator + 1);
}

export const isPrimaryActive = (status: BenchmarkPrimaryStatus): boolean =>
	status === "Queued" || status === "Running" || status === "CancelRequested";
export const isJudgeActive = (state: BenchmarkJudgeState): boolean => state === "queued" || state === "running";
const isPrimaryTerminal = (status: BenchmarkPrimaryStatus): boolean => !isPrimaryActive(status);
export const isRunTerminal = (run: BenchmarkRunSummary): boolean =>
	isPrimaryTerminal(run.primaryStatus) && !isJudgeActive(run.judge.state);

/** How far a matrix launch has got. `done` counts every terminal run, of which `failed` is the unhappy part. */
export interface BenchmarkBatchProgress {
	total: number;
	done: number;
	running: number;
	queued: number;
	failed: number;
}

/**
 * What a batch launch has achieved, read off the runs the list already holds. A started run the loaded page does not
 * carry yet counts as queued rather than disappearing, so `done + running + queued` always equals the number of runs
 * the node said it started — a progress line that silently shrank its own denominator would be worse than none.
 */
export function benchmarkBatchProgress(
	runs: readonly BenchmarkRunSummary[],
	startedRunIds: readonly string[],
): BenchmarkBatchProgress {
	const progress: BenchmarkBatchProgress = { total: startedRunIds.length, done: 0, running: 0, queued: 0, failed: 0 };
	for (const runId of startedRunIds) {
		const run = runs.find((candidate) => candidate.id === runId);
		if (!run || run.primaryStatus === "Queued") {
			progress.queued += 1;
		} else if (isPrimaryActive(run.primaryStatus)) {
			progress.running += 1;
		} else {
			progress.done += 1;
			if (run.primaryStatus !== "Succeeded") {
				progress.failed += 1;
			}
		}
	}
	return progress;
}

/**
 * The first thing the node's `BenchmarkJudgePolicyValidator` would reject, mirrored client-side so the operator is not
 * told about a bad criterion by a round-trip. `index` names the offending criterion, or -1 for a rubric-level issue.
 */
export interface BenchmarkRubricIssue {
	code: "count" | "id" | "duplicateId" | "title" | "description" | "weight";
	index: number;
}

const criterionId = /^[a-z0-9\-_]+$/;

export function benchmarkRubricIssue(rubric: BenchmarkRubric): BenchmarkRubricIssue | null {
	const { criteria } = rubric;
	if (criteria.length < benchmarkRubricLimits.minCriteria || criteria.length > benchmarkRubricLimits.maxCriteria) {
		return { code: "count", index: -1 };
	}
	const seen = new Set<string>();
	for (const [index, criterion] of criteria.entries()) {
		if (criterion.id.length === 0 || criterion.id.length > benchmarkRubricLimits.maxIdLength || !criterionId.test(criterion.id)) {
			return { code: "id", index };
		}
		if (seen.has(criterion.id)) {
			return { code: "duplicateId", index };
		}
		seen.add(criterion.id);
		if (criterion.title.trim().length === 0 || criterion.title.length > benchmarkRubricLimits.maxTitleLength) {
			return { code: "title", index };
		}
		if (
			criterion.description.trim().length === 0 ||
			criterion.description.length > benchmarkRubricLimits.maxDescriptionLength
		) {
			return { code: "description", index };
		}
		if (criterion.weight < benchmarkRubricLimits.minWeight || criterion.weight > benchmarkRubricLimits.maxWeight) {
			return { code: "weight", index };
		}
	}
	return null;
}
