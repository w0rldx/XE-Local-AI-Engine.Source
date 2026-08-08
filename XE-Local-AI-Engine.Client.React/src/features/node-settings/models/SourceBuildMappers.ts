import type {
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppSourceBuildPrerequisitesResponse,
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppSourceBuildStatusResponse,
} from "@/core/api/generated";
import type {
	LlamaCppSourceBuildPrerequisites,
	LlamaCppSourceBuildStatus,
} from "@/features/node-settings/models/SourceBuildModels";

export function toSourceBuildPrerequisites(
	dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppSourceBuildPrerequisitesResponse,
): LlamaCppSourceBuildPrerequisites {
	return {
		backend: dto.backend ?? "cpu",
		canBuild: dto.canBuild ?? false,
		items: (dto.items ?? []).map((item) => ({
			key: item.key ?? "",
			satisfied: item.satisfied ?? false,
			detail: item.detail ?? "",
		})),
	};
}

export function toSourceBuildStatus(
	dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppSourceBuildStatusResponse,
): LlamaCppSourceBuildStatus {
	const build = dto.currentBuild;
	return {
		phase: dto.phase ?? "Idle",
		isRunning: dto.isRunning ?? false,
		terminal: dto.terminal ?? false,
		logStartSequence: dto.logStartSequence,
		logLines: dto.logLines ?? [],
		sanitizedError: dto.sanitizedError ?? null,
		currentBuild:
			build == null
				? null
				: {
						buildId: build.buildId ?? "",
						backend: build.backend ?? "cpu",
						source: build.source ?? "official",
						repository: build.repository ?? "",
						revisionMode: build.revisionMode ?? "enginePinned",
						requestedCommit: build.requestedCommit ?? null,
						resolvedCommit: build.resolvedCommit ?? null,
					},
	};
}
