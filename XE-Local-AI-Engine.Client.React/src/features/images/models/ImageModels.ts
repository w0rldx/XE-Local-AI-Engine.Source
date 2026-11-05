import type { TFunction } from "i18next";
import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageJobResponse as ImageJobResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageModelResponse as ImageModelResponse,
	XeLocalAiEngineClientEndpointsImagesV1UploadedImageResponse as UploadedImageResponse,
} from "@/core/api/generated";

// Coarse image-job lifecycle, mirroring the backend ImageJobStatus enum (Queued/Generating/Succeeded/Failed/
// Cancelled). This is the only status the REST contract carries; the finer generation timeline (see
// imageGenerationPhases in ImageJobProgressModels) rides the SignalR push alongside it, because the runtime reads it
// from the daemon's stdout rather than from any HTTP field.
const imageJobStatuses = ["Queued", "Generating", "Succeeded", "Failed", "Cancelled"] as const;
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

// How a job edits a source image, mirroring the backend ImageEditModeNames (exact, case-sensitive). img2img redraws the
// source under a strength; reference keeps the source as a reference for an instruction prompt. Which of them a model
// supports is per family, and the node sends it as the model's `editModes`.
const imageEditModes = ["img2img", "reference"] as const;
export type ImageEditMode = (typeof imageEditModes)[number];

function toImageEditMode(raw: string | null | undefined): ImageEditMode | null {
	return (imageEditModes as readonly string[]).includes(raw ?? "") ? (raw as ImageEditMode) : null;
}

/** The operator-facing name of an edit mode. Literal keys, so the i18n default check covers both. */
export function editModeLabel(t: TFunction, mode: ImageEditMode): string {
	return mode === "img2img"
		? t("pages.images.edit.modes.img2img", "Variation")
		: t("pages.images.edit.modes.reference", "Instruction edit");
}

/** The img2img strength a fresh edit starts from — the same value the node substitutes when the field is omitted. */
export const defaultImageEditStrength = 0.75;

// Schema-first form contract. Numeric bounds keep a request within sane runtime limits before it reaches the server
// (which re-validates). modelName is required (a job cannot run without a resolved model); prompt is required and
// bounded so a runaway paste is rejected at the boundary. negativePrompt is optional. Maps 1:1 to CreateImageJobRequest.
export const imageGenerationFormFieldsSchema = z.object({
	modelName: z.string().min(1),
	prompt: z.string().trim().min(1).max(2000),
	negativePrompt: z.string().trim().max(2000).optional(),
	width: z.number().int().min(64).max(2048),
	height: z.number().int().min(64).max(2048),
	steps: z.number().int().min(1).max(150),
	sampler: z.enum(imageSamplers),
	cfgScale: z.number().min(1).max(30),
	seed: z.number().int().min(-1).max(4_294_967_295),
	editMode: z.enum(imageEditModes).optional(),
	sourceImageId: z.string().min(1).optional(),
	strength: z.number().min(0).max(1).optional(),
});

// The edit fields follow the node's CreateImageJobRequestValidator: a mode needs a source and a source needs a mode,
// and strength belongs to img2img alone. Refined on top of the field schema, because a refined object cannot be
// `.pick`ed (ImageFormOverrides picks the sampling fields from the unrefined one).
export const imageGenerationFormSchema = imageGenerationFormFieldsSchema.superRefine((values, ctx) => {
	if ((values.editMode === undefined) !== (values.sourceImageId === undefined)) {
		ctx.addIssue({ code: "custom", path: ["editMode"], message: "An edit needs both a mode and a source image." });
	}
	if (values.strength !== undefined && values.editMode !== "img2img") {
		ctx.addIssue({ code: "custom", path: ["strength"], message: "Strength applies to img2img only." });
	}
});

export type ImageGenerationFormValues = z.infer<typeof imageGenerationFormFieldsSchema>;

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
	/** `null` when sd-server chose its own default (an unclassified model, or a job from before samplers were stored). */
	sampler: string | null;
	cfgScale: number;
	createdAtUtc: number;
	startedAtUtc: number | null;
	completedAtUtc: number | null;
	durationMs: number | null;
	imageId: string | null;
	sanitizedError: string | null;
	editMode: ImageEditMode | null;
	sourceImageId: string | null;
	strength: number | null;
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
		sampler: dto.sampler ?? null,
		cfgScale: dto.cfgScale,
		createdAtUtc: dto.createdAtUtc,
		startedAtUtc: dto.startedAtUtc ?? null,
		completedAtUtc: dto.completedAtUtc ?? null,
		durationMs: dto.durationMs ?? null,
		imageId: dto.imageId ?? null,
		sanitizedError: dto.sanitizedError ?? null,
		editMode: toImageEditMode(dto.editMode),
		sourceImageId: dto.sourceImageId ?? null,
		strength: dto.strength ?? null,
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
	/** The edit modes this model's family supports; empty = the model cannot edit. */
	editModes: readonly ImageEditMode[];
	/** The pixel count (width × height) the family was trained at; edit dimensions are fitted to it. */
	nativePixels: number;
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
		// An unknown mode name from a newer node is dropped rather than offered: this client cannot label or send it.
		editModes: dto.editModes.map(toImageEditMode).filter((mode): mode is ImageEditMode => mode !== null),
		nativePixels: dto.nativePixels,
	};
}

/** An image uploaded to be edited: no job, no prompt, just the source dimensions. */
export interface UploadedImageView {
	imageId: string;
	width: number;
	height: number;
	createdAtUtc: number;
}

export function toUploadedImageView(dto: UploadedImageResponse): UploadedImageView {
	return { imageId: dto.imageId, width: dto.width, height: dto.height, createdAtUtc: dto.createdAtUtc };
}

/** The image an edit starts from — a generated job's image or an upload — with the dimensions its aspect comes from. */
export interface ImageEditSource {
	imageId: string;
	width: number;
	height: number;
}

const editDimensionStep = 64;
const editDimensionMin = 64;
const editDimensionMax = 2048;

function fitEditDimension(value: number): number {
	const rounded = Math.round(value / editDimensionStep) * editDimensionStep;
	return Math.min(editDimensionMax, Math.max(editDimensionMin, rounded));
}

/**
 * The output size an edit defaults to: the source's aspect ratio scaled to the model's native pixel count, each side
 * rounded to a multiple of 64 and clamped to 64..2048. A 4000×3000 photo edited on SD1.5 becomes 576×448, not a
 * 3968×2944 job that would run out of memory; the operator can still override both fields.
 */
export function fitEditDimensions(
	source: { width: number; height: number },
	nativePixels: number,
): { width: number; height: number } {
	const scale = Math.sqrt(nativePixels / (source.width * source.height));
	return { width: fitEditDimension(source.width * scale), height: fitEditDimension(source.height * scale) };
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
	const sampler = (imageSamplers as readonly string[]).includes(model.defaultSampler)
		? (model.defaultSampler as ImageSampler)
		: base.sampler;
	return { ...base, steps: model.defaultSteps, cfgScale: model.defaultCfgScale, sampler };
}

// Job-history page size. Deliberately smaller than the app-wide default of 25: every succeeded card fetches,
// decrypts and then holds its own PNG, so a page of this list costs real network and memory rather than a few
// table rows. The node caps a page at 200 (ListImageJobsRequestValidator.MaxLimit), well above every option here.
export const imageJobsPerPage = 10;
export const imageJobPageSizeOptions: readonly number[] = [10, 25, 50];
