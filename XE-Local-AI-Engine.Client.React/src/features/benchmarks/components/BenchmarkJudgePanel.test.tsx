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

	// The live failure: a 96/100 verdict on an answer that stopped mid-sentence. The score is real and stays visible,
	// but the panel has to say what it graded, or the number reads as a finished answer scoring 96.
	it("warns that a truncated answer is what the verdict graded, and only then", () => {
		const verdict = benchmarkJudgeFixture({ state: "succeeded", score: 96, policyRevision: 2, policyCurrent: true });

		renderPanel(verdict, { primaryTruncated: true });
		const notice = screen.getByTestId("benchmark-judge-truncated-notice").textContent ?? "";
		expect(notice).toContain("cut off by the token budget");
		expect(screen.getByTestId("benchmark-judge-score").textContent).toContain("96");

		cleanup();
		renderPanel(verdict);
		expect(screen.queryByTestId("benchmark-judge-truncated-notice")).toBeNull();
	});

	// An LLM judge rewards a longer answer for being longer, so the length of what it graded belongs next to the number
	// it may have inflated. Absent length must render nothing rather than a "0 tokens" that reads like a measurement.
	it("shows the graded answer's token count beside the score, and only when it is known", () => {
		const verdict = benchmarkJudgeFixture({ state: "succeeded", score: 88, policyRevision: 2, policyCurrent: true });

		renderPanel(verdict, { outputTokens: 4200 });
		expect(screen.getByTestId("benchmark-judge-output-length").textContent).toContain("4200");

		cleanup();
		renderPanel(verdict);
		expect(screen.queryByTestId("benchmark-judge-output-length")).toBeNull();
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

// C4: a verifiable criterion's score was DECIDED server-side, not judged. The panel says which side produced it, so a
// 10 that came from a regex match is not read as a model's opinion that happened to agree with one.
describe("BenchmarkJudgePanel verifier evidence", () => {
	afterEach(cleanup);

	const judged = benchmarkJudgeFixture({
		state: "succeeded",
		score: 90,
		criteria: [
			{ id: "answer", score: 10, rationale: "Matched the expected value." },
			{ id: "tone", score: 7, rationale: "Reads a little brusque." },
		],
		verifiers: [{ id: "answer", kind: "exact", passed: true, detail: "The answer equalled '42' after trimming." }],
	});

	it("labels the criterion the node decided with the kind that decided it", () => {
		renderPanel(judged);

		expect(screen.getByTestId("benchmark-judge-verifier-answer").textContent).toBe("Exact answer");
		expect(screen.getByTestId("benchmark-judge-verifier-detail-answer").textContent).toContain("42");
	});

	it("leaves an LLM-judged criterion without a verifier badge", () => {
		renderPanel(judged);

		expect(screen.queryByTestId("benchmark-judge-verifier-tone")).toBeNull();
	});

	it("shows a failed verifier's evidence rather than only its score", () => {
		renderPanel(
			benchmarkJudgeFixture({
				state: "succeeded",
				score: 0,
				criteria: [{ id: "answer", score: 0, rationale: "" }],
				verifiers: [{ id: "answer", kind: "regex", passed: false, detail: "The answer did not match /^42$/." }],
			}),
		);

		expect(screen.getByTestId("benchmark-judge-verifier-detail-answer").textContent).toContain("did not match");
	});
});

// Pairwise produces no per-run rubric score at all — a verdict matrix and a fitted number instead. Rendering pointwise
// chrome for it would present a score shape the policy does not produce.
describe("BenchmarkJudgePanel judging mode", () => {
	afterEach(cleanup);

	it("renders the pointwise reading by default", () => {
		renderPanel(benchmarkJudgeFixture({ state: "succeeded", score: 88 }));

		expect(screen.getByTestId("benchmark-judge-panel")).toBeTruthy();
	});

	it("renders nothing for a pairwise policy, whose reading this panel does not have", () => {
		renderPanel(benchmarkJudgeFixture({ state: "succeeded", score: 88 }), { mode: "pairwise" });

		expect(screen.queryByTestId("benchmark-judge-panel")).toBeNull();
	});
});
