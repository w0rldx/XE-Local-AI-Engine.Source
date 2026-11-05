import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageModelCatalogEntryResponse as ImageModelCatalogEntryResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageModelDownloadStatusResponse as ImageModelDownloadStatusResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageRepositoryFileResponse as ImageRepositoryFileResponse,
	XeLocalAiEngineClientEndpointsImagesV1ImageRepositoryResponse as ImageRepositoryResponse,
} from "@/core/api/generated";

// The diffusion families the install forms offer. Must match the backend ImageModelFamily enum names (parsed
// case-insensitively). Sd15 is single-file; every other family is a file SET.
export const imageModelFamilies = ["Sd15", "Sdxl", "Sd3", "Flux", "QwenImage"] as const;
export type ImageModelFamily = (typeof imageModelFamilies)[number];

// Part roles the backend ImageModelPartRole enum accepts, in the order a file-set is normally listed. Llm/LlmVision
// are the Qwen-Image text encoder (a full Qwen2.5-VL language model) and its vision tower.
export const imageModelPartRoles = ["Diffusion", "Vae", "ClipL", "ClipG", "T5", "Llm", "LlmVision"] as const;
export type ImageModelPartRole = (typeof imageModelPartRoles)[number];

function toImageModelPartRole(raw: string | null | undefined): ImageModelPartRole {
	return (imageModelPartRoles as readonly string[]).includes(raw ?? "") ? (raw as ImageModelPartRole) : "Diffusion";
}

function toImageModelFamily(raw: string | null | undefined): ImageModelFamily {
	return (imageModelFamilies as readonly string[]).includes(raw ?? "") ? (raw as ImageModelFamily) : "Sd15";
}

// How a catalog entry's weights compare to this machine's measured memory budget. "Unknown" is a real answer, not a soft
// yes: VRAM goes unmeasured on every non-NVIDIA GPU, and a badge that read "Fits" there would be a guess.
const imageModelFitVerdicts = ["Fits", "Tight", "WontFit", "Unknown"] as const;
export type ImageModelFitVerdict = (typeof imageModelFitVerdicts)[number];

function toImageModelFitVerdict(raw: string | null | undefined): ImageModelFitVerdict {
	return (imageModelFitVerdicts as readonly string[]).includes(raw ?? "") ? (raw as ImageModelFitVerdict) : "Unknown";
}

/** One weight file of a catalog entry, already in the shape the download mutation posts. */
export interface ImageModelCatalogPartView {
	role: ImageModelPartRole;
	fileName: string;
	repoId: string | null;
	sizeBytes: number;
}

/** One curated, one-click-installable image model annotated for this machine. */
export interface ImageModelCatalogEntryView {
	id: string;
	displayName: string;
	publisher: string;
	repoId: string;
	family: ImageModelFamily;
	license: string;
	recommended: boolean;
	notes: string | null;
	parts: readonly ImageModelCatalogPartView[];
	totalSizeBytes: number;
	isInstalled: boolean;
	fitVerdict: ImageModelFitVerdict;
	residentBytes: number;
	fitBudgetBytes: number;
	fitsOnDisk: boolean;
}

export function toImageModelCatalogEntryView(dto: ImageModelCatalogEntryResponse): ImageModelCatalogEntryView {
	return {
		id: dto.id,
		displayName: dto.displayName,
		publisher: dto.publisher,
		repoId: dto.repoId,
		family: toImageModelFamily(dto.family),
		license: dto.license,
		recommended: dto.recommended,
		notes: dto.notes ?? null,
		parts: (dto.parts ?? []).map((part) => ({
			role: toImageModelPartRole(part.role),
			fileName: part.fileName,
			repoId: part.repoId ?? null,
			sizeBytes: part.sizeBytes,
		})),
		totalSizeBytes: dto.totalSizeBytes,
		isInstalled: dto.isInstalled,
		fitVerdict: toImageModelFitVerdict(dto.fitVerdict),
		residentBytes: dto.residentBytes,
		fitBudgetBytes: dto.fitBudgetBytes,
		fitsOnDisk: dto.fitsOnDisk,
	};
}

