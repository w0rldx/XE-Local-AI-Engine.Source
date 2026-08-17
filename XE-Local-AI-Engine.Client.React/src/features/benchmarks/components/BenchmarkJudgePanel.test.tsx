// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkJudgePanel } from "@/features/benchmarks/components/BenchmarkJudgePanel";
import { benchmarkJudgeFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The judging is independent from the primary run, and the panel is where that independence is made legible: a judge
// failure must be visible without implying the primary failed, an unjudged run must not read like a judged one, and a
// score that can no longer be RANKED (older policy, different judge runtime) must still be shown as the real score it
// is — flagged, not hidden.

function renderPanel(judge = benchmarkJudgeFixture(), props: Record<string, unknown> = {}) {
	const onCancel = vi.fn();
	const onRejudge = vi.fn();
	const view = renderWithProviders(
		<BenchmarkJudgePanel judge={judge} canRejudge={true} onCancel={onCancel} onRejudge={onRejudge} {...props} />,
	);
	return { ...view, onCancel, onRejudge };
}

describe("BenchmarkJudgePanel", () => {
	afterEach(cleanup);

	it("renders the panel with the judging's own state badge", () => {
		renderPanel(benchmarkJudgeFixture({ state: "running" }));

		expect(screen.getByTestId("benchmark-judge-panel")).toBeTruthy();
		expect(screen.getByTestId("benchmark-judge-state").textContent).toBe("Judging");
	});

	it("shows the 0..100 score, the summary and every criterion with its rationale", () => {
		renderPanel(
			benchmarkJudgeFixture({
				state: "succeeded",
				score: 73,
				policyRevision: 2,
				policyCurrent: true,
				executionCurrent: true,
				summary: "Accurate but terse.",
				criteria: [
					{ id: "accuracy", score: 8, rationale: "Facts check out." },
					{ id: "clarity", score: 6, rationale: "Dense phrasing." },
				],
			}),
		);

		expect(screen.getByTestId("benchmark-judge-score").textContent).toBe("Judge score: 73 / 100");
		expect(screen.getByText("Accurate but terse.")).toBeTruthy();
		expect(screen.getByText("8 / 10")).toBeTruthy();
		expect(screen.getByText("Facts check out.")).toBeTruthy();
		expect(screen.getByText("Dense phrasing.")).toBeTruthy();
	});

	it("shows no score section while nothing has been scored", () => {
		renderPanel(benchmarkJudgeFixture({ state: "queued" }));

		expect(screen.queryByTestId("benchmark-judge-score")).toBeNull();
	});

	// A score produced under an older policy is still a score; the chip says it cannot be ranked.
	it("marks an outdated policy revision without hiding its score", () => {
		renderPanel(benchmarkJudgeFixture({ state: "succeeded", score: 40, policyRevision: 1, policyCurrent: false }));

		expect(screen.getByTestId("benchmark-judge-policy").textContent).toContain("outdated");
		expect(screen.getByTestId("benchmark-judge-score")).toBeTruthy();
	});

	it("does not mark a current policy as outdated", () => {
		renderPanel(
			benchmarkJudgeFixture({ state: "succeeded", score: 40, policyRevision: 2, policyCurrent: true, executionCurrent: true }),
		);

		expect(screen.getByTestId("benchmark-judge-policy").textContent).not.toContain("outdated");
		expect(screen.queryByTestId("benchmark-judge-runtime")).toBeNull();
	});

	// The two currency flags are separate facts: the policy can be current while the judge runtime has moved.
	it("flags a judge runtime that differs from the ranked cohort", () => {
		renderPanel(
			benchmarkJudgeFixture({ state: "succeeded", score: 55, policyRevision: 2, policyCurrent: true, executionCurrent: false }),
		);

		expect(screen.getByTestId("benchmark-judge-runtime")).toBeTruthy();
	});

	// A failed judge must surface its own error without touching the primary result — the two lifecycles are separate.
	it("surfaces the judge error only when the judging itself failed", () => {
		const failed = renderPanel(benchmarkJudgeFixture({ state: "failed", errorMessage: "Judge output was invalid." }));
		expect(screen.getByText("Judge output was invalid.")).toBeTruthy();
		failed.unmount();

		renderPanel(benchmarkJudgeFixture({ state: "succeeded", score: 10, errorMessage: "Judge output was invalid." }));

		expect(screen.queryByText("Judge output was invalid.")).toBeNull();
	});

	it("offers cancel while the judging runs and re-judge once it is over", () => {
		const running = renderPanel(benchmarkJudgeFixture({ state: "running" }));
		fireEvent.click(screen.getByRole("button", { name: "Cancel judge" }));
		expect(running.onCancel).toHaveBeenCalledOnce();
		expect(screen.queryByRole("button", { name: "Re-judge run" })).toBeNull();
		running.unmount();

		const done = renderPanel(benchmarkJudgeFixture({ state: "succeeded", score: 20 }));
		fireEvent.click(screen.getByRole("button", { name: "Re-judge run" }));

		expect(done.onRejudge).toHaveBeenCalledOnce();
	});

	// Only a succeeded primary has stored output to judge, so a re-judge must not be offered otherwise.
	it("hides re-judge when the run has no output to judge", () => {
		renderPanel(benchmarkJudgeFixture({ state: "none" }), { canRejudge: false });

		expect(screen.queryByRole("button", { name: "Re-judge run" })).toBeNull();
	});
});
