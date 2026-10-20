import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1ListBenchmarkReviewsResponse as ReviewQueueResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunArtifactResponse as RunArtifactResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunReviewResponse as RunReviewResponse,
} from "@/core/api/generated";

/**
 * A reviewer's stored decision on one run. Only a decision is ever stored: a run waiting for review has no review at
 * all, which is why the run detail carries `null` for it rather than a `pending` row.
 */
export interface BenchmarkRunReview {
	state: "approved" | "rejected";
	/** The overall 0..100, null on a rejection. */
	score: number | null;
	/** One 0..10 per criterion of the rubric the decision was made under. Empty when the project has no judge policy. */
	criteria: { criterionId: string; score: number }[];
	notes: string | null;
	/** False once the project judges under another revision: the decision no longer counts and the run is back in the inbox. */
	policyCurrent: boolean;
	updatedAtUtc: number;
}

/** Evidence a deterministic verifier left behind (the `pythonTests` streams). Downloaded, never rendered inline. */
export interface BenchmarkRunArtifact {
	id: string;
	kind: string;
	criterionId: string | null;
	judgeAttemptId: string;
	byteCount: number;
	contentHash: string;
}

/** The inbox filters, verbatim from `ListBenchmarkReviewsRequest.State`. */
export const benchmarkReviewQueueStates = ["pending", "stale", "approved", "rejected"] as const;
export type BenchmarkReviewQueueState = (typeof benchmarkReviewQueueStates)[number];

/** `BenchmarkStore.MaxReviewPageSize` and `ListBenchmarkReviewsRequest.Take`'s default: the node answers 400 outside 1..200. */
export const benchmarkReviewPageSize = { min: 1, max: 200, default: 50 } as const;

export interface BenchmarkReviewQueueItem {
	runId: string;
	projectId: string;
	runVersion: number;
	primaryModelName: string;
	taskItemId: string | null;
	taskItemIndex: number | null;
	primaryCompletedAtUtc: number | null;
	state: BenchmarkReviewQueueState;
	reviewScore: number | null;
	reviewedAtUtc: number | null;
}

export interface BenchmarkReviewQueue {
	items: BenchmarkReviewQueueItem[];
	totalCount: number;
}

/** What one review submission sends besides the run's version. Verifiable criteria are never sent: the node owns them. */
export interface BenchmarkReviewSubmission {
	state: "approved" | "rejected";
	/** The reviewer's 0..10 for every `llm` criterion, on an approval under a judge policy. */
	criteria?: { criterionId: string; score: number }[];
	/** The overall 0..100, on an approval of a project without a judge policy. */
	score?: number;
	notes: string | null;
}

// Fail closed: an unknown decision reads as no decision, which leaves the run in the inbox rather than ranked.
export function toBenchmarkRunReview(value: RunReviewResponse | null | undefined): BenchmarkRunReview | null {
	if (!value || (value.state !== "approved" && value.state !== "rejected")) {
		return null;
	}
	return {
		state: value.state,
		score: value.score ?? null,
		criteria: (value.criteria ?? []).flatMap((criterion) =>
			criterion.criterionId && typeof criterion.score === "number"
				? [{ criterionId: criterion.criterionId, score: criterion.score }]
				: [],
		),
		notes: value.notes ?? null,
		policyCurrent: value.policyCurrent === true,
		updatedAtUtc: value.updatedAtUtc ?? 0,
	};
}

export const toBenchmarkRunArtifact = (value: RunArtifactResponse): BenchmarkRunArtifact => ({
	id: value.id ?? "",
	kind: value.kind,
	criterionId: value.criterionId ?? null,
	judgeAttemptId: value.judgeAttemptId ?? "",
	byteCount: value.byteCount ?? 0,
	contentHash: value.contentHash,
});

const queueState = (value: unknown): BenchmarkReviewQueueState =>
	benchmarkReviewQueueStates.find((state) => state === value) ?? "pending";

export const toBenchmarkReviewQueue = (value: ReviewQueueResponse): BenchmarkReviewQueue => ({
	items: (value.items ?? []).map((item) => ({
		runId: item.runId ?? "",
		projectId: item.projectId ?? "",
		runVersion: item.runVersion ?? 0,
		primaryModelName: item.primaryModelName,
		taskItemId: item.taskItemId ?? null,
		taskItemIndex: item.taskItemIndex ?? null,
		primaryCompletedAtUtc: item.primaryCompletedAtUtc ?? null,
		state: queueState(item.state),
		reviewScore: item.reviewScore ?? null,
		reviewedAtUtc: item.reviewedAtUtc ?? null,
	})),
	totalCount: value.totalCount ?? 0,
});

/**
 * Why a review cannot be submitted right now, mirroring the node's `NotReviewable` refusals, or null when it can.
 * `judge`: under a judge policy the current judging must have succeeded under the CURRENT revision — a human scores the
 * rubric that judging checked, and never overrules a verifier it has not run. Read off `judge.state` and
 * `judge.policyCurrent`; `review.policyCurrent` is deliberately not a blocker, because a stale review is exactly the run
 * the reviewer is meant to decide again.
 */
export type BenchmarkReviewBlocker = "primary" | "revised" | "judge";

export function benchmarkReviewBlocker(
	run: {
		primaryStatus: string;
		rankExclusionReason: string | null;
		judge: { state: string; policyCurrent: boolean };
	},
	hasJudgePolicy: boolean,
): BenchmarkReviewBlocker | null {
	if (run.primaryStatus !== "Succeeded") {
		return "primary";
	}
	if (run.rankExclusionReason === "item-revised" || run.rankExclusionReason === "item-set-revised") {
		return "revised";
	}
	if (hasJudgePolicy && !(run.judge.state === "succeeded" && run.judge.policyCurrent)) {
		return "judge";
	}
	return null;
}

/**
 * Which score control a run's pane offers, mirroring the node's review gate:
 * - `review`: a review project's non-warm-up run on a SCORING item (a leaf that counts toward the score; a pre-suite run
 *   with no item counts as scoring) — the review endpoint is its only score writer.
 * - `judgeOnly`: every other run of a review project (a warm-up, a non-scoring leaf such as a NIAH case, an item no
 *   longer in the project). The node refuses both a plain score (409 `ReviewRequired`) and a review, so no control.
 * - `picker`: a project without review — the plain operator override.
 * - `loading`: the project or its items are not read yet. Neither control is rendered, so the wrong one never flashes.
 */
export type BenchmarkScoreControl = "review" | "judgeOnly" | "picker" | "loading";

export function benchmarkScoreControl(
	project: { requiresReview: boolean } | undefined,
	run: { isWarmup: boolean; taskItemId: string | null },
	items: readonly { id: string; isLeaf: boolean; countsTowardScore: boolean }[] | undefined,
): BenchmarkScoreControl {
	if (project === undefined) {
		return "loading";
	}
	if (!project.requiresReview) {
		return "picker";
	}
	if (run.isWarmup) {
		return "judgeOnly";
	}
	if (run.taskItemId === null) {
		return "review";
	}
	if (items === undefined) {
		return "loading";
	}
	const item = items.find((candidate) => candidate.id === run.taskItemId);
	return item?.isLeaf && item.countsTowardScore ? "review" : "judgeOnly";
}

/** `BenchmarkReviewLimits.MaxNotesCharacters` (Client.Persistence): the node answers 400 above it. */
export const benchmarkReviewNotesMaxLength = 8192;
