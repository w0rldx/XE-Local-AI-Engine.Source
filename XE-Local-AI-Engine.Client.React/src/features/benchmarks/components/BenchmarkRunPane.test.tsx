// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkJudgeFixture, benchmarkRunDetailFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { createProvidersWrapper, renderWithProviders } from "@/test/RenderWithProviders";

afterEach(cleanup);

const run = (overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail =>
	benchmarkRunDetailFixture({ outputParts: [{ kind: "output", content: "Answer" }], ...overrides });

function renderPane(value: BenchmarkRunDetail) {
	const handlers = {
		onScore: vi.fn(),
		onClearScore: vi.fn(),
		onCancel: vi.fn(),
		onRejudge: vi.fn(),
		onDelete: vi.fn(),
		onDownloadArtifact: vi.fn(),
	};
	const view = renderWithProviders(
		<BenchmarkRunPane run={value} parts={value.outputParts} isConnected={false} isReconnecting={false} {...handlers} />,
	);
	return { ...view, ...handlers };
}

describe("BenchmarkRunPane", () => {
	it("renders persisted output, nullable provenance and metrics", () => {
		renderPane(run());

		expect(screen.getByText("Answer")).toBeTruthy();
		expect(screen.getByText("Legacy / Unknown")).toBeTruthy();
		expect(screen.getByText(/1.3s/)).toBeTruthy();
	});

	// The split behind the headline tok/s. It is display only — the pane must never imply it ranks anything.
	it("breaks the throughput down into time to first token, prompt and generation", () => {
		renderPane(run());

		expect(screen.getByTestId("benchmark-throughput-ttft").textContent).toContain("180 ms");
		expect(screen.getByTestId("benchmark-throughput-pp").textContent).toContain("640.0 tok/s");
		expect(screen.getByTestId("benchmark-throughput-tg").textContent).toContain("24.0 tok/s");
		// A cold prefill says nothing extra; only a cache hit needs explaining.
		expect(screen.queryByTestId("benchmark-throughput-cached")).toBeNull();
	});

	it("warns when the prompt speed came off the KV cache rather than a cold prefill", () => {
		renderPane(
			run({
				throughput: {
					ttftMs: 40,
					promptTokens: 512,
					promptTokensPerSecond: 9000,
					generationTokens: 30,
					generationTokensPerSecond: 24,
					cachedPromptTokens: 480,
					segmentCount: 3,
				},
			}),
		);

		expect(screen.getByTestId("benchmark-throughput-cached").textContent).toMatch(/not a cold prefill/i);
		// A multi-request turn must say so: otherwise a prompt count spanning three prefills reads as one.
		expect(screen.getByTestId("benchmark-throughput-segments").textContent).toContain("3 model requests");
	});

	it("says nothing about request count for a plain single-request turn", () => {
		renderPane(run());

		expect(screen.queryByTestId("benchmark-throughput-segments")).toBeNull();
	});

	it("hides the breakdown entirely for a runtime that reported no timings", () => {
		renderPane(
			run({
				throughput: {
					ttftMs: null,
					promptTokens: null,
					promptTokensPerSecond: null,
					generationTokens: null,
					generationTokensPerSecond: null,
					cachedPromptTokens: null,
					segmentCount: null,
				},
			}),
		);

		expect(screen.queryByTestId("benchmark-throughput-breakdown")).toBeNull();
	});

	// The length-bias counterweight must count the ANSWER, not the question: totalTokens includes the prompt, so a long
	// task would make every answer look long.
	it("gives the judge panel the decoded token count, not the turn total", () => {
		renderPane(run({ totalTokens: 5000, judge: benchmarkJudgeFixture({ state: "succeeded", score: 80 }) }));

		expect(screen.getByTestId("benchmark-judge-panel").textContent).toContain("30 output tokens");
		expect(screen.getByTestId("benchmark-judge-panel").textContent).not.toContain("5000");
	});

	it("explains an unjudged run rather than leaving the judge panel blank", () => {
		renderPane(run());

		expect(screen.getByTestId("benchmark-judge-panel").textContent).toMatch(/has not been judged/i);
	});

	// Scoring is only meaningful once there is a stored result to score.
	it("locks the score control until the primary succeeded", () => {
		const running = renderPane(run({ primaryStatus: "Running" }));
		expect((screen.getByTestId("benchmark-score-preset-100") as HTMLButtonElement).disabled).toBe(true);
		running.unmount();

		const succeeded = renderPane(run());
		fireEvent.click(screen.getByTestId("benchmark-score-preset-100"));

		expect(succeeded.onScore).toHaveBeenCalledExactlyOnceWith(100);
	});

	it("clears an existing override through its own action", () => {
		const { onClearScore, onScore } = renderPane(run({ userScore: 60 }));

		fireEvent.click(screen.getByTestId("benchmark-score-clear"));

		expect(onClearScore).toHaveBeenCalledOnce();
		expect(onScore).not.toHaveBeenCalled();
	});

	it("does not downgrade a successful primary when the judging fails", () => {
		renderPane(run({ judge: benchmarkJudgeFixture({ state: "failed", errorMessage: "Judge output was invalid." }) }));

		expect(screen.getByText("Succeeded")).toBeTruthy();
		expect(screen.getByText("Judge output was invalid.")).toBeTruthy();
	});

	it("routes the judge panel's cancel to the Judge phase, not the primary one", () => {
		const { onCancel } = renderPane(run({ judge: benchmarkJudgeFixture({ state: "running" }) }));

		fireEvent.click(screen.getByRole("button", { name: "Cancel judge" }));

		expect(onCancel).toHaveBeenCalledExactlyOnceWith("Judge");
	});

	// In throughput mode the seed is the proof the repeats were deterministic; in answer-variance mode it is the only
	// thing that says which of several differing answers this one is. Either way it is a fact of the run, not a detail.
	it("states what the run actually sampled with", () => {
		renderPane(run({ repeatMode: "AnswerVariance", samplingTemperature: 0.7, samplingSeed: "4242" }));

		const line = screen.getByTestId("benchmark-run-sampling").textContent ?? "";
		expect(line).toContain("Answer variance");
		expect(line).toContain("0.7");
		expect(line).toContain("4242");
	});

	// A budget the runtime never applied is exactly why a run can look as if the budget did nothing. Without the note
	// the operator's next move is to raise a number that was never read.
	it("says when the frozen reasoning budget was never applied", () => {
		renderPane(run({ reasoningBudgetTokens: 2048, reasoningBudgetApplicable: false }));

		expect(screen.getByTestId("benchmark-run-budget-not-applicable").textContent).toContain("no thinking mode");
	});

	it("shows the budget alone when the model honoured it", () => {
		renderPane(run({ reasoningBudgetTokens: 2048, reasoningBudgetApplicable: true }));

		expect(screen.getByTestId("benchmark-run-sampling").textContent).toContain("2048");
		expect(screen.queryByTestId("benchmark-run-budget-not-applicable")).toBeNull();
	});

	// Evidence is a download, never inline content: the bytes are the model's own code output.
	it("lists each verifier artifact and downloads it on request", () => {
		const artifact = {
			id: "art-1",
			kind: "pythonTestsStdout",
			criterionId: "tests",
			judgeAttemptId: "attempt-1",
			byteCount: 2048,
			contentHash: "sha256:abc",
		};
		const { onDownloadArtifact } = renderPane(run({ artifacts: [artifact] }));

		const row = screen.getByTestId("benchmark-artifact-art-1");
		expect(row.textContent).toContain("Python tests stdout");
		expect(row.textContent).toContain("tests");
		expect(row.textContent).toContain("2 KB");
		fireEvent.click(screen.getByTestId("benchmark-artifact-download-art-1"));

		expect(onDownloadArtifact).toHaveBeenCalledExactlyOnceWith(artifact);
	});

	it("renders no artifact list for a run without evidence", () => {
		renderPane(run());

		expect(screen.queryByTestId("benchmark-run-artifacts")).toBeNull();
	});

	it("shows each timed tool call, the rollup and a repeated identical call", () => {
		const parts = [
			{ kind: "tool_call", toolCallId: "c1", toolName: "search", arguments: '{"q":"x"}' },
			{ kind: "tool_result", toolCallId: "c1", result: "a", durationMs: 1234 },
			{ kind: "tool_call", toolCallId: "c2", toolName: "search", arguments: '{"q":"x"}' },
			{ kind: "tool_result", toolCallId: "c2", result: "a", durationMs: 900 },
		];
		renderPane(run({ outputParts: parts, toolCallCount: 2, toolTimeMs: 2134 }));

		expect(screen.getByTestId("benchmark-tool-rollup").textContent).toBe("2 tool calls · 2.1 s");
		expect(screen.getByTestId("benchmark-tool-duration-c1").textContent).toBe("1.2 s");
		expect(screen.queryByTestId("benchmark-tool-repeated-c1")).toBeNull();
		expect(screen.getByTestId("benchmark-tool-repeated-c2")).toBeTruthy();
	});

	// Not measured is not zero: a run the node did not time says nothing about tools at all.
	it("renders no tool timing when nothing was measured", () => {
		renderPane(
			run({
				outputParts: [
					{ kind: "tool_call", toolCallId: "c1", toolName: "search", arguments: "{}" },
					{ kind: "tool_result", toolCallId: "c1", result: "a" },
				],
			}),
		);

		expect(screen.queryByTestId("benchmark-tool-timing")).toBeNull();
	});

	// A review project's scoring run takes its score through the review; the node refuses a plain override there.
	it("replaces the score picker with the review form on a review-gated run", () => {
		const view = renderWithProviders(
			<BenchmarkRunPane
				run={run()}
				parts={[]}
				isConnected={false}
				isReconnecting={false}
				onScore={vi.fn()}
				onClearScore={vi.fn()}
				onCancel={vi.fn()}
				onRejudge={vi.fn()}
				onDelete={vi.fn()}
				review={{ criteria: null, isSaving: false, onSubmit: vi.fn() }}
			/>,
		);

		expect(screen.getByTestId("benchmark-review-form")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-score-save")).toBeNull();
		view.unmount();
	});

	// The pane stays open while the judging runs: the verdict arriving must pre-fill the form, but a later unrelated
	// update must not wipe what the reviewer typed.
	it("re-seeds the review form when the judging completes, and only then", () => {
		const criteria = [{ id: "accuracy", title: "Accuracy", description: "", weight: 100, kind: null }];
		const judging = (state: "running" | "succeeded") =>
			benchmarkJudgeFixture({
				state,
				attemptSequence: 1,
				policyCurrent: true,
				executionCurrent: true,
				criteria: state === "succeeded" ? [{ id: "accuracy", score: 6, rationale: "" }] : [],
			});
		const pane = (value: BenchmarkRunDetail) => (
			<BenchmarkRunPane
				run={value}
				parts={value.outputParts}
				isConnected={false}
				isReconnecting={false}
				onScore={vi.fn()}
				onClearScore={vi.fn()}
				onCancel={vi.fn()}
				onRejudge={vi.fn()}
				onDelete={vi.fn()}
				review={{ criteria, isSaving: false, onSubmit: vi.fn() }}
			/>
		);
		const accuracy = () => screen.getByTestId("benchmark-review-criterion-accuracy") as HTMLInputElement;
		const approve = () => screen.getByTestId("benchmark-review-approve") as HTMLButtonElement;
		// RTL `rerender` keeps a `wrapper`, unlike a tree renderWithProviders wrapped itself.
		const view = render(pane(run({ judge: judging("running") })), { wrapper: createProvidersWrapper().wrapper });
		expect(accuracy().value).toBe("");
		expect(approve().disabled).toBe(true);

		view.rerender(pane(run({ judge: judging("succeeded") })));
		expect(accuracy().value).toBe("6");
		expect(approve().disabled).toBe(false);

		fireEvent.change(accuracy(), { target: { value: "9" } });
		view.rerender(pane(run({ judge: judging("succeeded"), toolCallCount: 4, toolTimeMs: 900 })));
		expect(accuracy().value).toBe("9");
	});

	it.each([
		["judgeOnly", true],
		["loading", false],
	] as const)("renders no score control while it is %s", (scoreControl, note) => {
		renderWithProviders(
			<BenchmarkRunPane
				run={run()}
				parts={[]}
				isConnected={false}
				isReconnecting={false}
				onScore={vi.fn()}
				onClearScore={vi.fn()}
				onCancel={vi.fn()}
				onRejudge={vi.fn()}
				onDelete={vi.fn()}
				scoreControl={scoreControl}
			/>,
		);

		expect(screen.queryByTestId("benchmark-score-save")).toBeNull();
		expect(screen.queryByTestId("benchmark-review-form")).toBeNull();
		expect(screen.queryByTestId("benchmark-score-judge-only") !== null).toBe(note);
	});

	it("counts a single tool call in the singular", () => {
		renderPane(run({ toolCallCount: 1, toolTimeMs: 400 }));

		expect(screen.getByTestId("benchmark-tool-rollup").textContent).toBe("1 tool call · 0.4 s");
	});

	// The call id is model-supplied; a repeat must not collapse two rows (timing or transcript) into one React key.
	it("keeps two tool calls apart when the model reused a call id", () => {
		const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);
		const parts = [
			{ kind: "tool_call", toolCallId: "dup", toolName: "search", arguments: '{"q":"a"}' },
			{ kind: "tool_result", toolCallId: "dup", result: "a", durationMs: 100 },
			{ kind: "tool_call", toolCallId: "dup", toolName: "search", arguments: '{"q":"b"}' },
			{ kind: "tool_result", toolCallId: "dup", result: "b", durationMs: 200 },
		];
		renderPane(run({ outputParts: parts }));

		expect(screen.getAllByTestId("benchmark-tool-dup")).toHaveLength(2);
		expect(consoleError.mock.calls.some((call) => String(call[0]).includes("same key"))).toBe(false);
		consoleError.mockRestore();
	});
});
