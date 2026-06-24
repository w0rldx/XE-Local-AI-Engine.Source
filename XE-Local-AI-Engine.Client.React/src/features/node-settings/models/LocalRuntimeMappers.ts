import type {
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse,
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppVersionResponse,
} from "@/core/api/generated";
import type { LlamaCppRuntimeStatus, LlamaCppVersion } from "@/features/node-settings/models/LocalRuntimeModels";

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

// Maps the read-only runtime-status response (GET model-fit/llamacpp/runtime) to the domain view-model. Mirrors the
// `toLlamaCppVersion` coalescing posture: every optional generated field is coalesced to a required value. `installed`
// is mapped to a nested view-model or null when absent; `upstreamLatestTag` keeps its nullable shape (it is only
// surfaced under developer mode).
export function toLlamaCppRuntimeStatus(
	dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse,
): LlamaCppRuntimeStatus {
	const installed = dto.installed;
	return {
		installed:
			installed != null
				? {
						tag: installed.tag ?? "",
						variant: installed.variant ?? "",
						asset: installed.asset ?? "",
						installedAtUtc: installed.installedAtUtc ?? undefined,
					}
				: null,
		recommendedTag: dto.recommendedTag ?? "",
		upstreamLatestTag: dto.upstreamLatestTag ?? null,
		updateAvailable: dto.updateAvailable ?? false,
		isOffline: dto.isOffline ?? false,
	};
}
