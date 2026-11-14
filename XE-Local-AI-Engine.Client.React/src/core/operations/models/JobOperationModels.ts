import type { TFunction } from "i18next";

import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageJobResponse as ImageJobResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse as TrainingDatasetResponse,
	XeLocalAiEngineClientEndpointsTrainingRunsV1TrainingRunResponse as TrainingRunResponse,
} from "@/core/api/generated";
import {
	byteLine,
	INDETERMINATE,
	knownProgress,
	type Operation,
	type OperationState,
} from "@/core/operations/models/OperationModels";

// The job sources of the operations feed. Training runs, dataset builds and image jobs are rows of the history lists
// their pages already read; knowledge-base indexing, scheduler runs and image/Whisper model downloads are push-fed maps
// (their hubs broadcast and there is no cheap list to hydrate from). Like the GGUF rows, a cancelled job is the user's
// own decision: no row.

/** The reserved scheduler template behind a model-recommendation refresh; its own page raises a richer toast. */
export const modelRecommendationCheckTemplateId = "model-recommendation-check";

// Every job source ends in Succeeded, Failed or Cancelled; anything else (Queued, Preparing, Generating, …) is work.
function jobState(status: string | null | undefined): OperationState | undefined {
	switch (status) {
		case "Succeeded":
			return "succeeded";
		case "Failed":
			return "failed";
		case "Cancelled":
		case null:
		case undefined:
			return undefined;
		default:
			return "running";
	}
}

function pick(state: OperationState, running: string, succeeded: string, failed: string): string {
	return state === "running" ? running : state === "succeeded" ? succeeded : failed;
}

export function fromTrainingRuns(runs: readonly TrainingRunResponse[], t: TFunction): Operation[] {
	return runs.flatMap((run) => {
		const state = jobState(run.status);
		return state === undefined
			? []
			: [
					{
						id: `training-run:${run.id}`,
						kind: "training-run",
						label: pick(
							state,
							t("operations.trainingRun.running", "Training a model"),
							t("operations.trainingRun.succeeded", "Training finished"),
							t("operations.trainingRun.failed", "Training failed"),
						),
						progress: INDETERMINATE,
						state,
						detail: state === "failed" ? (run.errorMessage ?? undefined) : undefined,
						href: { to: "/training" },
					} satisfies Operation,
				];
	});
}

// A dataset that was never generated (imported, or built before work tracking) has no work status: no row.
export function fromDatasetBuilds(datasets: readonly TrainingDatasetResponse[], t: TFunction): Operation[] {
	return datasets.flatMap((dataset) => {
		const state = jobState(dataset.workStatus);
		const name = dataset.name;
		return state === undefined
			? []
			: [
					{
						id: `dataset-build:${dataset.id}`,
						kind: "dataset-build",
						label: pick(
							state,
							t("operations.datasetBuild.running", "Building dataset {{name}}", { name }),
							t("operations.datasetBuild.succeeded", "Dataset {{name}} is ready", { name }),
							t("operations.datasetBuild.failed", "Dataset {{name}} could not be built", { name }),
						),
						progress: INDETERMINATE,
						state,
						detail: state === "failed" ? (dataset.workErrorMessage ?? undefined) : undefined,
						href: { to: "/training/datasets" },
					} satisfies Operation,
				];
	});
}

export function fromImageJobs(jobs: readonly ImageJobResponse[], t: TFunction): Operation[] {
	return jobs.flatMap((job) => {
		const state = jobState(job.status);
		return state === undefined
			? []
			: [
					{
						id: `image-job:${job.id}`,
						kind: "image-job",
						label: pick(
							state,
							t("operations.imageJob.running", "Generating an image"),
							t("operations.imageJob.succeeded", "Your image is ready"),
							t("operations.imageJob.failed", "The image could not be generated"),
						),
						progress: INDETERMINATE,
						state,
						detail: state === "failed" ? (job.sanitizedError ?? undefined) : undefined,
						href: { to: "/images" },
					} satisfies Operation,
				];
	});
}

/** Document id to its last pushed indexing status, for the current batch. */
export type KnowledgeIndexingBatch = ReadonlyMap<string, string>;

function isIndexing(status: string): boolean {
	return status !== "Indexed" && status !== "Failed";
}

/**
 * Folds one `knowledge.documentChanged` push into the batch. The first in-progress push after a settled batch opens a
 * new one; a terminal push outside an open batch is not news (an edit, a delete, an already-finished document) and
 * leaves the batch as it is. Returns the same map when nothing changed.
 */
export function applyKnowledgeDocumentStatus(
	batch: KnowledgeIndexingBatch | undefined,
	documentId: string,
	status: string,
): KnowledgeIndexingBatch | undefined {
	const open = batch !== undefined && [...batch.values()].some(isIndexing);
	if (!isIndexing(status) && !open) {
		return batch;
	}
	if (batch?.get(documentId) === status) {
		return batch;
	}
	const next = new Map(open ? batch : undefined);
	next.set(documentId, status);
	return next;
}

