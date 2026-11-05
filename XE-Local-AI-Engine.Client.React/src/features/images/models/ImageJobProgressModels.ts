import { z } from "zod";

import { type ImageJobStatus, toImageJobStatus } from "@/features/images/models/ImageModels";

// The single SignalR client-method name the ImageJobHub invokes. Must match ImageJobHubEvents.StatusChanged.
export const IMAGE_JOB_STATUS_CHANGED = "imageJob.statusChanged";

// The fine phase inside "Generating", mirroring the backend ImageGenPhase values the runtime can observe. Only
// "Sampling" has a measurable rate: loading and encoding run before step 1 and decoding runs after the last one, so
// neither can honestly carry a countdown.
const imageGenerationPhases = ["Loading", "Encoding", "Sampling", "Decoding"] as const;
export type ImageGenerationPhase = (typeof imageGenerationPhases)[number];

function toImageGenerationPhase(raw: string | null | undefined): ImageGenerationPhase | null {
	return (imageGenerationPhases as readonly string[]).includes(raw ?? "") ? (raw as ImageGenerationPhase) : null;
}

// Status push payload. PascalCase off the wire is normalized to camelCase by the SignalR JSON protocol the server
// configures (same as the GGUF download hub). Validated with zod before use — an unparseable push is dropped.
//
// This schema is HAND-WRITTEN and is not covered by `pnpm openapi:check`, which only regenerates the REST client. A
// field added to ImageJobStatusHubMessage without a matching entry here is silently dropped with no failing check, so
// treat the two as one contract when either changes.
export const imageJobStatusPushSchema = z.object({
	jobId: z.string(),
	phase: z.string(),
	queuePosition: z.number().nullish(),
	elapsedMs: z.number().nullish(),
	imageId: z.string().nullish(),
	sanitizedError: z.string().nullish(),
	occurredAtUtc: z.number(),
	seq: z.number(),
	generationPhase: z.string().nullish(),
	step: z.number().nullish(),
	totalSteps: z.number().nullish(),
	secondsPerIteration: z.number().nullish(),
	estimatedRemainingMs: z.number().nullish(),
});

export type ImageJobStatusPush = z.infer<typeof imageJobStatusPushSchema>;

// The live generation state for one job, held in TanStack Query under its own key. It exists separately from the job
// row because none of it is in the REST contract: the runtime reads it from the daemon's stdout, so it arrives only
// over the hub and disappears when the job ends.
export interface ImageJobProgressView {
	seq: number;
	status: ImageJobStatus;
	queuePosition: number | null;
	generationPhase: ImageGenerationPhase | null;
	step: number | null;
	totalSteps: number | null;
	secondsPerIteration: number | null;
	estimatedRemainingMs: number | null;
	// Server wall-clock (unix ms) of the first push that reported Decoding. sd-server prints no decode progress, so
	// pushes stop for the whole decode; the card ticks an elapsed counter off this instead. Null outside Decoding.
	decodeStartedAtUtc: number | null;
}

export function toImageJobProgressView(push: ImageJobStatusPush): ImageJobProgressView {
	return {
		seq: push.seq,
		status: toImageJobStatus(push.phase),
		queuePosition: push.queuePosition ?? null,
		generationPhase: toImageGenerationPhase(push.generationPhase),
		step: push.step ?? null,
		totalSteps: push.totalSteps ?? null,
		secondsPerIteration: push.secondsPerIteration ?? null,
		estimatedRemainingMs: push.estimatedRemainingMs ?? null,
		decodeStartedAtUtc: push.generationPhase === "Decoding" ? push.occurredAtUtc : null,
	};
}

/**
 * Reconciles a newly arrived progress state against the cached one. Two independent rules, because two different
 * things can go stale:
 *
 * - `seq` is the per-job monotonic sequence, so an older or replayed push is dropped outright.
 * - the step counter is checked separately as a belt-and-braces guard. `seq` is assigned server-side at delivery, so
 *   a pair of step reports that were reordered before that point would BOTH look new and the bar would walk
 *   backwards. The server reports synchronously to keep that from happening; this rule makes the client independent
 *   of that guarantee.
 */
export function keepLatestImageJobProgress(
	current: ImageJobProgressView | null,
	next: ImageJobProgressView,
): ImageJobProgressView {
	if (current === null) {
		return next;
	}
	if (next.seq <= current.seq) {
		return current;
	}
	const isSameSamplingRun =
		current.generationPhase === "Sampling" &&
		next.generationPhase === "Sampling" &&
		current.totalSteps === next.totalSteps &&
		current.step !== null &&
		next.step !== null;
	if (isSameSamplingRun && (next.step as number) < (current.step as number)) {
		return current;
	}
	// A repeated Decoding push must not restart the decode clock.
	if (current.decodeStartedAtUtc !== null && next.decodeStartedAtUtc !== null) {
		return { ...next, decodeStartedAtUtc: current.decodeStartedAtUtc };
	}
	return next;
}

// What the card should actually render. Kept as a discriminated union computed by one pure function so the honesty
// rule — a countdown ONLY while sampling — is stated once and can be tested without a DOM.
export type ImageProgressDisplay =
	| { kind: "none" }
	| { kind: "queued"; queuePosition: number | null }
	| { kind: "preparing" }
	| {
			kind: "sampling";
			step: number;
			totalSteps: number;
			secondsPerIteration: number | null;
			estimatedRemainingMs: number | null;
	  }
	| { kind: "finishing"; decodeStartedAtUtc: number | null };

/**
 * Maps the live progress state onto what the operator is shown.
 *
 * The rule this encodes: a countdown appears ONLY during sampling, and only for a job that is not waiting behind
 * another one. Loading and encoding happen before the first step and the decode happens after the last, so neither
 * has a rate to extrapolate from — showing a countdown there produces "0s left" followed by a wait, which is exactly
 * the experience this timeline replaces.
 */
export function toProgressDisplay(progress: ImageJobProgressView | null): ImageProgressDisplay {
	if (progress === null) {
		return { kind: "none" };
	}
	if (progress.status === "Queued") {
		return { kind: "queued", queuePosition: progress.queuePosition };
	}
	if (progress.status !== "Generating") {
		return { kind: "none" };
	}
	if (progress.generationPhase === "Decoding") {
		// Elapsed time since decode began, never a countdown: there is no rate to extrapolate from.
		return { kind: "finishing", decodeStartedAtUtc: progress.decodeStartedAtUtc };
	}
	if (progress.generationPhase === "Sampling" && progress.step !== null && progress.totalSteps !== null) {
		// A job queued behind another on the daemon would have that job's wait added to its own; rather than guess at
		// it, the estimate is withheld until this job is the one running.
		const isWaitingBehindAnother = (progress.queuePosition ?? 0) > 1;
		return {
			kind: "sampling",
			step: progress.step,
			totalSteps: progress.totalSteps,
			secondsPerIteration: progress.secondsPerIteration,
			estimatedRemainingMs: isWaitingBehindAnother ? null : progress.estimatedRemainingMs,
		};
	}
	return { kind: "preparing" };
}
