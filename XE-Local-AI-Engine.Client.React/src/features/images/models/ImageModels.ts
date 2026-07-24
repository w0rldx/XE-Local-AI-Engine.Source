import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageJobResponse as ImageJobResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageModelResponse as ImageModelResponse,
} from "@/core/api/generated";

// Coarse image-job lifecycle, mirroring the backend ImageJobStatus enum (Queued/Generating/Succeeded/Failed/
// Cancelled). Progress is deliberately coarse — the sd-server runtime exposes no step/percent over HTTP,
// so the UI shows queued→generating(elapsed)→terminal, never a step-progress bar.
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

// Form defaults tuned for the step-1 target model (SD1.5 @ 512, plan decision 7). seed -1 = runtime-random.
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
}

export function toImageModelView(dto: ImageModelResponse): ImageModelView {
	return {
		modelName: dto.modelName,
		repoId: dto.repoId,
		family: dto.family,
		kind: dto.kind,
		sizeBytes: dto.sizeBytes,
		downloadedAtUtc: dto.downloadedAtUtc,
	};
}

// The single SignalR client-method name the ImageJobHub invokes. Must match ImageJobHubEvents.StatusChanged.
export const IMAGE_JOB_STATUS_CHANGED = "imageJob.statusChanged";

// Coarse status push payload. PascalCase off the wire is normalized to camelCase by the SignalR JSON protocol the
// server configures (same as the GGUF download hub). Validated with zod before use — an unparseable push is dropped.
export const imageJobStatusPushSchema = z.object({
	jobId: z.string(),
	phase: z.string(),
	queuePosition: z.number().nullish(),
	elapsedMs: z.number().nullish(),
	imageId: z.string().nullish(),
	sanitizedError: z.string().nullish(),
	occurredAtUtc: z.number(),
	seq: z.number(),
});

export type ImageJobStatusPush = z.infer<typeof imageJobStatusPushSchema>;
