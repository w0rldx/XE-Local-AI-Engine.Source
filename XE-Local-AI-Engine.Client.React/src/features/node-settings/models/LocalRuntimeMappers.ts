import type { XeLocalAiEngineClientEndpointsModelFitV1LlamaCppVersionResponse } from "@/core/api/generated";
import type { LlamaCppVersion } from "@/features/node-settings/models/LocalRuntimeModels";

// Maps the generated (OpenAPI) llama.cpp version response to the stricter domain view-model the Node Settings
// local-runtime card depends on. The generated fields are all optional (`x?: T`), so the mapper coalesces every
// field to a required value with a safe default.
export function toLlamaCppVersion(dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppVersionResponse): LlamaCppVersion {
	return {
		version: dto.version ?? "",
		variant: dto.variant ?? "",
		isPinnedFallback: dto.isPinnedFallback ?? false,
		pinnedTag: dto.pinnedTag ?? "",
	};
}
