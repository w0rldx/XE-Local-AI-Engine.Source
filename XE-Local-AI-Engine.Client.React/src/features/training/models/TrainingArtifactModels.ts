import type { XeLocalAiEngineClientEndpointsTrainingExportsV1TrainingArtifactResponse as TrainingArtifactResponse } from "@/core/api/generated";

export type TrainingArtifactKindValue = "AdapterGguf" | "MergedGguf" | "HfAdapterDir";
export type TrainingArtifactSmokeStateValue = "Pending" | "Passed" | "Failed" | "Skipped";
export type TrainingArtifactQualityOutcomeValue = "Pending" | "Passed" | "Failed" | "Overridden";

/** Quantizations accepted by the training export endpoint, in their UI presentation order. */
export const trainingExportQuantizations = ["Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0", "F16"] as const;
export type TrainingExportQuantization = (typeof trainingExportQuantizations)[number];
export const defaultTrainingExportQuantization: TrainingExportQuantization = trainingExportQuantizations[0];

export interface TrainingArtifactView {
	readonly id: string;
	readonly runId: string;
	readonly kind: TrainingArtifactKindValue;
	readonly fileName: string;
	readonly sha256: string | null;
	readonly sizeBytes: number;
	readonly smokeState: TrainingArtifactSmokeStateValue;
	readonly smokeReason: string | null;
	/** The registry name once promoted; null while the artifact is still staged and inert. */
	readonly committedModelName: string | null;
	readonly qualityComparisonId: string | null;
	readonly qualityOutcome: TrainingArtifactQualityOutcomeValue;
	readonly discardedAtUtc: number | null;
	readonly discardReason: string | null;
	readonly discardCleanupPending: boolean;
	readonly version: number;
}

const artifactQualityOutcomes: readonly TrainingArtifactQualityOutcomeValue[] = ["Passed", "Failed", "Overridden"];

function toArtifactQualityOutcome(value: string | null | undefined): TrainingArtifactQualityOutcomeValue {
	return artifactQualityOutcomes.includes(value as TrainingArtifactQualityOutcomeValue)
		? (value as TrainingArtifactQualityOutcomeValue)
		: "Pending";
}

export function toTrainingArtifactView(response: TrainingArtifactResponse): TrainingArtifactView {
	return {
		id: response.id,
		runId: response.runId,
		kind: response.kind as TrainingArtifactKindValue,
		fileName: response.fileName,
		sha256: response.sha256 ?? null,
		sizeBytes: response.sizeBytes,
		smokeState: response.smokeState as TrainingArtifactSmokeStateValue,
		smokeReason: response.smokeReason ?? null,
		committedModelName: response.committedModelName ?? null,
		qualityComparisonId: response.qualityComparisonId ?? null,
		qualityOutcome: toArtifactQualityOutcome(response.qualityOutcome),
		discardedAtUtc: response.discardedAtUtc ?? null,
		discardReason: response.discardReason ?? null,
		discardCleanupPending: response.discardCleanupPending,
		version: response.version,
	};
}

/** The first characters of the digest — enough to tell two builds apart without a 64-character wall of hex. */
export function shortDigest(sha256: string | null): string | null {
	return sha256 == null || sha256.length < 12 ? sha256 : sha256.slice(0, 12);
}

/** Promotion remains an explicit action, but only after smoke + quality pass (or an audited override). */
export function canPromote(artifact: TrainingArtifactView): boolean {
	return (
		artifact.smokeState === "Passed" &&
		artifact.committedModelName == null &&
		artifact.discardedAtUtc == null &&
		(artifact.qualityOutcome === "Passed" || artifact.qualityOutcome === "Overridden")
	);
}

export function canValidateArtifact(artifact: TrainingArtifactView): boolean {
	return artifact.smokeState === "Passed" && artifact.committedModelName == null && artifact.discardedAtUtc == null;
}

export function canOverrideArtifactQuality(artifact: TrainingArtifactView): boolean {
	return (
		artifact.qualityOutcome === "Failed" &&
		artifact.qualityComparisonId != null &&
		artifact.committedModelName == null &&
		artifact.discardedAtUtc == null
	);
}

export function canDiscardArtifactQuality(artifact: TrainingArtifactView): boolean {
	return artifact.qualityComparisonId != null && artifact.committedModelName == null && artifact.discardedAtUtc == null;
}

export function canRetryArtifactDiscardCleanup(
	artifact: TrainingArtifactView,
): artifact is TrainingArtifactView & { readonly discardReason: string } {
	return artifact.discardedAtUtc != null && artifact.discardCleanupPending && artifact.discardReason != null;
}

/** The trainer's own adapter directory is an input to an export, never an export target itself. */
export function isExportedArtifact(artifact: TrainingArtifactView): boolean {
	return artifact.kind !== "HfAdapterDir";
}

// The phases the export pipeline ends on. Everything else it publishes means work is still in flight.
const terminalExportPhases = new Set(["ready", "smokeFailed", "skipped", "failed"]);

/** True while an export is still running, from its latest published phase. */
export function isExportRunning(phase: string | null): boolean {
	return phase != null && !terminalExportPhases.has(phase);
}

/** Percentage through the current epoch's steps, or null when the trainer has not reported a total yet. */
export function runPercent(step: number, totalSteps: number): number | null {
	if (totalSteps <= 0) {
		return null;
	}
	return Math.min(100, Math.round((step / totalSteps) * 100));
}
