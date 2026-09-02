import type {
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryFileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryResponse,
	XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse,
} from "@/core/api/generated";
import type {
	GgufFitVerdict,
	GgufQuantTier,
	GgufRepository,
	GgufRepositoryDetail,
	GgufRepositoryFile,
} from "@/features/models/models/GgufModels";

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Only API-projected sanitized fields are exposed.

export function toGgufRepository(dto: XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryResponse): GgufRepository {
	return {
		repoId: dto.repoId ?? "",
		isGated: dto.isGated ?? false,
		downloads: dto.downloads ?? 0,
		likes: dto.likes ?? 0,
		lastModifiedAtUtc: dto.lastModifiedAtUtc ?? null,
		license: dto.license ?? null,
		hasUsableGguf: dto.hasUsableGguf ?? false,
		isTrustedPublisher: dto.isTrustedPublisher ?? false,
	};
}

function toGgufRepositoryFile(dto: XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryFileResponse): GgufRepositoryFile {
	return {
		fileName: dto.fileName ?? "",
		quant: dto.quant ?? "",
		isDynamic: dto.isDynamic ?? false,
		// Default false: an older backend that does not send the flag has no draft concept, so every file it lists is a
		// base quant — treating an omitted flag as "draft" would hide real quants.
		isDraft: dto.isDraft ?? false,
		sizeBytes: dto.sizeBytes ?? 0,
		// The backend only ever emits the known enum-name values, so a plain cast is safe; defaults cover the
		// degraded/omitted case (e.g. an old backend or a discovery failure) — Balanced/Unknown are the neutral picks.
		qualityTier: (dto.qualityTier as GgufQuantTier) ?? "Balanced",
		fitVerdict: (dto.fitVerdict as GgufFitVerdict) ?? "Unknown",
		isRecommended: dto.isRecommended ?? false,
	};
}

export function toGgufRepositoryDetail(
	dto: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse,
): GgufRepositoryDetail {
	return {
		repoId: dto.repoId ?? "",
		// A discovery failure returns an empty file list (200); coalesce defensively in case it is omitted.
		files: (dto.files ?? []).map(toGgufRepositoryFile),
	};
}
