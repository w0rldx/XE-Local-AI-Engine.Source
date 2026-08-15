// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { BenchmarkJudgePanel } from "@/features/benchmarks/components/BenchmarkJudgePanel";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The judge lifecycle is independent from the primary run, and the panel is where that independence is made legible:
// Disabled ("never asked for") and Skipped ("asked for, but the primary did not succeed") must not read the same, a
// judge failure must be visible without implying the primary failed, and a score only appears once a result exists.

function run(overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return {
		id: "run-1",
		projectId: "project-1",
		primaryModelName: "model.gguf",
		primaryModelOrigin: null,
		modelContentFingerprint: "v1:test",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judgeStatus: "Disabled",
		effectiveContextTokens: 4096,
		durationMs: 1250,
		totalTokens: 30,
		tokensPerSecond: 24,
		userScore: null,
		lastStreamSequence: 2,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		outputParts: [],
		judgeResult: null,
		primaryErrorMessage: null,
		judgeErrorMessage: null,
		startedAtUtc: 1,
		primaryCompletedAtUtc: 2,
		judgeStartedAtUtc: null,
		judgeCompletedAtUtc: null,
		...overrides,
	};
}

describe("BenchmarkJudgePanel", () => {
	afterEach(cleanup);

	it("renders the panel with the judge status badge", () => {
		renderWithProviders(<BenchmarkJudgePanel run={run({ judgeStatus: "Running" })} />);

		expect(screen.getByTestId("benchmark-judge-panel")).toBeTruthy();
		expect(screen.getByText("Running")).toBeTruthy();
	});

	it("distinguishes a judge that was never requested from one that was skipped", () => {
		const disabled = renderWithProviders(<BenchmarkJudgePanel run={run({ judgeStatus: "Disabled" })} />);
		const disabledText = screen.getByTestId("benchmark-judge-panel").textContent ?? "";
		expect(disabledText).toMatch(/not requested/i);
		disabled.unmount();

		renderWithProviders(<BenchmarkJudgePanel run={run({ judgeStatus: "Skipped" })} />);

		const skippedText = screen.getByTestId("benchmark-judge-panel").textContent ?? "";
		expect(skippedText).toMatch(/requested but skipped/i);
		expect(skippedText).not.toBe(disabledText);
	});

	it("shows the judge score and rationale once a result exists", () => {
		renderWithProviders(
			<BenchmarkJudgePanel
				run={run({
					judgeStatus: "Succeeded",
					judgeResult: {
						schemaVersion: 1,
						score: 4,
						rationale: "Accurate but terse.",
						judgeModelContentFingerprint: "v1:judge",
						promptVersion: 1,
					},
				})}
			/>,
		);

		expect(screen.getByText("Judge score: 4")).toBeTruthy();
		expect(screen.getByText("Accurate but terse.")).toBeTruthy();
	});

	it("shows no score section while there is no judge result", () => {
		renderWithProviders(<BenchmarkJudgePanel run={run({ judgeStatus: "Pending" })} />);

		expect(screen.queryByText(/Judge score:/)).toBeNull();
	});

	// A failed judge must surface its own error without touching the primary result — the two lifecycles are separate.
	it("surfaces the judge error only when the judge itself failed", () => {
		const failed = renderWithProviders(
			<BenchmarkJudgePanel run={run({ judgeStatus: "Failed", judgeErrorMessage: "Judge output was invalid." })} />,
		);
		expect(screen.getByText("Judge output was invalid.")).toBeTruthy();
		failed.unmount();

		// The same message on a non-failed judge is not an error to show.
		renderWithProviders(
			<BenchmarkJudgePanel run={run({ judgeStatus: "Succeeded", judgeErrorMessage: "Judge output was invalid." })} />,
		);

		expect(screen.queryByText("Judge output was invalid.")).toBeNull();
	});
});
