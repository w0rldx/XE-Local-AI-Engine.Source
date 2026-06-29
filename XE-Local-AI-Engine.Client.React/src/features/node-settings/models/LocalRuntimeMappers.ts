import type {
	XeLocalAiEngineClientEndpointsModelFitV1CudaBuildPrerequisitesResponse,
	XeLocalAiEngineClientEndpointsModelFitV1CudaBuildStatusResponse,
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse,
} from "@/core/api/generated";
import type {
	CudaBuildPrerequisites,
	CudaBuildStatus,
	LlamaCppRuntimeStatus,
} from "@/features/node-settings/models/LocalRuntimeModels";

// Maps the read-only runtime-status response (GET model-fit/llamacpp/runtime) to the domain view-model. Every optional
// generated field is coalesced to a required value. `installed` is mapped to a nested view-model or null when absent;
// `upstreamLatestTag` keeps its nullable shape (it is only surfaced under developer mode). `runningProcessCount`
// coalesces to 0 when the field is absent (an older/empty payload), so the UI treats "unknown" as "safe to update".
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
						isSourceBuild: installed.isSourceBuild ?? false,
					}
				: null,
		recommendedTag: dto.recommendedTag ?? "",
		upstreamLatestTag: dto.upstreamLatestTag ?? null,
		updateAvailable: dto.updateAvailable ?? false,
		isOffline: dto.isOffline ?? false,
		runningProcessCount: dto.runningProcessCount ?? 0,
		isSourceBuild: dto.isSourceBuild ?? false,
		rebuildAvailable: dto.rebuildAvailable ?? false,
	};
}

// Maps the CUDA build prerequisite report to the domain view-model. Each item's optional fields are coalesced; an absent
// `items` array becomes empty so the checklist renders nothing rather than throwing. `canBuild` defaults to false (the
// safe gate) when absent.
export function toCudaBuildPrerequisites(
	dto: XeLocalAiEngineClientEndpointsModelFitV1CudaBuildPrerequisitesResponse,
): CudaBuildPrerequisites {
	return {
		items: (dto.items ?? []).map((item) => ({
			key: item.key ?? "",
			satisfied: item.satisfied ?? false,
			detail: item.detail ?? "",
		})),
		canBuild: dto.canBuild ?? false,
	};
}

// Maps the persisted CUDA build status to the domain view-model. `logLines` coalesces to an empty array; the nullable
// `sanitizedError` and `tag` keep their nullable shape. `isRunning`/`terminal` default to false (the safe idle gate).
export function toCudaBuildStatus(
	dto: XeLocalAiEngineClientEndpointsModelFitV1CudaBuildStatusResponse,
): CudaBuildStatus {
	return {
		phase: dto.phase ?? "",
		isRunning: dto.isRunning ?? false,
		terminal: dto.terminal ?? false,
		logLines: dto.logLines ?? [],
		sanitizedError: dto.sanitizedError ?? null,
		tag: dto.tag ?? null,
	};
}
