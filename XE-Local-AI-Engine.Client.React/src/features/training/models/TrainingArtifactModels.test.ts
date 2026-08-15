import { describe, expect, it } from "vitest";

import type { TrainingArtifactView, TrainingRunEvent } from "@/features/training/models/TrainingModels";
import {
	applyRunEvent,
	canPromote,
	emptyTrainingRunProgress,
	isExportRunning,
	isExportedArtifact,
	shortDigest,
	toTrainingArtifactView,
	trainingRunEventSchema,
} from "@/features/training/models/TrainingModels";

function artifact(overrides: Partial<TrainingArtifactView> = {}): TrainingArtifactView {
	return {
		id: "artifact-1",
		runId: "run-1",
		kind: "MergedGguf",
		fileName: "merged-Q4_K_M.gguf",
		sha256: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
		sizeBytes: 4096,
		smokeState: "Passed",
		smokeReason: null,
		committedModelName: null,
		version: 3,
		...overrides,
	};
}

describe("staged artifact view", () => {
	it("maps the wire shape's optional-and-nullable fields onto stable nulls", () => {
		const view = toTrainingArtifactView({
			id: "artifact-1",
			runId: "run-1",
			kind: "AdapterGguf",
			fileName: "adapter-F16.gguf",
			sizeBytes: 512,
			smokeState: "Pending",
			version: 1,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		});

		expect(view.sha256).toBeNull();
		expect(view.smokeReason).toBeNull();
		expect(view.committedModelName).toBeNull();
	});

	it("only offers promotion for a smoke-passed artifact that is not registered yet", () => {
		expect(canPromote(artifact())).toBe(true);
		expect(canPromote(artifact({ smokeState: "Failed" }))).toBe(false);
		expect(canPromote(artifact({ smokeState: "Skipped" }))).toBe(false);
		expect(canPromote(artifact({ smokeState: "Pending" }))).toBe(false);
		// Already in the registry: promoting again would be a second entry over the same bytes.
		expect(canPromote(artifact({ committedModelName: "tuned:Q4_K_M" }))).toBe(false);
	});

	it("hides the trainer's own adapter directory, which is an export INPUT rather than a result", () => {
		expect(isExportedArtifact(artifact({ kind: "HfAdapterDir" }))).toBe(false);
		expect(isExportedArtifact(artifact({ kind: "AdapterGguf" }))).toBe(true);
	});

	it("shortens a digest without inventing one when it is absent", () => {
		expect(shortDigest(artifact().sha256)).toBe("0123456789ab");
		expect(shortDigest(null)).toBeNull();
	});
});

describe("export phase tracking", () => {
	it("treats every pipeline phase as running until a terminal one arrives", () => {
		expect(isExportRunning("merging")).toBe(true);
		expect(isExportRunning("quantizing")).toBe(true);
		expect(isExportRunning("smoke")).toBe(true);
		expect(isExportRunning("ready")).toBe(false);
		expect(isExportRunning("failed")).toBe(false);
		expect(isExportRunning("skipped")).toBe(false);
		expect(isExportRunning("smokeFailed")).toBe(false);
		expect(isExportRunning(null)).toBe(false);
	});

	it("parses the Export event kind and folds its phase like any other", () => {
		const parsed = trainingRunEventSchema.safeParse({
			runId: "run-1",
			sequence: 1,
			kind: "Export",
			payload: { phase: "quantizing" },
		});

		expect(parsed.success).toBe(true);
		const folded = applyRunEvent(emptyTrainingRunProgress, parsed.data as TrainingRunEvent);
		expect(folded.phase).toBe("quantizing");
	});
});
