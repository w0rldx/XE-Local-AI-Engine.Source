import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsTrainingRunsV1TrainingRunDefaultsResponse as TrainingRunDefaultsResponse,
	XeLocalAiEngineClientEndpointsTrainingRunsV1TrainingRunOptionsPayload as TrainingRunOptionsPayload,
	XeLocalAiEngineClientEndpointsTrainingRunsV1TrainingRunResponse as TrainingRunResponse,
} from "@/core/api/generated";

export type TrainingRunStatusValue =
	| "Queued"
	| "Preparing"
	| "Training"
	| "Exporting"
	| "Smoke"
	| "Succeeded"
	| "Failed"
	| "Cancelled";

export interface TrainingRunOptionsView {
	readonly maxSeqLength: number;
	readonly loraR: number;
	readonly loraAlpha: number;
	readonly loraDropout: number;
	readonly perDeviceTrainBatchSize: number;
	readonly gradientAccumulationSteps: number;
	readonly learningRate: number;
	readonly warmupRatio: number;
	readonly epochs: number;
	readonly seed: number;
	readonly optimizer: string;
}

export interface TrainingRunProgressView {
	readonly phase: string;
	readonly step: number;
	readonly totalSteps: number;
	readonly epoch: number | null;
	readonly loss: number | null;
	readonly learningRate: number | null;
	readonly vramBytes: number | null;
}

export interface TrainingRunView {
	readonly id: string;
	readonly datasetId: string;
	readonly baseArtifactId: string;
	readonly status: TrainingRunStatusValue;
	readonly datasetRevision: number;
	readonly datasetContentFingerprint: string;
	readonly errorMessage: string | null;
	readonly logTail: string | null;
	readonly progress: TrainingRunProgressView | null;
	readonly options: TrainingRunOptionsView | null;
	readonly version: number;
	readonly updatedAtUtc: number;
}

export interface TrainingRunFootprintView {
	readonly gpuBytes: number;
	readonly ramBytes: number;
	readonly parameterCount: number;
	readonly trainableParameterCount: number;
	readonly experimental: boolean;
}

export interface TrainingRunLicenseView {
	readonly repoId: string;
	readonly license: string | null;
	readonly isGated: boolean;
	/** False when no license metadata was found at all — still confirmable, just a different statement. */
	readonly metadataPresent: boolean;
	readonly confirmationText: string;
}

export interface TrainingRunDefaultsView {
	readonly options: TrainingRunOptionsView;
	readonly estimate: TrainingRunFootprintView;
	readonly availableVramBytes: number;
	readonly vramKnown: boolean;
	readonly fits: boolean;
	readonly rejectionReason: string | null;
	readonly license: TrainingRunLicenseView | null;
}

export const trainingRunEventSchema = z.object({
	runId: z.string(),
	sequence: z.number(),
	// "Export" rides the same stream as training progress: the run's own status never moves for an export, so this
	// is the only live signal that one is happening.
	kind: z.enum(["State", "Phase", "Progress", "Artifact", "Export", "Error"]),
	payload: z.object({
		state: z.string().nullish(),
		phase: z.string().nullish(),
		step: z.number().nullish(),
		totalSteps: z.number().nullish(),
		epoch: z.number().nullish(),
		loss: z.number().nullish(),
		learningRate: z.number().nullish(),
		vramBytes: z.number().nullish(),
		message: z.string().nullish(),
		runVersion: z.number().nullish(),
	}),
});

export const trainingRunReplayResetSchema = z.object({
	runId: z.string(),
	latestSequence: z.number(),
	runVersion: z.number(),
});

export type TrainingRunEvent = z.infer<typeof trainingRunEventSchema>;

/** Live progress for one run, folded from the hub stream. */
export interface TrainingRunLiveProgress {
	readonly status: TrainingRunStatusValue | null;
	readonly phase: string | null;
	readonly step: number;
	readonly totalSteps: number;
	readonly loss: number | null;
	readonly message: string | null;
}

export const emptyTrainingRunProgress: TrainingRunLiveProgress = {
	status: null,
	phase: null,
	step: 0,
	totalSteps: 0,
	loss: null,
	message: null,
};

function toRunOptions(dto: TrainingRunOptionsPayload): TrainingRunOptionsView {
	return {
		maxSeqLength: dto.maxSeqLength,
		loraR: dto.loraR,
		loraAlpha: dto.loraAlpha,
		loraDropout: dto.loraDropout,
		perDeviceTrainBatchSize: dto.perDeviceTrainBatchSize,
		gradientAccumulationSteps: dto.gradientAccumulationSteps,
		learningRate: dto.learningRate,
		warmupRatio: dto.warmupRatio,
		epochs: dto.epochs,
		seed: dto.seed,
		optimizer: dto.optimizer,
	};
}

export function toTrainingRunView(response: TrainingRunResponse): TrainingRunView {
	const progress = response.progress;
	return {
		id: response.id,
		datasetId: response.datasetId,
		baseArtifactId: response.baseArtifactId,
		status: response.status as TrainingRunStatusValue,
		datasetRevision: response.datasetRevision,
		datasetContentFingerprint: response.datasetContentFingerprint,
		errorMessage: response.errorMessage ?? null,
		logTail: response.logTail ?? null,
		progress:
			progress == null
				? null
				: {
						phase: progress.phase,
						step: progress.step,
						totalSteps: progress.totalSteps,
						epoch: progress.epoch ?? null,
						loss: progress.loss ?? null,
						learningRate: progress.learningRate ?? null,
						vramBytes: progress.vramBytes ?? null,
					},
		options: response.options == null ? null : toRunOptions(response.options),
		version: response.version,
		updatedAtUtc: response.updatedAtUtc,
	};
}

export function toTrainingRunDefaultsView(response: TrainingRunDefaultsResponse): TrainingRunDefaultsView {
	const license = response.license;
	return {
		options: toRunOptions(response.options),
		estimate: {
			gpuBytes: response.estimate.gpuBytes,
			ramBytes: response.estimate.ramBytes,
			parameterCount: response.estimate.parameterCount,
			trainableParameterCount: response.estimate.trainableParameterCount,
			experimental: response.estimate.experimental,
		},
		availableVramBytes: response.availableVramBytes,
		vramKnown: response.vramKnown,
		fits: response.fits,
		rejectionReason: response.rejectionReason ?? null,
		license:
			license == null
				? null
				: {
						repoId: license.repoId,
						license: license.license ?? null,
						isGated: license.isGated,
						metadataPresent: license.metadataPresent,
						confirmationText: license.confirmationText,
					},
	};
}

/** Folds one hub event into the running progress view. */
export function applyRunEvent(current: TrainingRunLiveProgress, event: TrainingRunEvent): TrainingRunLiveProgress {
	const next = { ...current };
	if (event.payload.state != null) {
		next.status = event.payload.state as TrainingRunStatusValue;
	}
	if (event.payload.phase != null) {
		next.phase = event.payload.phase;
	}
	if (event.payload.step != null) {
		next.step = event.payload.step;
	}
	if (event.payload.totalSteps != null) {
		next.totalSteps = event.payload.totalSteps;
	}
	if (event.payload.loss != null) {
		next.loss = event.payload.loss;
	}
	if (event.payload.message != null) {
		next.message = event.payload.message;
	}
	return next;
}

/** True while the run still owns the GPU. A terminal run is done and its row stops polling. */
export function isRunActive(status: TrainingRunStatusValue): boolean {
	return status !== "Succeeded" && status !== "Failed" && status !== "Cancelled";
}
