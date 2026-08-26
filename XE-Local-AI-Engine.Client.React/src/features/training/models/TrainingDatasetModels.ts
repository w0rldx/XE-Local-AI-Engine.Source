import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsTrainingV1ToolMockResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse,
} from "@/core/api/generated";

// Domain types + wire→domain mappers for the training dataset surface. The mappers coalesce the generated
// all-optional DTO fields into required domain fields with safe defaults; they do NOT re-validate — response
// validation happens once through the generated zod validator at the query-hook layer.

export type TrainingSamplePart =
	| { readonly kind: "text"; readonly id: string; readonly sequence: number; readonly text: string }
	| {
			readonly kind: "tool";
			readonly id: string;
			readonly sequence: number;
			readonly name: string;
			readonly state: "received" | "failed";
			readonly args?: string;
			readonly result?: string;
	  };

export type DatasetStatus = "Generating" | "Ready" | "Failed";
export type SampleLabel = "Good" | "Bad";
export type SampleReviewState = "Pending" | "Approved" | "Rejected";
export type MockVerificationState = "Unverified" | "Verified" | "Rejected";
export type TeacherOutputMode = "Constrained" | "ValidateAfter";
export type DatasetWorkStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";

/** Mirrors the backend's DatasetDefinitionBodyV1 default. */
export const HOLDOUT_FRACTION_DEFAULT = 0.1;

export interface TrainingDefinition {
	id: string;
	name: string;
	teacherModelName: string;
	teacherOutputMode: TeacherOutputMode;
	systemInstructions: string;
	toolNames: string[];
	sampleKinds: { kind: string; count: number; label: SampleLabel }[];
	holdoutFraction: number;
	temperature: number;
	baseSeed: string | null;
	criticEnabled: boolean;
	criticModelName: string | null;
	definitionVersion: number;
	version: number;
	updatedAtUtc: number;
}

export interface TrainingDataset {
	id: string;
	definitionId: string;
	definitionVersion: number;
	name: string;
	status: DatasetStatus;
	revision: number;
	contentFingerprint: string | null;
	totalSampleCount: number;
	goodSampleCount: number;
	badSampleCount: number;
	rejectedSampleCount: number;
	duplicateSampleCount: number;
	workStatus: DatasetWorkStatus | null;
	workErrorMessage: string | null;
	version: number;
	updatedAtUtc: number;
}

export interface TrainingSampleValidationLayer {
	layer: string;
	passed: boolean;
	scoredBy: string;
	reason: string | null;
}

export interface TrainingSample {
	id: string;
	datasetId: string;
	sequence: number;
	kind: string;
	label: SampleLabel;
	reviewState: SampleReviewState;
	sourceHash: string;
	systemInstructions: string;
	parts: TrainingSamplePart[];
	validationPassed: boolean | null;
	validationLayers: TrainingSampleValidationLayer[];
}

export interface ToolMock {
	id: string;
	toolName: string;
	verificationState: MockVerificationState;
	enabled: boolean;
	ruleCount: number;
	findings: string[];
	version: number;
}

/** Live generation progress derived from the hub stream. */
export interface DatasetGenerationProgress {
	completed: number;
	total: number;
	rejected: number;
	state: DatasetStatus | null;
}

export const datasetGenerationEventSchema = z.object({
	datasetId: z.string(),
	sequence: z.number(),
	kind: z.enum(["State", "Progress", "SampleAdded", "Rejected"]),
	payload: z.object({
		state: z.string().nullish(),
		completed: z.number().nullish(),
		total: z.number().nullish(),
		kind: z.string().nullish(),
		label: z.string().nullish(),
		reason: z.string().nullish(),
	}),
});

export const datasetGenerationReplayResetSchema = z.object({
	datasetId: z.string(),
	latestSequence: z.number(),
	datasetVersion: z.number(),
});

export type DatasetGenerationEvent = z.infer<typeof datasetGenerationEventSchema>;