/** Drops a deleted document from an open batch, which settles if nothing else is unfinished; a settled batch is history. */
export function removeKnowledgeDocument(
	batch: KnowledgeIndexingBatch | undefined,
	documentId: string,
): KnowledgeIndexingBatch | undefined {
	if (!batch?.has(documentId) || ![...batch.values()].some(isIndexing)) {
		return batch;
	}
	const next = new Map(batch);
	next.delete(documentId);
	return next;
}

/** One row for the whole batch, so a repository import of hundreds of files alerts once. */
export function fromKnowledgeIndexing(batch: KnowledgeIndexingBatch | undefined, t: TFunction): Operation | undefined {
	if (batch === undefined || batch.size === 0) {
		return undefined;
	}
	const statuses = [...batch.values()];
	const done = statuses.filter((status) => !isIndexing(status)).length;
	const failed = statuses.filter((status) => status === "Failed").length;
	const count = statuses.length;
	const base = { id: "knowledge-indexing", kind: "knowledge-indexing", href: { to: "/knowledge-base" } } as const;
	if (done < count) {
		return {
			...base,
			label: t("operations.knowledgeIndexing.running", "Indexing documents for the knowledge base"),
			progress: knownProgress((done / count) * 100),
			state: "running",
			detail: t("operations.knowledgeIndexing.progress", "{{done}} of {{count}} done", { done, count }),
		};
	}
	return failed > 0
		? {
				...base,
				label: t("operations.knowledgeIndexing.failed", "Some documents could not be indexed"),
				progress: INDETERMINATE,
				state: "failed",
				detail: t("operations.knowledgeIndexing.failedDetail", "{{failed}} of {{count}} failed", { failed, count }),
			}
		: {
				...base,
				label: t("operations.knowledgeIndexing.succeeded", "The knowledge base is up to date"),
				progress: INDETERMINATE,
				state: "succeeded",
			};
}

export interface SchedulerRunEntry {
	readonly state: OperationState;
	readonly error?: string;
}

/** Run id to its state, from the scheduler hub's run events. */
export type SchedulerRuns = ReadonlyMap<string, SchedulerRunEntry>;

const SCHEDULER_RUN_STATE: Readonly<Record<string, OperationState | "cancelled">> = {
	"scheduler.runStarted": "running",
	"scheduler.runProgress": "running",
	"scheduler.runCompleted": "succeeded",
	"scheduler.runFailed": "failed",
	"scheduler.runCancelled": "cancelled",
};

// Terminal entries kept for the coordinator to see; older ones are dropped so a long session stays bounded.
const MAX_SETTLED_SCHEDULER_RUNS = 20;

