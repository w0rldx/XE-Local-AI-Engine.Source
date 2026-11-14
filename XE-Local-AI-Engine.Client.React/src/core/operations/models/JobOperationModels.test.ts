import { t } from "i18next";
import { describe, expect, it } from "vitest";

import type {
	XeLocalAiEngineClientEndpointsImagesV1ImageJobResponse as ImageJobResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDatasetResponse as TrainingDatasetResponse,
	XeLocalAiEngineClientEndpointsTrainingRunsV1TrainingRunResponse as TrainingRunResponse,
} from "@/core/api/generated";
import {
	applyKnowledgeDocumentStatus,
	applyModelDownloadEvent,
	applySchedulerRunEvent,
	fromDatasetBuilds,
	fromImageJobs,
	fromKnowledgeIndexing,
	fromModelDownloads,
	fromSchedulerRuns,
	fromTrainingRuns,
	type KnowledgeIndexingBatch,
	type ModelDownloads,
	modelRecommendationCheckTemplateId,
	removeKnowledgeDocument,
	type SchedulerRuns,
} from "@/core/operations/models/JobOperationModels";
import { isActiveOperation } from "@/core/operations/models/OperationModels";

const run = (status: string, errorMessage?: string) => ({ id: "run-1", status, errorMessage }) as TrainingRunResponse;
const dataset = (workStatus: string | null, workErrorMessage?: string) =>
	({ id: "ds-1", name: "Support", workStatus, workErrorMessage }) as TrainingDatasetResponse;
const imageJob = (status: string, sanitizedError?: string) => ({ id: "job-1", status, sanitizedError }) as ImageJobResponse;

describe("job sources map into operation rows", () => {
	it("maps a training run through its lifecycle, links the training page and drops a cancelled run", () => {
		expect(fromTrainingRuns([run("Training")], t)).toEqual([
			expect.objectContaining({ id: "training-run:run-1", kind: "training-run", state: "running", label: "Training a model" }),
		]);
		expect(fromTrainingRuns([run("Succeeded")], t)[0]).toMatchObject({ state: "succeeded", label: "Training finished" });
		expect(fromTrainingRuns([run("Failed", "Out of memory.")], t)[0]).toMatchObject({
			state: "failed",
			detail: "Out of memory.",
			href: { to: "/training" },
		});
		expect(fromTrainingRuns([run("Cancelled")], t)).toEqual([]);
	});

	it("maps a dataset build by its work status and skips a dataset that was never generated", () => {
		expect(fromDatasetBuilds([dataset("Running")], t)[0]).toMatchObject({
			id: "dataset-build:ds-1",
			state: "running",
			label: "Building dataset Support",
			href: { to: "/training/datasets" },
		});
		expect(fromDatasetBuilds([dataset("Failed", "Model unloaded.")], t)[0]).toMatchObject({
			state: "failed",
			label: "Dataset Support could not be built",
			detail: "Model unloaded.",
		});
		expect(fromDatasetBuilds([dataset(null), dataset("Cancelled")], t)).toEqual([]);
	});

	it("maps an image job, queued and generating alike, with its sanitized error on failure", () => {
		expect(fromImageJobs([imageJob("Queued")], t)[0]).toMatchObject({ state: "running", href: { to: "/images" } });
		expect(fromImageJobs([imageJob("Generating")], t)[0]).toMatchObject({ state: "running" });
		expect(fromImageJobs([imageJob("Succeeded")], t)[0]).toMatchObject({ state: "succeeded", label: "Your image is ready" });
		expect(fromImageJobs([imageJob("Failed", "Out of VRAM.")], t)[0]).toMatchObject({ state: "failed", detail: "Out of VRAM." });
	});

	it("keeps a failed download in Activity but never a failed job", () => {
		const failedJob = fromImageJobs([imageJob("Failed")], t)[0];
		expect(failedJob && isActiveOperation(failedJob)).toBe(false);
		const runningJob = fromImageJobs([imageJob("Generating")], t)[0];
		expect(runningJob && isActiveOperation(runningJob)).toBe(true);
	});
});

