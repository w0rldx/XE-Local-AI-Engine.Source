import type {
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryFileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryResponse,
	XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse,
} from "@/core/api/generated";
import type { GgufRepository, GgufRepositoryDetail, GgufRepositoryFile } from "@/features/models/models/GgufModels";

// Maps the generated (OpenAPI) GGUF browse/inspect response types to the stricter domain view-models the Model
// Management GGUF section depends on. The generated types are the single source of truth for the wire shape; their
// fields are all optional (`x?: T`), so each mapper coalesces every field to a required value with a safe default.
// The DTOs carry only sanitized fields (no download URL / token); redaction is the backend's.

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
		sizeBytes: dto.sizeBytes ?? 0,
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
