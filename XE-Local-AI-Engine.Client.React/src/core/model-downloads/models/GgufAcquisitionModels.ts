import type {
	XeLocalAiEngineClientEndpointsModelFitV1GgufAcquisitionStatusResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GgufDownloadStatusResponse,
} from "@/core/api/generated";

export type GgufAcquisitionKind = "Download" | "Import";
export type GgufAcquisitionPhase =
	| "Queued"
	| "Validating"
	| "Copying"
	| "Downloading"
	| "Committing"
	| "Completed"
	| "Cancelled"
	| "Failed";

export interface GgufAcquisitionStatus {
	readonly operationId: string;
	readonly operationKind: GgufAcquisitionKind;
	readonly modelName: string;
	readonly phase: GgufAcquisitionPhase;
	readonly pct: number | undefined;
	readonly completedBytes: number | null | undefined;
	readonly totalBytes: number | null | undefined;
	readonly startedAtUtc: string | null | undefined;
	readonly updatedAtUtc: string | null | undefined;
	readonly errorCode: string | null | undefined;
	readonly sanitizedMessage: string | null | undefined;
	// A download's start-request options, so a retry replays them. `includeProjector` is null when the node does not
	// know the request (an import, or a row recorded after a restart): then none of the three can be replayed.
	readonly fileName?: string | null;
	readonly revision?: string | null;
	readonly includeProjector?: boolean | null;
}

type AcquisitionWireStatus =
	| XeLocalAiEngineClientEndpointsModelFitV1GgufAcquisitionStatusResponse
	| XeLocalAiEngineClientEndpointsModelFitV1GgufDownloadStatusResponse;

const phases = new Set<GgufAcquisitionPhase>([
	"Queued",
	"Validating",
	"Copying",
	"Downloading",
	"Committing",
	"Completed",
	"Cancelled",
	"Failed",
]);

export function toGgufAcquisitionStatus(raw: Partial<AcquisitionWireStatus>): GgufAcquisitionStatus | null {
	if (!raw.operationId || !raw.modelName || (raw.operationKind !== "Download" && raw.operationKind !== "Import")) {
		return null;
	}
	const phase =
		raw.phase === "Running"
			? raw.operationKind === "Import"
				? "Copying"
				: "Downloading"
			: phases.has(raw.phase as GgufAcquisitionPhase)
				? (raw.phase as GgufAcquisitionPhase)
				: "Failed";
	const pct = raw.totalBytes && raw.completedBytes != null ? Math.round((raw.completedBytes / raw.totalBytes) * 100) : undefined;
	return {
		operationId: raw.operationId,
		operationKind: raw.operationKind,
		modelName: raw.modelName,
		phase,
		pct,
		completedBytes: raw.completedBytes,
		totalBytes: raw.totalBytes,
		startedAtUtc: raw.startedAtUtc,
		updatedAtUtc: raw.updatedAtUtc,
		errorCode: raw.errorCode,
		sanitizedMessage: "sanitizedMessage" in raw ? raw.sanitizedMessage : "sanitizedError" in raw ? raw.sanitizedError : undefined,
		fileName: "fileName" in raw ? raw.fileName : undefined,
		revision: "revision" in raw ? raw.revision : undefined,
		includeProjector: "includeProjector" in raw ? raw.includeProjector : undefined,
	};
}

export function isTerminalAcquisitionPhase(phase: GgufAcquisitionPhase): boolean {
	return phase === "Completed" || phase === "Cancelled" || phase === "Failed";
}

export function isCancellableAcquisitionPhase(phase: GgufAcquisitionPhase): boolean {
	return !isTerminalAcquisitionPhase(phase);
}
