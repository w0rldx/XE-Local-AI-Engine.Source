// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkReviewForm } from "@/features/benchmarks/components/BenchmarkReviewForm";
import type { BenchmarkRubricCriterion, BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkJudgeFixture, benchmarkRunDetailFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

afterEach(cleanup);

const rubric: BenchmarkRubricCriterion[] = [
	{ id: "accuracy", title: "Accuracy", description: "Is it right?", weight: 60, kind: null },
	{ id: "tests", title: "Tests", description: "Do the tests pass?", weight: 40, kind: "pythonTests", config: "{}" },
];

/** A run whose judging succeeded under the current policy: the ordinary reviewable case. */
const judged = (overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail =>
	benchmarkRunDetailFixture({
		judge: benchmarkJudgeFixture({
			state: "succeeded",
			score: 76,
			policyCurrent: true,
			executionCurrent: true,
			criteria: [
				{ id: "accuracy", score: 6, rationale: "Mostly right." },
				{ id: "tests", score: 10, rationale: "" },
			],
			verifiers: [{ id: "tests", kind: "pythonTests", passed: true, detail: "3 of 3 tests passed." }],
		}),
		...overrides,
	});

function renderForm(run: BenchmarkRunDetail, criteria: BenchmarkRubricCriterion[] | null = rubric) {
	const onSubmit = vi.fn();
	renderWithProviders(<BenchmarkReviewForm run={run} criteria={criteria} onSubmit={onSubmit} />);
	return { onSubmit };
}

const input = (testId: string) => screen.getByTestId(testId) as HTMLInputElement;
const button = (testId: string) => screen.getByTestId(testId) as HTMLButtonElement;

describe("BenchmarkReviewForm", () => {
	it("pre-fills each llm criterion from the judging", () => {
		renderForm(judged());

		expect(input("benchmark-review-criterion-accuracy").value).toBe("6");
	});

	// A human does not overrule a deterministic check: the verifier's verdict is shown, never offered as an input.
	it("shows a verifiable criterion read-only with its verifier's verdict", () => {
		renderForm(judged());

		const verified = screen.getByTestId("benchmark-review-verified-tests");
		expect(verified.textContent).toContain("passed");
		expect(verified.textContent).toContain("3 of 3 tests passed.");
		expect(verified.querySelector("input")).toBeNull();
		expect(screen.queryByTestId("benchmark-review-criterion-tests")).toBeNull();
	});

	// The payload names only the reviewer's own scores; the node fills in the verifier's and computes the overall.
	it("approves with the llm criteria and the notes only", () => {
		const { onSubmit } = renderForm(judged());

		fireEvent.change(input("benchmark-review-criterion-accuracy"), { target: { value: "8" } });
		fireEvent.change(screen.getByTestId("benchmark-review-notes"), { target: { value: "Checked by hand." } });
		fireEvent.click(button("benchmark-review-approve"));

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith({
			state: "approved",
			criteria: [{ criterionId: "accuracy", score: 8 }],
			notes: "Checked by hand.",
		});
	});

	it("reviews one overall score when the project has no judge policy", () => {
		const { onSubmit } = renderForm(benchmarkRunDetailFixture(), null);

		// Nothing to pre-fill from, so an approval waits for the reviewer's number.
		expect(button("benchmark-review-approve").disabled).toBe(true);
		fireEvent.change(input("benchmark-review-overall"), { target: { value: "64" } });
		fireEvent.click(button("benchmark-review-approve"));

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith({ state: "approved", score: 64, notes: null });
	});

	it("rejects with no scores at all", () => {
		const { onSubmit } = renderForm(judged());

		fireEvent.click(button("benchmark-review-reject"));

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith({ state: "rejected", notes: null });
	});

	// Mirrors the node's NotReviewable: the reviewer scores the rubric the CURRENT judging checked.
	it.each([
		["a judging that is not finished", benchmarkJudgeFixture({ state: "running", policyCurrent: true })],
		["a judging under an older policy", benchmarkJudgeFixture({ state: "succeeded", policyCurrent: false })],
	])("locks both decisions with a re-judge hint for %s", (_, judge) => {
		renderForm(judged({ judge }));

		expect(button("benchmark-review-approve").disabled).toBe(true);
		expect(button("benchmark-review-reject").disabled).toBe(true);
		expect(screen.getByTestId("benchmark-review-blocked").textContent).toMatch(/re-judge this run first/i);
	});

	// A stale decision is exactly the one the reviewer is meant to make again, so it never locks the form.
	it("shows a stored stale review and lets it be decided again", () => {
		renderForm(
			judged({
				review: {
					state: "approved",
					score: 70,
					criteria: [{ criterionId: "accuracy", score: 9 }],
					notes: "Earlier pass.",
					policyCurrent: false,
					updatedAtUtc: 1_700_000_000_000,
				},
			}),
		);

		const stored = screen.getByTestId("benchmark-review-stored");
		expect(stored.textContent).toContain("stale");
		expect(stored.textContent).toContain("70 / 100");
		// The stored decision wins over the judging for the pre-fill: the reviewer revises their own last answer.
		expect(input("benchmark-review-criterion-accuracy").value).toBe("9");
		expect(button("benchmark-review-approve").disabled).toBe(false);
	});

	// Partial input is no score yet: never NaN, never a silent 0 the reviewer did not type.
	it("treats a lone minus sign as no score", () => {
		renderForm(benchmarkRunDetailFixture(), null);

		fireEvent.change(input("benchmark-review-overall"), { target: { value: "-" } });

		expect(button("benchmark-review-approve").disabled).toBe(true);
	});

	it("caps the notes at the node's limit and sends them trimmed", () => {
		const { onSubmit } = renderForm(judged());

		const notes = screen.getByTestId("benchmark-review-notes") as HTMLTextAreaElement;
		expect(notes.maxLength).toBe(8192);
		fireEvent.change(notes, { target: { value: "  Looks right.  " } });
		fireEvent.click(button("benchmark-review-reject"));

		expect(onSubmit).toHaveBeenCalledExactlyOnceWith({ state: "rejected", notes: "Looks right." });
	});
});