/** How many distinct repositories a catalog entry's parts are pulled from (a Qwen-Image set spans two). */
export function catalogEntryRepoCount(entry: ImageModelCatalogEntryView): number {
	return new Set([entry.repoId, ...entry.parts.map((part) => part.repoId ?? entry.repoId)]).size;
}

export interface ImageRepositoryView {
	repoId: string;
	isGated: boolean;
	downloads: number;
	likes: number;
	lastModifiedAtUtc: number;
	license: string | null;
	hasUsableWeights: boolean;
	isTrustedPublisher: boolean;
}

export function toImageRepositoryView(dto: ImageRepositoryResponse): ImageRepositoryView {
	return {
		repoId: dto.repoId,
		isGated: dto.isGated,
		downloads: dto.downloads,
		likes: dto.likes,
		lastModifiedAtUtc: dto.lastModifiedAtUtc,
		license: dto.license ?? null,
		hasUsableWeights: dto.hasUsableWeights,
		isTrustedPublisher: dto.isTrustedPublisher,
	};
}

/** One selectable weight file inside an inspected repository. */
export interface ImageRepositoryFileView {
	fileName: string;
	format: string;
	sizeBytes: number;
	suggestedRole: ImageModelPartRole;
	/** Why this file cannot be the Diffusion part (today only `diffusers_layout_unsupported`); absent/null = usable. */
	unsupportedReason?: string | null;
}

export function toImageRepositoryFileView(dto: ImageRepositoryFileResponse): ImageRepositoryFileView {
	return {
		fileName: dto.fileName,
		format: dto.format,
		sizeBytes: dto.sizeBytes,
		suggestedRole: toImageModelPartRole(dto.suggestedRole),
		unsupportedReason: dto.unsupportedReason ?? null,
	};
}

/**
 * Guesses the family a repository's selected file-set belongs to, so the install form starts on the right one instead
 * of on SD 1.5 for every model. Purely a starting value the operator can override — the family drives which sampling
 * defaults the generation form pre-fills, and getting it wrong does not fail loudly, it just produces a worse image.
 */
export function suggestFamilyForRepo(repoId: string, files: readonly ImageRepositoryFileView[]): ImageModelFamily {
	const haystack = `${repoId} ${files.map((file) => file.fileName).join(" ")}`.toLowerCase();
	if (haystack.includes("qwen")) {
		return "QwenImage";
	}
	if (haystack.includes("flux")) {
		return "Flux";
	}
	if (haystack.includes("sd3") || haystack.includes("stable-diffusion-3")) {
		return "Sd3";
	}
	if (haystack.includes("xl")) {
		return "Sdxl";
	}
	return "Sd15";
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

/** One declared file of a multi-part download draft, as edited in the manual download form. */
export interface PartDraft {
	// Stable identity for the row, so removing a middle row does not remount the ones after it.
	id: string;
	role: ImageModelPartRole;
	fileName: string;
	// Blank = the set's repo. A file-set is not always published in one place: a Qwen-Image install takes its diffusion
	// weights and VAE from one repo and the Qwen2.5-VL text encoder from another.
	repoId: string;
	// Blank = unknown. A declared size is what makes the free-disk pre-flight run at all and what lets the backend
	// compute one aggregate percentage instead of a bar that restarts per part — on an 18 GB set that is the difference
	// between a usable progress display and a mystery.
	sizeBytes: string;
	sha256: string;
}

/** The manual download form's in-progress draft: a model name/repo/family plus its declared file-set. */
export interface DownloadDraft {
	repoId: string;
	fileName: string;
	modelName: string;
	family: ImageModelFamily;
	// The simple form sends a single Diffusion part (correct and sufficient for SD1.5); the advanced one sends the
	// whole declared file-set.
	isAdvanced: boolean;
	parts: readonly PartDraft[];
}

/** The file-set an operator assembled in the browse panel, ready for the install mutation. */
export interface BrowseInstallRequest {
	modelName: string;
	repoId: string;
	family: ImageModelFamily;
	parts: readonly {
		role: ImageModelPartRole;
		fileName: string;
		sizeBytes: number;
	}[];
}