describe("knowledge-base indexing batch", () => {
	function fold(...pushes: [string, string][]): KnowledgeIndexingBatch | undefined {
		return pushes.reduce<KnowledgeIndexingBatch | undefined>(
			(batch, [documentId, status]) => applyKnowledgeDocumentStatus(batch, documentId, status),
			undefined,
		);
	}

	it("reports one running row with known progress while any document is still indexing", () => {
		const batch = fold(["a", "Pending"], ["b", "Embedding"], ["a", "Indexed"]);

		expect(fromKnowledgeIndexing(batch, t)).toMatchObject({
			id: "knowledge-indexing",
			state: "running",
			progress: { kind: "known", pct: 50 },
			detail: "1 of 2 done",
			href: { to: "/knowledge-base" },
		});
	});

	it("settles as succeeded when every document indexed, and as failed when any failed", () => {
		expect(fromKnowledgeIndexing(fold(["a", "Pending"], ["a", "Indexed"]), t)?.state).toBe("succeeded");
		expect(fromKnowledgeIndexing(fold(["a", "Pending"], ["b", "Pending"], ["a", "Indexed"], ["b", "Failed"]), t)).toMatchObject({
			state: "failed",
			detail: "1 of 2 failed",
		});
	});

	it("keeps one running row through a sequential import and reports its failure once the last document settles", () => {
		const pushes: [string, string][] = [
			["a", "Pending"],
			["b", "Pending"],
			["c", "Pending"],
			["a", "Extracting"],
			["a", "Indexed"],
			["b", "Extracting"],
			["b", "Failed"],
			["c", "Extracting"],
			["c", "Indexed"],
		];
		let batch: KnowledgeIndexingBatch | undefined;
		const states = pushes.map(([documentId, status]) => {
			batch = applyKnowledgeDocumentStatus(batch, documentId, status);
			return fromKnowledgeIndexing(batch, t)?.state;
		});

		expect(states).toEqual(pushes.map((_, index) => (index < pushes.length - 1 ? "running" : "failed")));
		expect(fromKnowledgeIndexing(batch, t)).toMatchObject({ detail: "1 of 3 failed" });
	});

	it("settles as up to date when the last unfinished document is deleted", () => {
		const open = fold(["a", "Pending"], ["b", "Pending"], ["a", "Indexed"]);
		expect(fromKnowledgeIndexing(open, t)?.state).toBe("running");

		expect(fromKnowledgeIndexing(removeKnowledgeDocument(open, "b"), t)).toMatchObject({
			state: "succeeded",
			label: "The knowledge base is up to date",
		});
		expect(removeKnowledgeDocument(open, "unknown")).toBe(open);
	});

	it("ignores a terminal push outside an open batch and starts a fresh batch on the next indexing push", () => {
		expect(fold(["a", "Indexed"])).toBeUndefined();
		const settled = fold(["a", "Pending"], ["a", "Indexed"]);
		expect(applyKnowledgeDocumentStatus(settled, "z", "Failed")).toBe(settled);

		const next = applyKnowledgeDocumentStatus(settled, "b", "Pending");
		expect([...(next?.keys() ?? [])]).toEqual(["b"]);
	});
});

describe("scheduler runs", () => {
	function fold(...events: [string, Record<string, unknown>][]): SchedulerRuns | undefined {
		return events.reduce<SchedulerRuns | undefined>(
			(runs, [name, payload]) => applySchedulerRunEvent(runs, name, payload),
			undefined,
		);
	}

	it("follows a run from start to failure and carries the failure message", () => {
		const runs = fold(
			["scheduler.runStarted", { runId: "r1" }],
			["scheduler.runFailed", { runId: "r1", errorMessage: "Agent missing." }],
		);

		expect(fromSchedulerRuns(runs, t)).toEqual([
			expect.objectContaining({ id: "scheduler-run:r1", state: "failed", detail: "Agent missing.", href: { to: "/scheduler" } }),
		]);
	});

	it("drops a cancelled run, a terminal event for an unseen run, and the model-recommendation refresh", () => {
		expect(fold(["scheduler.runStarted", { runId: "r1" }], ["scheduler.runCancelled", { runId: "r1" }])?.size).toBe(0);
		expect(fold(["scheduler.runCompleted", { runId: "r2" }])).toBeUndefined();
		expect(fold(["scheduler.runStarted", { runId: "r3", templateId: modelRecommendationCheckTemplateId }])).toBeUndefined();
		expect(fold(["scheduler.runStarted", { runId: 42 }])).toBeUndefined();
	});

	it("never shows a model-recommendation refresh, even through its progress events, which carry no templateId", () => {
		const runs = fold(
			["scheduler.runStarted", { runId: "rec", templateId: modelRecommendationCheckTemplateId }],
			["scheduler.runProgress", { runId: "rec", message: "Checking models", percent: 40 }],
			["scheduler.runCompleted", { runId: "rec", templateId: modelRecommendationCheckTemplateId }],
		);

		expect(fromSchedulerRuns(runs, t)).toEqual([]);
	});

	it("follows an ordinary run through start, progress and completion", () => {
		const runs = fold(
			["scheduler.runStarted", { runId: "r1" }],
			["scheduler.runProgress", { runId: "r1", percent: 50 }],
			["scheduler.runCompleted", { runId: "r1" }],
		);

		expect(fromSchedulerRuns(runs, t)).toEqual([expect.objectContaining({ id: "scheduler-run:r1", state: "succeeded" })]);
	});

	it("keeps at most twenty settled runs", () => {
		const events = Array.from({ length: 25 }, (_, index) => [
			["scheduler.runStarted", { runId: `r${index}` }],
			["scheduler.runCompleted", { runId: `r${index}` }],
		]).flat() as [string, Record<string, unknown>][];

		const runs = fold(...events);

		expect(runs?.size).toBe(20);
		expect(runs?.has("r24")).toBe(true);
		expect(runs?.has("r0")).toBe(false);
	});
});

