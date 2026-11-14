import type { TFunction } from "i18next";

import { humanizeBytes } from "@/core/formatting/BytesFormatting";
import type { GgufAcquisitionStatus } from "@/core/model-downloads/models/GgufAcquisitionModels";
import type { RuntimeAcquisitionStatus } from "@/core/runtime-acquisition/queries/useRuntimeAcquisitionStatus";

// One normalized view over the long-running operations the node already reports on their own feeds (GGUF downloads
// and imports, the llama.cpp runtime acquisition, and the job sources in JobOperationModels). The feeds stay the
// source of truth; these rows are derived from them on every render and never stored.

export type OperationKind =
	| "gguf-download"
	| "gguf-import"
	| "runtime-acquisition"
	| "knowledge-indexing"
	| "training-run"
	| "dataset-build"
	| "image-job"
	| "scheduler-run"
	| "image-model-download"
	| "transcription-model-download";

/** `succeeded` rows exist only so the toast coordinator can see the transition; the Activity views drop them. */
export type OperationState = "running" | "succeeded" | "failed";

/** Known only when the source reported a percentage or byte counts with a total; never estimated. */
export type OperationProgress = { readonly kind: "known"; readonly pct: number } | { readonly kind: "indeterminate" };

/** Where the user acts on the operation, as router link options. */
export type OperationLink =
	| {
			readonly to:
				| "/models"
				| "/knowledge-base"
				| "/training"
				| "/training/datasets"
				| "/images"
				| "/scheduler"
				| "/transcription";
	  }
	| { readonly to: "/node-settings"; readonly search: { readonly section: "runtimes" } };

export interface Operation {
	/** Stable across the operation's lifetime, prefixed by kind. */
	readonly id: string;
	readonly kind: OperationKind;
	readonly label: string;
	readonly progress: OperationProgress;
	readonly state: OperationState;
	readonly detail?: string;
	readonly href: OperationLink;
	/** Present only where the source already has a retry action. */
	readonly retry?: () => void;
}

export interface ActiveOperation extends Operation {
	readonly state: "running" | "failed";
}

export const INDETERMINATE: OperationProgress = { kind: "indeterminate" };
const MODELS_LINK: OperationLink = { to: "/models" };
const RUNTIMES_LINK: OperationLink = { to: "/node-settings", search: { section: "runtimes" } };

const RUNTIME_ACQUISITION_OPERATION_ID = "runtime-acquisition:llamacpp";

export function knownProgress(pct: number): OperationProgress {
	return { kind: "known", pct: Math.min(100, Math.max(0, Math.round(pct))) };
}

export function byteLine(completedBytes: number | null | undefined, totalBytes: number | null | undefined, t: TFunction) {
	if (completedBytes == null) {
		return undefined;
	}
	return totalBytes != null && totalBytes > 0
		? t("operations.progress", "{{completed}} of {{total}}", {
				completed: humanizeBytes(completedBytes),
				total: humanizeBytes(totalBytes),
			})
		: t("operations.progressUnknownTotal", "{{completed}} downloaded", { completed: humanizeBytes(completedBytes) });
}

function ggufLabel(kind: OperationKind, state: OperationState, model: string, t: TFunction): string {
	if (kind === "gguf-import") {
		switch (state) {
			case "running":
				return t("operations.ggufImport.running", "Importing {{model}}", { model });
			case "succeeded":
				return t("operations.ggufImport.succeeded", "{{model}} is imported", { model });
			default:
				return t("operations.ggufImport.failed", "{{model}} could not be imported", { model });
		}
	}
	switch (state) {
		case "running":
			return t("operations.ggufDownload.running", "Downloading {{model}}", { model });
		case "succeeded":
			return t("operations.ggufDownload.succeeded", "{{model}} is downloaded", { model });
		default:
			return t("operations.ggufDownload.failed", "{{model}} could not be downloaded", { model });
	}
}

/** GGUF downloads and imports. A cancelled one is the user's own decision, so it is neither activity nor news. */
export function fromGgufAcquisitions(statuses: Iterable<GgufAcquisitionStatus>, t: TFunction): Operation[] {
	const operations: Operation[] = [];
	for (const status of statuses) {
		if (status.phase === "Cancelled") {
			continue;
		}
		const kind: OperationKind = status.operationKind === "Import" ? "gguf-import" : "gguf-download";
		const state: OperationState = status.phase === "Completed" ? "succeeded" : status.phase === "Failed" ? "failed" : "running";
		const detail =
			state === "failed"
				? (status.sanitizedMessage ?? undefined)
				: state === "running"
					? byteLine(status.completedBytes, status.totalBytes, t)
					: undefined;
		operations.push({
			id: `${kind}:${status.operationId}`,
			kind,
			label: ggufLabel(kind, state, status.modelName, t),
			progress: state === "running" && status.pct !== undefined ? knownProgress(status.pct) : INDETERMINATE,
			state,
			detail,
			href: MODELS_LINK,
		});
	}
	return operations;
}

