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
