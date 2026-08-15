import { z } from "zod";

import type {
	XeLocalAiEngineClientEndpointsTrainingV1ToolMockResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse,
	XeLocalAiEngineClientEndpointsTrainingBaseArtifactsV1BaseArtifactResponse as BaseArtifactResponse,
	XeLocalAiEngineClientEndpointsTrainingRuntimeV1TrainingRuntimePrerequisitesResponse as PrerequisitesResponse,
	XeLocalAiEngineClientEndpointsTrainingRuntimeV1TrainingRuntimeStatusResponse as RuntimeStatusResponse,
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

// ---------------------------------------------------------------------------
// Runtime + base-artifact view models (Slice 2 surface).
// ---------------------------------------------------------------------------


// Domain view-models for the training feature. The generated shapes mark nullable fields as optional-and-nullable;
// mapping them once here — at the query boundary — keeps `?? null` out of every component that reads them, and gives
// the UI a single stable shape to render even as the wire shape grows.

export interface TrainingRuntimePrerequisiteItemView {
	readonly key: string;
	readonly satisfied: boolean;
	readonly detail: string;
}

export interface TrainingRuntimePrerequisitesView {
	readonly canInstall: boolean;
	readonly items: readonly TrainingRuntimePrerequisiteItemView[];
}

export interface InstalledTrainingRuntimeView {
	readonly uvVersion: string;
	readonly pythonVersion: string;
	readonly contractVersion: number;
	readonly installedAtUtc: number;
	readonly torchVersion: string | null;
	readonly unslothVersion: string | null;
	readonly deviceName: string | null;
}

export interface TrainingRuntimeStatusView {
	readonly phase: string;
	readonly isRunning: boolean;
	readonly terminal: boolean;
	readonly logStartSequence: number;
	readonly logLines: readonly string[];
	readonly sanitizedError: string | null;
	readonly installed: InstalledTrainingRuntimeView | null;
}

export interface BaseArtifactFileView {
	readonly role: string;
	readonly fileName: string;
	readonly sizeBytes: number;
	readonly sha256: string | null;
}

export interface BaseArtifactLicenseView {
	readonly repoId: string;
	readonly license: string | null;
	readonly isGated: boolean;
	readonly fetchedAtUtc: number;
}

export interface BaseArtifactProgressView {
	readonly completedBytes: number;
	readonly totalBytes: number | null;
	readonly fileIndex: number;
	readonly fileCount: number;
}

export interface BaseArtifactView {
	readonly id: string;
	readonly repoId: string;
	readonly revision: string;
	readonly status: string;
	readonly totalBytes: number;
	readonly files: readonly BaseArtifactFileView[];
	readonly license: BaseArtifactLicenseView | null;
	readonly errorMessage: string | null;
	readonly progress: BaseArtifactProgressView | null;
}

export function toRuntimeStatusView(response: RuntimeStatusResponse): TrainingRuntimeStatusView {
	const installed = response.installed;
	return {
		phase: response.phase,
		isRunning: response.isRunning,
		terminal: response.terminal,
		logStartSequence: response.logStartSequence,
		logLines: response.logLines,
		sanitizedError: response.sanitizedError ?? null,
		installed:
			installed == null
				? null
				: {
						uvVersion: installed.uvVersion,
						pythonVersion: installed.pythonVersion,
						contractVersion: installed.contractVersion,
						installedAtUtc: installed.installedAtUtc,
						torchVersion: installed.torchVersion ?? null,
						unslothVersion: installed.unslothVersion ?? null,
						deviceName: installed.deviceName ?? null,
					},
	};
}

export function toPrerequisitesView(response: PrerequisitesResponse): TrainingRuntimePrerequisitesView {
	return {
		canInstall: response.canInstall,
		items: response.items.map((item) => ({
			key: item.key,
			satisfied: item.satisfied,
			detail: item.detail,
		})),
	};
}

export function toBaseArtifactView(response: BaseArtifactResponse): BaseArtifactView {
	const progress = response.progress;
	const license = response.license;
	return {
		id: response.id,
		repoId: response.repoId,
		revision: response.revision,
		status: response.status,
		totalBytes: response.totalBytes,
		files: response.files.map((file) => ({
			role: file.role,
			fileName: file.fileName,
			sizeBytes: file.sizeBytes,
			sha256: file.sha256 ?? null,
		})),
		license:
			license == null
				? null
				: {
						repoId: license.repoId,
						license: license.license ?? null,
						isGated: license.isGated,
						fetchedAtUtc: license.fetchedAtUtc,
					},
		errorMessage: response.errorMessage ?? null,
		progress:
			progress == null
				? null
				: {
						completedBytes: progress.completedBytes,
						totalBytes: progress.totalBytes ?? null,
						fileIndex: progress.fileIndex,
						fileCount: progress.fileCount,
					},
	};
}

export interface TrainingLogEntry {
	readonly sequence: number;
	readonly message: string;
}

export function trainingLogEntries(startSequence: number, logLines: readonly string[]): readonly TrainingLogEntry[] {
	return logLines.map((message, index) => ({ sequence: startSequence + index, message }));
}

const maxTrainingLogEntries = 2000;

/**
 * Merges log slices by sequence number. The install streams appended lines with a starting offset, so a reconnect
 * that replays an overlapping window must not duplicate them — keying on the sequence makes the merge idempotent.
 */
export function mergeTrainingLogs(...sources: readonly (readonly TrainingLogEntry[])[]): readonly TrainingLogEntry[] {
	const bySequence = new Map<number, string>();
	for (const source of sources) {
		for (const entry of source) {
			if (Number.isSafeInteger(entry.sequence) && entry.sequence >= 0) {
				bySequence.set(entry.sequence, entry.message);
			}
		}
	}
	const merged = [...bySequence].sort(([left], [right]) => left - right).map(([sequence, message]) => ({ sequence, message }));
	return merged.slice(Math.max(0, merged.length - maxTrainingLogEntries));
}

/** True while the runtime install is mid-flight — the phases between Idle/Ready/Failed. */
export function isRuntimeInstalling(phase: string): boolean {
	return phase === "AcquiringUv" || phase === "ProvisioningPython" || phase === "InstallingPackages" || phase === "Verifying";
}

export function isArtifactDownloading(status: string): boolean {
	return status === "Downloading";
}

/** Percentage complete, or null when the total is unknown — a bar that guesses is worse than one that admits it. */
export function downloadPercent(progress: BaseArtifactProgressView | null): number | null {
	if (progress == null || progress.totalBytes == null || progress.totalBytes <= 0) {
		return null;
	}
	return Math.min(100, Math.round((progress.completedBytes / progress.totalBytes) * 100));
}

export function formatBytes(bytes: number): string {
	if (bytes <= 0) {
		return "0 B";
	}
	const units = ["B", "KB", "MB", "GB", "TB"];
	const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
	const value = bytes / 1024 ** exponent;
	return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`;
}
