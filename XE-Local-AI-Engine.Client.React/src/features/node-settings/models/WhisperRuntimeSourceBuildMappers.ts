import type {
	XeLocalAiEngineClientEndpointsTranscriptionV1TranscriptionRuntimeStatusResponse as TranscriptionRuntimeStatusDto,
	XeLocalAiEngineClientEndpointsTranscriptionV1WhisperCppSourceBuildPrerequisitesResponse as WhisperSourceBuildPrerequisitesDto,
	XeLocalAiEngineClientEndpointsTranscriptionV1WhisperCppSourceBuildStatusResponse as WhisperSourceBuildStatusDto,
} from "@/core/api/generated/types.gen";
import type {
	WhisperRuntimeStatus,
	WhisperSourceBuildPrerequisites,
	WhisperSourceBuildStatus,
} from "@/features/node-settings/models/WhisperRuntimeSourceBuildModels";

export function toWhisperSourceBuildPrerequisites(dto: WhisperSourceBuildPrerequisitesDto): WhisperSourceBuildPrerequisites {
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

export function toWhisperSourceBuildStatus(dto: WhisperSourceBuildStatusDto): WhisperSourceBuildStatus {
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

/**
 * The source-build card reads only the two parts of the runtime status it acts on: the managed-build record it can
 * remove, and the activity that decides whether a build or an eject is allowed. Everything else on that response
 * (state, resolved backend, model, VAD) belongs to the transcription page's own runtime card.
 */
export function toWhisperRuntimeStatus(dto: TranscriptionRuntimeStatusDto): WhisperRuntimeStatus {
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
			activeTranscriptionCount: activity.activeTranscriptionCount,
			spawnReadinessCount: activity.spawnReadinessCount,
			residentProcessCount: activity.residentProcessCount,
			mutationReserved: activity.mutationReserved,
			evictionReserved: activity.evictionReserved,
			isBusy: activity.isBusy,
		},
	};
}
