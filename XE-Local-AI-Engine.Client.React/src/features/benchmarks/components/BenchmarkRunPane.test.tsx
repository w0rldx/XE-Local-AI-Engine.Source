// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRunPane } from "@/features/benchmarks/components/BenchmarkRunPane";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkJudgeFixture, benchmarkRunDetailFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

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
				},
			}),
		);

		expect(screen.getByTestId("benchmark-throughput-cached").textContent).toMatch(/not a cold prefill/i);
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
				},
			}),
		);

		expect(screen.queryByTestId("benchmark-throughput-breakdown")).toBeNull();
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
});