describe("image and transcription model downloads", () => {
	function fold(nameKey: "modelName" | "modelId", ...payloads: Record<string, unknown>[]): ModelDownloads | undefined {
		return payloads.reduce<ModelDownloads | undefined>(
			(downloads, payload) => applyModelDownloadEvent(downloads, payload, nameKey),
			undefined,
		);
	}

	it("shows an image download's byte progress and links the images page", () => {
		const downloads = fold("modelName", { modelName: "sd-1.5", phase: "Running", completedBytes: 50, totalBytes: 200 });

		expect(fromModelDownloads(downloads, "image-model-download", t)).toEqual([
			expect.objectContaining({
				id: "image-model-download:sd-1.5",
				state: "running",
				label: "Downloading the image model sd-1.5",
				progress: { kind: "known", pct: 25 },
				href: { to: "/images" },
			}),
		]);
	});

	it("follows a Whisper download, spelled in lower case by its hub, to its failure", () => {
		const downloads = fold(
			"modelId",
			{ modelId: "base", phase: "running", completedBytes: null, totalBytes: null },
			{ modelId: "base", phase: "failed", sanitizedError: "Disk full." },
		);

		const [row] = fromModelDownloads(downloads, "transcription-model-download", t);
		expect(row).toMatchObject({ state: "failed", detail: "Disk full.", href: { to: "/transcription" } });
		expect(row && isActiveOperation(row)).toBe(false);
	});

	it("drops a cancelled download, a terminal push for an unseen one and a malformed push", () => {
		expect(
			fromModelDownloads(
				fold("modelName", { modelName: "a", phase: "Running" }, { modelName: "a", phase: "Cancelled" }),
				"image-model-download",
				t,
			),
		).toEqual([]);
		expect(fold("modelName", { modelName: "b", phase: "Completed" })).toBeUndefined();
		expect(fold("modelName", { modelName: 7, phase: "Running" })).toBeUndefined();
		expect(fold("modelId", { modelName: "c", phase: "running" })).toBeUndefined();
	});

	it("ignores a stale Running push that lands after Completed, and accepts a new start", () => {
		const finished = fold(
			"modelName",
			{ modelName: "a", phase: "Running", sequence: 1 },
			{ modelName: "a", phase: "Completed", sequence: 3 },
			// Fire-and-forget on the node: the progress push stamped 2 arrives last.
			{ modelName: "a", phase: "Running", completedBytes: 10, totalBytes: 20, sequence: 2 },
		);
		expect(fromModelDownloads(finished, "image-model-download", t)[0]).toMatchObject({ state: "succeeded" });

		const restarted = applyModelDownloadEvent(finished, { modelName: "a", phase: "Running", sequence: 4 }, "modelName");
		expect(fromModelDownloads(restarted, "image-model-download", t)[0]).toMatchObject({ state: "running" });
	});

	it("keeps an end that arrives before its download's Running push from being revived by it", () => {
		const downloads = fold(
			"modelName",
			// Fire-and-forget on the node: the Completed push stamped 3 overtakes the Running push stamped 2.
			{ modelName: "a", phase: "Completed", sequence: 3 },
			{ modelName: "a", phase: "Running", completedBytes: 10, totalBytes: 20, sequence: 2 },
		);

		expect(fromModelDownloads(downloads, "image-model-download", t)).toEqual([]);
		const restarted = applyModelDownloadEvent(downloads, { modelName: "a", phase: "Running", sequence: 4 }, "modelName");
		expect(fromModelDownloads(restarted, "image-model-download", t)[0]).toMatchObject({ state: "running" });
	});

	it("keeps a cancelled download hidden when a stale Running push lands after the cancel", () => {
		const cancelled = fold(
			"modelId",
			{ modelId: "base", phase: "running", sequence: 1 },
			{ modelId: "base", phase: "cancelled", sequence: 3 },
			{ modelId: "base", phase: "running", sequence: 2 },
		);
		expect(fromModelDownloads(cancelled, "transcription-model-download", t)).toEqual([]);
	});

	it("reports a finished download as succeeded so its alert can fire", () => {
		const downloads = fold("modelName", { modelName: "a", phase: "Running" }, { modelName: "a", phase: "Completed" });

		expect(fromModelDownloads(downloads, "image-model-download", t)[0]).toMatchObject({
			state: "succeeded",
			label: "The image model a is downloaded",
		});
	});
});
