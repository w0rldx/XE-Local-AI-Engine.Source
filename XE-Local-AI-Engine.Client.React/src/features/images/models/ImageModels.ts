import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageJobResponse as ImageJobResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageModelDownloadStatusResponse as ImageModelDownloadStatusResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageModelResponse as ImageModelResponse,
} from "@/core/api/generated";

// Coarse image-job lifecycle, mirroring the backend ImageJobStatus enum (Queued/Generating/Succeeded/Failed/
// Cancelled). This is the only status the REST contract carries; the finer generation timeline (see
// imageGenerationPhases) rides the SignalR push alongside it, because the runtime reads it from the daemon's stdout
// rather than from any HTTP field.
export const imageJobStatuses = ["Queued", "Generating", "Succeeded", "Failed", "Cancelled"] as const;
export type ImageJobStatus = (typeof imageJobStatuses)[number];

// A status is terminal once the job can no longer transition. Non-terminal jobs are the ones the hub subscribes to
// for live push (queued position / generating elapsed / the eventual terminal transition).
const terminalStatuses = new Set<ImageJobStatus>(["Succeeded", "Failed", "Cancelled"]);

export function isTerminalStatus(status: ImageJobStatus): boolean {
	return terminalStatuses.has(status);
}

// Normalizes an arbitrary wire status string into the known union, falling back to "Queued" for an unrecognized value
// so an unknown backend state never crashes the list rendering.
export function toImageJobStatus(raw: string | null | undefined): ImageJobStatus {
	return (imageJobStatuses as readonly string[]).includes(raw ?? "") ? (raw as ImageJobStatus) : "Queued";
}

// Sampling methods accepted by sd-server's `sample_method`. Kept as a small curated set — the backend
// re-validates, and an unknown sampler simply falls back to the runtime default. euler_a is the safe default for SD1.5.
export const imageSamplers = ["euler", "euler_a", "heun", "dpm2", "dpmpp2m", "dpmpp2s_a", "lcm"] as const;
export type ImageSampler = (typeof imageSamplers)[number];

// Form defaults tuned for the first supported image model, SD1.5 at 512x512. seed -1 = runtime-random.
export const imageFormDefaults = {
	prompt: "",
	negativePrompt: "",
	width: 512,
	height: 512,
	steps: 20,
	sampler: "euler_a" as ImageSampler,
	cfgScale: 7,
	seed: -1,
} as const;

// Schema-first form contract. Numeric bounds keep a request within sane runtime limits before it reaches the server
// (which re-validates). modelName is required (a job cannot run without a resolved model); prompt is required and
// bounded so a runaway paste is rejected at the boundary. negativePrompt is optional. Maps 1:1 to CreateImageJobRequest.
export const imageGenerationFormSchema = z.object({
	modelName: z.string().min(1),
	prompt: z.string().trim().min(1).max(2000),
	negativePrompt: z.string().trim().max(2000).optional(),
	width: z.number().int().min(64).max(2048),
	height: z.number().int().min(64).max(2048),
	steps: z.number().int().min(1).max(150),
	sampler: z.enum(imageSamplers),
	cfgScale: z.number().min(1).max(30),
	seed: z.number().int().min(-1).max(4_294_967_295),
});

export type ImageGenerationFormValues = z.infer<typeof imageGenerationFormSchema>;

// Domain view-models derived from the (optional-field) generated DTOs — a strict shape the components render against.
export interface ImageJobView {
	id: string;
	modelName: string;
	prompt: string;
	negativePrompt: string | null;
	status: ImageJobStatus;
	seed: number;
	width: number;
	height: number;
	steps: number;
	sampler: string;
	cfgScale: number;
	createdAtUtc: number;
	startedAtUtc: number | null;
	completedAtUtc: number | null;
	durationMs: number | null;
	imageId: string | null;
	sanitizedError: string | null;
}

export function toImageJobView(dto: ImageJobResponse): ImageJobView {
	return {
		id: dto.id,
		modelName: dto.modelName,
		prompt: dto.prompt,
		negativePrompt: dto.negativePrompt ?? null,
		status: toImageJobStatus(dto.status),
		// The wire seed is a precision-safe string; image seeds are bounded to uint32 (see the form schema), so parsing
		// back to a number for display never loses precision.
		seed: Number(dto.seed),
		width: dto.width,
		height: dto.height,
		steps: dto.steps,
		sampler: dto.sampler,
		cfgScale: dto.cfgScale,
		createdAtUtc: dto.createdAtUtc,
		startedAtUtc: dto.startedAtUtc ?? null,
		completedAtUtc: dto.completedAtUtc ?? null,
		durationMs: dto.durationMs ?? null,
		imageId: dto.imageId ?? null,
		sanitizedError: dto.sanitizedError ?? null,
	};
}

export interface ImageModelView {
	modelName: string;
	repoId: string;
	family: string;
	kind: string;
	sizeBytes: number;
	downloadedAtUtc: number;
	// Per-family starting parameters (backend ImageFamilyDefaults). They exist because the wrong ones do not fail —
	// FLUX-schnell run at SD1.5's 20 steps / CFG 7 just produces a burnt image, five times slower.
	defaultSteps: number;
	defaultCfgScale: number;
	defaultSampler: string;
}

