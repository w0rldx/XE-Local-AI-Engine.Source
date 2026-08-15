import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsTrainingV1ToolMockResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse,
} from "@/core/api/generated";
import type { ChatMessagePart } from "@/features/chat/models/ChatModels";

// Domain types + wire→domain mappers for the training dataset surface. The mappers coalesce the generated
// all-optional DTO fields into required domain fields with safe defaults; they do NOT re-validate — response
// validation happens once through the generated zod validator at the query-hook layer.

export type DatasetStatus = "Generating" | "Ready" | "Failed";
export type SampleLabel = "Good" | "Bad";
export type SampleReviewState = "Pending" | "Approved" | "Rejected";
export type MockVerificationState = "Unverified" | "Verified" | "Rejected";
export type TeacherOutputMode = "Constrained" | "ValidateAfter";

/** Mirrors the backend's DatasetDefinitionBodyV1 default (plan decision #17). */
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
	criticEnabled: boolean;
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
	parts: ChatMessagePart[];
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

export function toTrainingDefinition(dto: XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse): TrainingDefinition {
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
		criticEnabled: body.criticEnabled ?? false,
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
		workErrorMessage: dto.workErrorMessage ?? null,
		version: dto.version,
		updatedAtUtc: dto.updatedAtUtc,
	};
}

/**
 * Projects a persisted sample onto the chat `parts[]` shape so the existing `MessageParts` renderer draws it. Unknown
 * part kinds are skipped, matching `NodeChatMapper.mapParts`: a forward-compat backend addition never breaks rendering.
 */
export function toTrainingSample(dto: XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse): TrainingSample {
	const content = dto.content ?? {};
	const parts: ChatMessagePart[] = [];
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
export function applyGenerationEvent(current: DatasetGenerationProgress, event: DatasetGenerationEvent): DatasetGenerationProgress {
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
