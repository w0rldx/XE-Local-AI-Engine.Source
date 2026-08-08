import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageRuntimeStatusResponse as ImageRuntimeStatusDto,
	XeLocalAiEngineClientEndpointsImagesV1StableDiffusionCppSourceBuildPrerequisitesResponse as SourceBuildPrerequisitesDto,
	XeLocalAiEngineClientEndpointsImagesV1StableDiffusionCppSourceBuildStatusResponse as SourceBuildStatusDto,
} from "@/core/api/generated/types.gen";
import type {
	ImageRuntimeSourceBuildPrerequisites,
	ImageRuntimeSourceBuildStatus,
	ImageRuntimeStatus,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";

export function toImageRuntimeSourceBuildPrerequisites(dto: SourceBuildPrerequisitesDto): ImageRuntimeSourceBuildPrerequisites {
	return {
		backend: dto.backend,
		canBuild: dto.canBuild,
		items: dto.items.map((item) => ({
			key: item.key,
			satisfied: item.satisfied,
			detail: item.detail,
		})),
	};
}

export function toImageRuntimeSourceBuildStatus(dto: SourceBuildStatusDto): ImageRuntimeSourceBuildStatus {
	const build = dto.currentBuild;
	return {
		phase: dto.phase,
		isRunning: dto.isRunning,
		terminal: dto.terminal,
		logStartSequence: dto.logStartSequence,
		logLines: dto.logLines,
		sanitizedError: dto.sanitizedError ?? null,
		currentBuild:
			build == null
				? null
				: {
						buildId: build.buildId,
						backend: build.backend,
						source: build.source,
						repository: build.repository,
						revisionMode: build.revisionMode,
						requestedCommit: build.requestedCommit ?? null,
						resolvedCommit: build.resolvedCommit ?? null,
					},
	};
}

export function toImageRuntimeStatus(dto: ImageRuntimeStatusDto): ImageRuntimeStatus {
	const managed = dto.managedRuntime;
	const activity = dto.activity;
	return {
		managedRuntime:
			managed == null
				? null
				: {
						validity: managed.validity,
						desiredBackend: managed.desiredBackend,
						sourceRepository: managed.sourceRepository,
						sourceCommit: managed.sourceCommit,
						sourceSelection: managed.sourceSelection,
						sourceRevisionMode: managed.sourceRevisionMode,
						sourceRequestedCommit: managed.sourceRequestedCommit ?? null,
						installedAtUtc: managed.installedAtUtc,
						invalidReason: managed.invalidReason ?? null,
					},
		activity: {
			activeJobCount: activity.activeJobCount,
			spawnReadinessCount: activity.spawnReadinessCount,
			residentProcessCount: activity.residentProcessCount,
			mutationReserved: activity.mutationReserved,
			evictionReserved: activity.evictionReserved,
			isBusy: activity.isBusy,
		},
	};
}