export function toImageModelView(dto: ImageModelResponse): ImageModelView {
	return {
		modelName: dto.modelName,
		repoId: dto.repoId,
		family: dto.family,
		kind: dto.kind,
		sizeBytes: dto.sizeBytes,
		downloadedAtUtc: dto.downloadedAtUtc,
		defaultSteps: dto.defaultSteps,
		defaultCfgScale: dto.defaultCfgScale,
		defaultSampler: dto.defaultSampler,
	};
}

/**
 * The form values a freshly-selected model should start from: the shared defaults with the model's family-specific
 * sampling parameters applied over them. The sampler is only adopted when the backend names one this form can actually
 * offer, so an unknown method name leaves the picker on a valid value instead of blanking it.
 */
export function imageFormDefaultsForModel(model: ImageModelView | undefined): ImageGenerationFormValues {
	const base: ImageGenerationFormValues = { ...imageFormDefaults, modelName: model?.modelName ?? "" };
	if (model === undefined) {
		return base;
	}
	const sampler = (imageSamplers as readonly string[]).includes(model.defaultSampler) ? (model.defaultSampler as ImageSampler) : base.sampler;
	return { ...base, steps: model.defaultSteps, cfgScale: model.defaultCfgScale, sampler };
}

// Coarse lifecycle of an image-model weight download, mirroring the backend ImageModelDownloadPhase enum. A download
// ALWAYS ends in one of the three terminal phases — that is the whole point of the coordinator: a failure that is never
// reported is indistinguishable from a slow download.
const imageModelDownloadPhases = ["Running", "Completed", "Cancelled", "Failed"] as const;
export type ImageModelDownloadPhase = (typeof imageModelDownloadPhases)[number];

// Normalizes an arbitrary wire phase into the known union. An unrecognized value is treated as "Running" so a future
// backend phase never makes the UI declare a live download finished.
function toImageModelDownloadPhase(raw: string | null | undefined): ImageModelDownloadPhase {
	return (imageModelDownloadPhases as readonly string[]).includes(raw ?? "") ? (raw as ImageModelDownloadPhase) : "Running";
}

export interface ImageModelDownloadView {
	modelName: string;
	phase: ImageModelDownloadPhase;
	completedBytes: number | null;
	totalBytes: number | null;
	sanitizedError: string | null;
	// An image model is a file SET (diffusion + VAE + text encoders). Without the part framing the operator cannot tell
	// "the bar advanced into part 2" from "something restarted"; null until the first progress event names a part.
	partIndex: number | null;
	partCount: number | null;
}

export function toImageModelDownloadView(dto: ImageModelDownloadStatusResponse): ImageModelDownloadView {
	return {
		modelName: dto.modelName,
		phase: toImageModelDownloadPhase(dto.phase),
		completedBytes: dto.completedBytes ?? null,
		totalBytes: dto.totalBytes ?? null,
		sanitizedError: dto.sanitizedError ?? null,
		partIndex: dto.partIndex ?? null,
		partCount: dto.partCount ?? null,
	};
}

// The single SignalR client-method name the ImageJobHub invokes. Must match ImageJobHubEvents.StatusChanged.
export const IMAGE_JOB_STATUS_CHANGED = "imageJob.statusChanged";

// The fine phase inside "Generating", mirroring the backend ImageGenPhase values the runtime can observe. Only
// "Sampling" has a measurable rate: loading and encoding run before step 1 and decoding runs after the last one, so
// neither can honestly carry a countdown.
export const imageGenerationPhases = ["Loading", "Encoding", "Sampling", "Decoding"] as const;
export type ImageGenerationPhase = (typeof imageGenerationPhases)[number];

function toImageGenerationPhase(raw: string | null | undefined): ImageGenerationPhase | null {
	return (imageGenerationPhases as readonly string[]).includes(raw ?? "") ? (raw as ImageGenerationPhase) : null;
}

// Status push payload. PascalCase off the wire is normalized to camelCase by the SignalR JSON protocol the server
// configures (same as the GGUF download hub). Validated with zod before use — an unparseable push is dropped.
//
// This schema is HAND-WRITTEN and is not covered by `pnpm openapi:check`, which only regenerates the REST client. A
// field added to ImageJobStatusHubEvent without a matching entry here is silently dropped with no failing check, so
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
export function keepLatestImageJobProgress(current: ImageJobProgressView | null, next: ImageJobProgressView): ImageJobProgressView {
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
	return next;
}

// What the card should actually render. Kept as a discriminated union computed by one pure function so the honesty
// rule — a countdown ONLY while sampling — is stated once and can be tested without a DOM.
export type ImageProgressDisplay =
	| { kind: "none" }
	| { kind: "queued"; queuePosition: number | null }
	| { kind: "preparing" }
	| { kind: "sampling"; step: number; totalSteps: number; secondsPerIteration: number | null; estimatedRemainingMs: number | null }
	| { kind: "finishing" };

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
		return { kind: "finishing" };
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