export function toTrainingDefinition(
	dto: XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse,
): TrainingDefinition {
	const body = dto.body ?? {};
	return {
		id: dto.id,
		name: dto.name,
		teacherModelName: body.teacherModelName ?? "",
		teacherOutputMode: (body.teacherOutputMode ?? "Constrained") as TeacherOutputMode,
		systemInstructions: body.systemInstructions ?? "",
		toolNames: (body.tools ?? []).map((tool) => tool.name ?? ""),
		sampleKinds: (body.sampleKinds ?? []).map((sampleKind) => ({
			kind: sampleKind.kind ?? "",
			count: sampleKind.count ?? 0,
			label: (sampleKind.label ?? "Good") as SampleLabel,
		})),
		holdoutFraction: body.holdoutFraction ?? HOLDOUT_FRACTION_DEFAULT,
		temperature: body.temperature ?? 0,
		baseSeed: body.baseSeed ?? null,
		criticEnabled: body.criticEnabled ?? false,
		criticModelName: body.criticModelName ?? null,
		definitionVersion: dto.definitionVersion,
		version: dto.version,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

export function toTrainingDataset(dto: XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse): TrainingDataset {
	return {
		id: dto.id,
		definitionId: dto.definitionId,
		definitionVersion: dto.definitionVersion,
		name: dto.name,
		status: dto.status as DatasetStatus,
		revision: dto.revision,
		contentFingerprint: dto.contentFingerprint ?? null,
		totalSampleCount: dto.totalSampleCount,
		goodSampleCount: dto.goodSampleCount,
		badSampleCount: dto.badSampleCount,
		rejectedSampleCount: dto.rejectedSampleCount,
		duplicateSampleCount: dto.duplicateSampleCount,
		workStatus: dto.workStatus ?? null,
		workErrorMessage: dto.workErrorMessage ?? null,
		version: dto.version,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

/** Generation is only interruptible while the work item is still queued or running. */
export function isDatasetGenerationCancellable(dataset: TrainingDataset): boolean {
	return dataset.workStatus === "Queued" || dataset.workStatus === "Running";
}

/**
 * How a frozen dataset fingerprint (carried by a training run or an evaluation) relates to the dataset as it stands
 * now. "edited" means the hold-out set the scores were computed against no longer matches the dataset, so the numbers
 * are not comparable with anything scored after the edit. `datasets` is undefined while the list is still loading —
 * that reads as "current", never as "deleted", so a slow query cannot flash a false warning.
 */
export type DatasetDriftState = "current" | "edited" | "deleted";

export function datasetDriftState(
	frozenFingerprint: string | null,
	datasetId: string,
	datasets: readonly TrainingDataset[] | undefined,
): DatasetDriftState {
	if (datasets === undefined) {
		return "current";
	}
	const dataset = datasets.find((candidate) => candidate.id === datasetId);
	if (dataset === undefined) {
		return "deleted";
	}
	if (frozenFingerprint == null || dataset.contentFingerprint == null) {
		return "current";
	}
	return frozenFingerprint === dataset.contentFingerprint ? "current" : "edited";
}

/**
 * Projects a persisted sample onto the chat `parts[]` shape so the existing `MessageParts` renderer draws it. Unknown
 * part kinds are skipped, matching `NodeChatMapper.mapParts`: a forward-compat backend addition never breaks rendering.
 */
export function toTrainingSample(dto: XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse): TrainingSample {
	const content = dto.content ?? {};
	const parts: TrainingSamplePart[] = [];
	for (const part of content.parts ?? []) {
		const id = `${dto.id}:${part.sequence ?? 0}`;
		if (part.kind === "tool") {
			parts.push({
				kind: "tool",
				id: part.toolCallId ?? id,
				sequence: part.sequence ?? 0,
				name: part.toolName ?? "",
				state: part.isError ? "failed" : "received",
				args: part.arguments ?? undefined,
				result: part.result ?? undefined,
			});
			continue;
		}
		if (part.kind === "user" || part.kind === "text") {
			parts.push({ kind: "text", id, sequence: part.sequence ?? 0, text: part.content ?? "" });
		}
	}

	return {
		id: dto.id,
		datasetId: dto.datasetId,
		sequence: dto.sequence,
		kind: dto.kind,
		label: dto.label as SampleLabel,
		reviewState: dto.reviewState as SampleReviewState,
		sourceHash: dto.sourceHash,
		systemInstructions: content.systemInstructions ?? "",
		parts: parts.sort((left, right) => left.sequence - right.sequence),
		validationPassed: dto.validation?.passed ?? null,
		validationLayers: (dto.validation?.layers ?? []).map((layer) => ({
			layer: layer.layer ?? "",
			passed: layer.passed ?? false,
			scoredBy: layer.scoredBy ?? "",
			reason: layer.reason ?? null,
		})),
	};
}

export function toToolMock(dto: XeLocalAiEngineClientEndpointsTrainingV1ToolMockResponse): ToolMock {
	return {
		id: dto.id,
		toolName: dto.toolName,
		verificationState: dto.verificationState as MockVerificationState,
		enabled: dto.enabled,
		ruleCount: (dto.body?.rules ?? []).length,
		findings: dto.verification?.findings ?? [],
		version: dto.version,
	};
}

/** Folds one hub event into the running progress view. */
export function applyGenerationEvent(
	current: DatasetGenerationProgress,
	event: DatasetGenerationEvent,
): DatasetGenerationProgress {
	const next = { ...current };
	if (event.payload.total != null) {
		next.total = event.payload.total;
	}
	if (event.payload.completed != null) {
		next.completed = event.payload.completed;
	}
	if (event.kind === "Rejected") {
		next.rejected = current.rejected + 1;
	}
	if (event.kind === "State" && event.payload.state) {
		next.state = event.payload.state as DatasetStatus;
	}

	return next;
}

// ---------------------------------------------------------------------------
// Runtime + base-artifact view models.
// ---------------------------------------------------------------------------

// Domain view-models for the training feature. The generated shapes mark nullable fields as optional-and-nullable;
// mapping them once here — at the query boundary — keeps `?? null` out of every component that reads them, and gives
// the UI a single stable shape to render even as the wire shape grows.