// An unrecognized phase (a backend that grew one ahead of this client) gets no phase line rather than a raw enum name.
function runtimePhaseLine(phase: string, t: TFunction): string | undefined {
	switch (phase) {
		case "DetectingGpu":
			return t("operations.runtime.detectingGpu", "Detecting your graphics hardware…");
		case "Downloading":
			return t("operations.runtime.downloading", "Downloading the llama.cpp runtime…");
		case "Verifying":
			return t("operations.runtime.verifying", "Verifying the downloaded runtime…");
		case "Extracting":
			return t("operations.runtime.extracting", "Installing the runtime…");
		default:
			return undefined;
	}
}

/**
 * The llama.cpp runtime acquisition the host starts at boot. Idle is no operation at all. A failure stays a row until
 * the source leaves `Failed` (a retry starts a new acquisition), because on a first run it is the only explanation of
 * why chat does not work.
 */
export function fromRuntimeAcquisition(
	status: RuntimeAcquisitionStatus | undefined,
	t: TFunction,
	retry: (failed: RuntimeAcquisitionStatus) => void,
): Operation | undefined {
	if (status === undefined || status.phase === "Idle") {
		return undefined;
	}
	const base = { id: RUNTIME_ACQUISITION_OPERATION_ID, kind: "runtime-acquisition", href: RUNTIMES_LINK } as const;
	if (status.phase === "Completed") {
		return {
			...base,
			label: t("operations.runtime.succeeded", "The local AI runtime is installed"),
			progress: INDETERMINATE,
			state: "succeeded",
		};
	}
	if (status.phase === "Failed") {
		return {
			...base,
			label: t("operations.runtime.failed", "The local AI runtime could not be installed"),
			progress: INDETERMINATE,
			state: "failed",
			detail:
				status.sanitizedError ??
				t(
					"operations.runtime.failedFallback",
					"The llama.cpp runtime could not be downloaded. Check your internet connection and try again.",
				),
			retry: () => retry(status),
		};
	}
	const { completedBytes, totalBytes } = status;
	// The step counter only matters on the multi-archive path, where the bar otherwise runs 0 to 100 % twice.
	const step =
		status.stepCount > 1
			? t("operations.runtime.step", "Step {{index}} of {{count}}", { index: status.stepIndex, count: status.stepCount })
			: undefined;
	const detail = [runtimePhaseLine(status.phase, t), step, byteLine(completedBytes, totalBytes, t)].filter(Boolean).join(" · ");
	// Determinate only while bytes are moving AND the total is known; the total is absent until the headers land.
	const determinate = status.phase === "Downloading" && completedBytes != null && totalBytes != null && totalBytes > 0;
	return {
		...base,
		label: t("operations.runtime.running", "Setting up the local AI runtime"),
		progress: determinate ? knownProgress((completedBytes / totalBytes) * 100) : INDETERMINATE,
		state: "running",
		detail: detail.length > 0 ? detail : undefined,
	};
}

// A failed download or runtime install blocks what comes next, so it stays a row until its source moves on. A failed
// job is history: its alert and its own page report it, and an Activity row would never go away.
const FAILURE_STAYS_ACTIVE: ReadonlySet<OperationKind> = new Set<OperationKind>([
	"gguf-download",
	"gguf-import",
	"runtime-acquisition",
]);

export function isActiveOperation(operation: Operation): operation is ActiveOperation {
	return operation.state === "running" || (operation.state === "failed" && FAILURE_STAYS_ACTIVE.has(operation.kind));
}

export interface OperationTransitions {
	/** Operations seen running before and terminal now: each appears here exactly once per run. */
	readonly finished: readonly Operation[];
	/** The state map to pass to the next call. */
	readonly seen: ReadonlyMap<string, OperationState>;
}

/**
 * Terminal transitions between two snapshots. Only an observed running-to-terminal step counts, so a terminal state
 * on first sight (page load hydration, a remount, a reconnect replay of an already-terminal row) never counts. Ids
 * missing from the snapshot are forgotten, which bounds the map to what the sources still report.
 */
export function operationTransitions(
	previous: ReadonlyMap<string, OperationState>,
	operations: readonly Operation[],
): OperationTransitions {
	const finished: Operation[] = [];
	const seen = new Map<string, OperationState>();
	for (const operation of operations) {
		if (previous.get(operation.id) === "running" && operation.state !== "running") {
			finished.push(operation);
		}
		seen.set(operation.id, operation.state);
	}
	return { finished, seen };
}
