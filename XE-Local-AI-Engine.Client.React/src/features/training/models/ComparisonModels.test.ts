import { describe, expect, it } from "vitest";

import {
	formatAccuracy,
	formatDelta,
	isEvaluationActive,
	isEvaluationUsable,
	toComparisonReport,
	toEvaluationRun,
} from "@/features/training/models/ComparisonModels";

describe("ComparisonModels", () => {
	it("coalesces the evaluation DTO's optional fields into required domain fields", () => {
		const run = toEvaluationRun({
			id: "e1",
			modelName: "base-model",
			datasetId: "d1",
			status: "Running",
			totalCount: 10,
			scoredCount: 4,
			passedCount: 3,
			perKind: [{ kind: "tool-call", total: 4, passed: 3 }],
			version: 2,
			createdAtUtc: 1,
			updatedAtUtc: 2,
		});

		expect(run.trainingRunId).toBeNull();
		expect(run.comparisonId).toBeNull();
		expect(run.errorMessage).toBeNull();
		expect(run.perKind).toEqual([{ kind: "tool-call", total: 4, passed: 3 }]);
	});

	it("degrades an unrecognized status to Queued rather than throwing", () => {
		// A wire value this build does not know is not worth a blank page over.
		expect(toEvaluationRun({ id: "e1", modelName: "m", datasetId: "d", status: "Reticulating", totalCount: 1, scoredCount: 0, passedCount: 0, perKind: [], version: 1, createdAtUtc: 1, updatedAtUtc: 1 }).status).toBe(
			"Queued",
		);
	});

	it("keeps an unreadable deltas document as null instead of a fabricated zero report", () => {
		const report = toComparisonReport({
			id: "c1",
			name: "base vs tuned",
			baseEvaluationRunId: "e1",
			tunedEvaluationRunId: "e2",
			version: 1,
			createdAtUtc: 1,
			updatedAtUtc: 1,
		});

		expect(report.deltas).toBeNull();
		expect(report.baseBenchmarkRunId).toBeNull();
	});

	it("reports a dash rather than 0% when nothing was scored", () => {
		expect(formatAccuracy(0, 0)).toBe("—");
		expect(formatAccuracy(0.75, 4)).toBe("75.0%");
	});

	it("formats deltas as signed percentage points", () => {
		expect(formatDelta(0.125)).toBe("+12.5pp");
		expect(formatDelta(-0.5)).toBe("-50.0pp");
	});

	it("treats only queued and running evaluations as active, and only scored ones as usable", () => {
		expect(isEvaluationActive("Queued")).toBe(true);
		expect(isEvaluationActive("Running")).toBe(true);
		expect(isEvaluationActive("Succeeded")).toBe(false);

		const succeeded = { status: "Succeeded" as const, scoredCount: 3 };
		expect(isEvaluationUsable({ ...base, ...succeeded })).toBe(true);
		// A "succeeded" evaluation that scored nothing is not a comparison input.
		expect(isEvaluationUsable({ ...base, ...succeeded, scoredCount: 0 })).toBe(false);
		expect(isEvaluationUsable({ ...base, status: "Failed", scoredCount: 3 })).toBe(false);
		expect(isEvaluationUsable(null)).toBe(false);
	});
});

const base = {
	id: "e1",
	trainingRunId: null,
	comparisonId: null,
	modelName: "m",
	status: "Succeeded" as const,
	totalCount: 3,
	scoredCount: 3,
	passedCount: 3,
	perKind: [],
	errorMessage: null,
	version: 1,
};