function readString(payload: unknown, key: "runId" | "templateId" | "errorMessage"): string | undefined {
	const value = typeof payload === "object" && payload !== null ? (payload as Record<string, unknown>)[key] : undefined;
	return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

/**
 * Folds one scheduler run event (untrusted wire data, narrowed field by field) into the map. Only a start begins tracking
 * a run; a progress or terminal event for a run this map never saw start is dropped, which also keeps the map bounded to
 * what this page watched.
 */
export function applySchedulerRunEvent(
	runs: SchedulerRuns | undefined,
	eventName: string,
	payload: unknown,
): SchedulerRuns | undefined {
	const state = SCHEDULER_RUN_STATE[eventName];
	const runId = readString(payload, "runId");
	if (state === undefined || runId === undefined || readString(payload, "templateId") === modelRecommendationCheckTemplateId) {
		return runs;
	}
	// Only a start begins tracking a run: progress messages carry no templateId, so a progress event for an untracked run
	// may belong to an excluded template whose start was dropped, and its excluded completion would never clear it.
	const known = runs?.has(runId) === true;
	if (!known && eventName !== "scheduler.runStarted") {
		return runs;
	}
	if (state === "running" && runs?.get(runId)?.state === "running") {
		return runs;
	}
	const next = new Map(runs);
	if (state === "cancelled") {
		next.delete(runId);
		return next;
	}
	next.set(runId, { state, error: state === "failed" ? readString(payload, "errorMessage") : undefined });
	const settled = [...next].filter(([, entry]) => entry.state !== "running");
	for (const [id] of settled.slice(0, Math.max(0, settled.length - MAX_SETTLED_SCHEDULER_RUNS))) {
		next.delete(id);
	}
	return next;
}

/** One image or Whisper model download, as its hub last pushed it. */
export interface ModelDownloadEntry {
	/** A cancelled download, or an end never seen running, keeps its entry (and sequence) so a late push cannot revive it, but shows no row. */
	readonly state: OperationState | "cancelled";
	readonly completedBytes?: number;
	readonly totalBytes?: number;
	readonly error?: string;
	/** The node's write order for this status; a push with a lower one is stale. */
	readonly sequence?: number;
}

/** Model name (images) or catalog id (transcription) to its last pushed status. */
export type ModelDownloads = ReadonlyMap<string, ModelDownloadEntry>;

const MAX_SETTLED_MODEL_DOWNLOADS = 20;

function readNumber(payload: Record<string, unknown>, key: string): number | undefined {
	const value = payload[key];
	return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

/**
 * Folds one download push (untrusted wire data) into the map. The images hub spells the phase `Running`, the
 * transcription hub `running`. The pushes are fire-and-forget on the node, so one can land after a newer one: a push whose
 * `sequence` is not above the applied one is dropped (a new start always carries a higher one). A cancelled download
 * shows no row; a terminal push for a download this map never saw running is not news and shows no row either, kept
 * only (when sequenced) to block an older Running push, and settled entries are capped so the map stays bounded.
 */
export function applyModelDownloadEvent(
	downloads: ModelDownloads | undefined,
	payload: unknown,
	nameKey: "modelName" | "modelId",
): ModelDownloads | undefined {
	const record = typeof payload === "object" && payload !== null ? (payload as Record<string, unknown>) : {};
	const name = record[nameKey];
	const rawPhase = record["phase"];
	const phase = typeof rawPhase === "string" ? rawPhase.toLowerCase() : undefined;
	if (typeof name !== "string" || name.length === 0 || phase === undefined) {
		return downloads;
	}
	const current = downloads?.get(name);
	const sequence = readNumber(record, "sequence");
	if (current?.sequence !== undefined && sequence !== undefined && sequence <= current.sequence) {
		return downloads;
	}
	const known = current !== undefined && current.state !== "cancelled";
	const state: ModelDownloadEntry["state"] | undefined =
		phase === "running"
			? "running"
			: phase === "completed"
				? "succeeded"
				: phase === "failed"
					? "failed"
					: phase === "cancelled"
						? "cancelled"
						: undefined;
	if (state === undefined) {
		return downloads;
	}
	// An end for a download this map never saw running is not news, but a sequenced one is kept hidden (like a cancel)
	// so an older Running push that lands after it cannot revive the download.
	const unseenEnd = state !== "running" && !known;
	if (unseenEnd && sequence === undefined) {
		return downloads;
	}
	const error = record["sanitizedError"];
	const next = new Map(downloads);
	next.set(name, {
		state: unseenEnd ? "cancelled" : state,
		completedBytes: readNumber(record, "completedBytes"),
		totalBytes: readNumber(record, "totalBytes"),
		error: !unseenEnd && state === "failed" && typeof error === "string" && error.trim().length > 0 ? error : undefined,
		sequence,
	});
	const settled = [...next].filter(([, entry]) => entry.state !== "running");
	for (const [id] of settled.slice(0, Math.max(0, settled.length - MAX_SETTLED_MODEL_DOWNLOADS))) {
		next.delete(id);
	}
	return next;
}

export function fromModelDownloads(
	downloads: ModelDownloads | undefined,
	kind: "image-model-download" | "transcription-model-download",
	t: TFunction,
): Operation[] {
	const images = kind === "image-model-download";
	const shown = [...(downloads ?? [])].flatMap(([model, entry]) =>
		entry.state === "cancelled" ? [] : [[model, { ...entry, state: entry.state }] as const],
	);
	return shown.map(([model, entry]) => {
		const { completedBytes, totalBytes } = entry;
		const known = entry.state === "running" && completedBytes !== undefined && totalBytes !== undefined && totalBytes > 0;
		return {
			id: `${kind}:${model}`,
			kind,
			label: images
				? pick(
						entry.state,
						t("operations.imageModelDownload.running", "Downloading the image model {{model}}", { model }),
						t("operations.imageModelDownload.succeeded", "The image model {{model}} is downloaded", { model }),
						t("operations.imageModelDownload.failed", "The image model {{model}} could not be downloaded", { model }),
					)
				: pick(
						entry.state,
						t("operations.transcriptionModelDownload.running", "Downloading the transcription model {{model}}", { model }),
						t("operations.transcriptionModelDownload.succeeded", "The transcription model {{model}} is downloaded", {
							model,
						}),
						t("operations.transcriptionModelDownload.failed", "The transcription model {{model}} could not be downloaded", {
							model,
						}),
					),
			progress: known ? knownProgress((completedBytes / totalBytes) * 100) : INDETERMINATE,
			state: entry.state,
			detail:
				entry.state === "failed" ? entry.error : entry.state === "running" ? byteLine(completedBytes, totalBytes, t) : undefined,
			href: { to: images ? "/images" : "/transcription" },
		};
	});
}

export function fromSchedulerRuns(runs: SchedulerRuns | undefined, t: TFunction): Operation[] {
	return [...(runs ?? [])].map(([runId, entry]) => ({
		id: `scheduler-run:${runId}`,
		kind: "scheduler-run",
		label: pick(
			entry.state,
			t("operations.schedulerRun.running", "Running a scheduled task"),
			t("operations.schedulerRun.succeeded", "A scheduled task finished"),
			t("operations.schedulerRun.failed", "A scheduled task failed"),
		),
		progress: INDETERMINATE,
		state: entry.state,
		detail: entry.error,
		href: { to: "/scheduler" },
	}));
}
