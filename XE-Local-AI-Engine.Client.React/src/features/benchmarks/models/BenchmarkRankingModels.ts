import type {
	BenchmarkJudgeState,
	BenchmarkPrimaryStatus,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkRunModels";

/** What the project's ranking is computed against, so the table can say "n of m ranked" honestly. */
/** Which answer the judge preferred, already normalized to the canonical pair — never to the order it was shown in. */
const benchmarkVerdicts = ["a", "b", "tie"] as const;
export type BenchmarkVerdict = (typeof benchmarkVerdicts)[number];
export const toBenchmarkVerdict = (value: unknown): BenchmarkVerdict | null =>
	benchmarkVerdicts.find((verdict) => verdict === value) ?? null;

/**
 * One judged pair. `order` is the position the answers were SHOWN in (0 = A first), which is the whole point of the
 * swap: the same pair is judged both ways so a position preference cancels instead of becoming a verdict.
 */
export interface BenchmarkComparison {
	id: string;
	runAId: string;
	runBId: string;
	order: number;
	attemptSequence: number;
	sequence: number;
	taskCaseId: string | null;
	status: string;
	verdict: BenchmarkVerdict | null;
	answerATruncated: boolean;
	answerBTruncated: boolean;
	judgeExecutionKey: string | null;
	errorMessage: string | null;
	enqueuedAtUtc: number;
	completedAtUtc: number | null;
}

/** One run's fitted score. The interval is the bootstrap CI; a score with no interval is not a rankable reading. */
export interface BenchmarkPairwiseRunScore {
	runId: string;
	score: number | null;
	ciLow: number | null;
	ciHigh: number | null;
	comparisons: number;
	bootstrapAppearances: number;
	reason: string | null;
}

/**
 * The Bradley-Terry fit the scores were read out of. `isCurrent` false is the `pairwise-stale` case: the fit exists
 * but does not describe the cohort as it now stands, and a score from it must not be rendered as a current one.
 */
export interface BenchmarkPairwiseFit {
	fitKey: string;
	judgeExecutionKey: string;
	comparisonSetVersion: number;
	cohortGeneration: number;
	iterations: number;
	bootstrapReplicates: number;
	isCurrent: boolean;
	createdAtUtc: number;
	scores: BenchmarkPairwiseRunScore[];
}

/** The verdicts and the fit they produced, as one read — a score beside verdicts that did not produce it is a lie. */
export interface BenchmarkComparisonList {
	cohortGeneration: number;
	comparisonSetVersion: number;
	referenceExecutionKey: string | null;
	items: BenchmarkComparison[];
	fit: BenchmarkPairwiseFit | null;
}

/** What switching a project to pairwise would cost, shown BEFORE the save that commits to it. */
export interface BenchmarkPairwiseEstimate {
	eligibleRuns: number;
	pairedRuns: number;
	/** Runs left out because the cohort is above `maximumRuns`. */
	cappedRuns: number;
	judgeCalls: number;
	/** Null when the node cannot estimate one. Rendered as absent, never as 0. */
	estimatedSeconds: number | null;
	warn: boolean;
	maximumRuns: number;
}

export interface BenchmarkRankCohort {
	policyRevision: number | null;
	executionKey: string | null;
	cohortGeneration: number | null;
	rankedCount: number;
	totalScored: number;
}

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

/**
 * How many runs may be compared at once. A hard cap rather than a scrollable table: the compare view is one column per
 * run over a field set in the hundreds, and the live pane under it renders a full transcript each. Six covers the case
 * the cap exists for — one model's quant ladder — and the operator deselects to look at a seventh.
 */
export const maxComparedBenchmarkRuns = 6;

/**
 * Adds or removes one run from the compare selection, newest-first and capped. Selecting past the cap drops the OLDEST
 * selection rather than refusing the click: an operator working down a quant ladder means "and this one too", and a
 * silently ignored checkbox reads as a broken table.
 */
export function toggleBenchmarkRunSelection(current: readonly string[], runId: string, cap = maxComparedBenchmarkRuns): string[] {
	return current.includes(runId) ? current.filter((id) => id !== runId) : [runId, ...current].slice(0, cap);
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
