import { describe, expect, it } from "vitest";

import { toLocalModelViewModel, toPullProgressModel } from "@/features/models/models/LocalModelMappers";

describe("local model mappers", () => {
	it("maps generated local model items to display labels", () => {
		expect(
			toLocalModelViewModel({
				modelName: "llama3:8b",
				sizeBytes: 1_073_741_824,
				modifiedAtUtc: Date.UTC(2026, 4, 24),
				family: "llama",
				parameterSize: "8B",
				quantizationLevel: "Q4_0",
				isSelected: true,
				kind: "Chat",
				detectedKind: "Chat",
				capabilities: ["completion", "tools"],
				isOverridden: false,
			}),
		).toEqual({
			modelName: "llama3:8b",
			sizeLabel: "1.0 GB",
			modifiedDateLabel: "2026-05-24",
			familyLabel: "llama",
			parameterSizeLabel: "8B",
			quantizationLabel: "Q4_0",
			isSelected: true,
			kind: "Chat",
			detectedKind: "Chat",
			capabilities: ["completion", "tools"],
			isOverridden: false,
		});
	});

	it("falls back to em-dash labels and Unknown kind for an empty generated item", () => {
		expect(toLocalModelViewModel({})).toEqual({
			modelName: "",
			sizeLabel: "—",
			modifiedDateLabel: "—",
			familyLabel: "—",
			parameterSizeLabel: "—",
			quantizationLabel: "—",
			isSelected: false,
			kind: "Unknown",
			detectedKind: "Unknown",
			capabilities: [],
			isOverridden: false,
		});
	});

	it("maps pull progress and clamps percentages", () => {
		expect(toPullProgressModel({ modelName: "llama", status: "pulling", totalBytes: 100, completedBytes: 150 })).toEqual({
			status: "pulling",
			progressPercent: 100,
		});
		expect(toPullProgressModel({ modelName: "llama", status: "", totalBytes: null, completedBytes: null })).toEqual({
			status: "Complete",
			progressPercent: undefined,
		});
	});
});
