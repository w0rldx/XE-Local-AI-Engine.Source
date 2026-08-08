import type { XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse } from "@/core/api/generated";
import type { LlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeModels";

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
						sourceRepository: installed.sourceRepository ?? null,
						sourceCommit: installed.sourceCommit ?? null,
						sourceSelection: installed.sourceSelection ?? null,
						sourceRevisionMode: installed.sourceRevisionMode ?? null,
						sourceRequestedCommit: installed.sourceRequestedCommit ?? null,
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
