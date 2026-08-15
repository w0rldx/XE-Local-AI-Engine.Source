import { describe, expect, it } from "vitest";

import type {
	XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse,
	XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse,
} from "@/core/api/generated";
import type { DatasetGenerationProgress } from "@/features/training/models/TrainingModels";
import { applyGenerationEvent, HOLDOUT_FRACTION_DEFAULT, toTrainingDefinition, toTrainingSample } from "@/features/training/models/TrainingModels";

const emptyProgress: DatasetGenerationProgress = { completed: 0, total: 0, rejected: 0, state: null };

function sample(
	overrides: Partial<XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse> = {},
): XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse {
	return {
		id: "sample-1",
		datasetId: "dataset-1",
		sequence: 0,
		kind: "tool-call",
		label: "Good",
		reviewState: "Pending",
		provenance: "Generated",
		sourceHash: "abc",
		content: {
			systemInstructions: "You call tools.",
			parts: [
				{ kind: "user", sequence: 0, content: "read the readme" },
				{ kind: "tool", sequence: 1, toolCallId: "call-1", toolName: "read_file", arguments: '{"path":"README.md"}', result: "# Title" },
				{ kind: "text", sequence: 2, content: "Here it is." },
			],
		},
		createdAtUtc: 0,
		updatedAtUtc: 0,
		...overrides,
	} as XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse;
}

describe("toTrainingSample", () => {
	it("projects the persisted trajectory onto the chat parts shape in sequence order", () => {
		const mapped = toTrainingSample(sample());

		expect(mapped.parts.map((part) => part.kind)).toEqual(["text", "tool", "text"]);
		const toolPart = mapped.parts[1];
		expect(toolPart).toMatchObject({ kind: "tool", id: "call-1", name: "read_file", state: "received", result: "# Title" });
	});

	it("marks a failed tool part so the shared renderer shows it as an error", () => {
		const mapped = toTrainingSample(
			sample({
				content: { systemInstructions: "", parts: [{ kind: "tool", sequence: 0, toolName: "read_file", isError: true }] },
			} as Partial<XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse>),
		);

		expect(mapped.parts[0]).toMatchObject({ kind: "tool", state: "failed" });
	});

	it("skips an unknown part kind instead of breaking the render", () => {
		const mapped = toTrainingSample(
			sample({
				content: {
					systemInstructions: "",
					parts: [
						{ kind: "some-future-kind", sequence: 0, content: "?" },
						{ kind: "text", sequence: 1, content: "kept" },
					],
				},
			} as Partial<XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse>),
		);

		expect(mapped.parts).toHaveLength(1);
		expect(mapped.parts[0]).toMatchObject({ kind: "text", text: "kept" });
	});

	it("surfaces every failed validation layer with its provenance", () => {
		const mapped = toTrainingSample(
			sample({
				validation: {
					schemaVersion: 1,
					passed: false,
					layers: [
						{ layer: "record-schema", passed: true, scoredBy: "schema" },
						{ layer: "execution", passed: false, scoredBy: "execution:validation-only", reason: "no mock matched" },
					],
				},
			} as Partial<XeLocalAiEngineClientEndpointsTrainingV1TrainingSampleResponse>),
		);

		expect(mapped.validationPassed).toBe(false);
		expect(mapped.validationLayers.filter((layer) => !layer.passed)).toEqual([
			{ layer: "execution", passed: false, scoredBy: "execution:validation-only", reason: "no mock matched" },
		]);
	});
});

describe("toTrainingDefinition", () => {
	it("defaults a missing hold-out fraction to ten percent", () => {
		const mapped = toTrainingDefinition({
			id: "definition-1",
			name: "definition",
			kind: "ToolCalling",
			body: { teacherModelName: "teacher.gguf" },
			definitionVersion: 2,
			version: 3,
			createdAtUtc: 0,
			updatedAtUtc: 0,
		} as XeLocalAiEngineClientEndpointsTrainingV1TrainingDefinitionResponse);

		expect(mapped.holdoutFraction).toBe(HOLDOUT_FRACTION_DEFAULT);
		expect(mapped.teacherOutputMode).toBe("Constrained");
	});
});

describe("applyGenerationEvent", () => {
	it("tracks completion counts and counts rejections separately", () => {
		const afterSample = applyGenerationEvent(emptyProgress, {
			datasetId: "d",
			sequence: 1,
			kind: "SampleAdded",
			payload: { completed: 1, total: 4 },
		});
		const afterRejection = applyGenerationEvent(afterSample, {
			datasetId: "d",
			sequence: 2,
			kind: "Rejected",
			payload: { reason: "schema" },
		});

		expect(afterRejection).toEqual({ completed: 1, total: 4, rejected: 1, state: null });
	});

	it("records the terminal state from a State event", () => {
		const terminal = applyGenerationEvent(emptyProgress, { datasetId: "d", sequence: 1, kind: "State", payload: { state: "Ready" } });

		expect(terminal.state).toBe("Ready");
	});
});
