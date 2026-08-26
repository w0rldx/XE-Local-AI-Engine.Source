// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The breakdown reads run DETAILS for the cell that is opened, through the same per-run cache the live panes use.
const { getRunMock } = vi.hoisted(() => ({ getRunMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	getBenchmarkRun: getRunMock,
}));

import { BenchmarkCellsTable } from "@/features/benchmarks/components/BenchmarkCellsTable";
import type { BenchmarkCell } from "@/features/benchmarks/models/BenchmarkCells";
import { benchmarkCellFixture, benchmarkTaskItemFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const cohort = { policyRevision: 1, executionKey: "k", cohortGeneration: 1, rankedCount: 1, totalScored: 2 };

/** A generated case carries the `exact` override that names the criterion its needle is decided on. */
const niahCase = (id: string) =>
	benchmarkTaskItemFixture({ id, kind: "niahCase", countsTowardScore: false, verifierConfig: { recall: { expected: "x" } } });

const items = [
	benchmarkTaskItemFixture({ id: "a", index: 0, prompt: "Capital of France?" }),
	benchmarkTaskItemFixture({ id: "b", index: 1, prompt: "17 times 3?" }),
];

const complete: BenchmarkCell = benchmarkCellFixture({
	cellKey: "cell:one:1",
	primaryModelName: "owner/Repo:Q4_K_M",
	kvCacheType: "q8_0",
	quality: 75,
	rank: 1,
	items: [
		{ runId: "run-a", taskItemId: "a", taskItemIndex: 0, qualityScore: 80, primaryStopReason: "stop", rankExclusionReason: null },
		{ runId: "run-b", taskItemId: "b", taskItemIndex: 1, qualityScore: 70, primaryStopReason: "stop", rankExclusionReason: null },
	],
});

const incomplete: BenchmarkCell = benchmarkCellFixture({
	cellKey: "cell:two:1",
	primaryModelName: "owner/Repo:Q8_0",
	quality: null,
	rank: null,
	rankExclusionReason: "item-incomplete",
	items: [
		{ runId: "run-c", taskItemId: "a", taskItemIndex: 0, qualityScore: 90, primaryStopReason: "stop", rankExclusionReason: null },
	],
});

const render = (props: Partial<Parameters<typeof BenchmarkCellsTable>[0]> = {}) => {
	const onRerunCell = vi.fn();
	const onToggleRun = vi.fn();
	renderWithProviders(
		<BenchmarkCellsTable
			cells={[complete, incomplete]}
			cohort={cohort}
			scorableItemCount={2}
			items={items}
			selectedRunIds={[]}
			onToggleRun={onToggleRun}
			onRerunCell={onRerunCell}
			{...props}
		/>,
	);
	return { onRerunCell, onToggleRun };
};

// A needle's verdict lives on the run's own verifier evidence, so the mock answers per run id rather than with one
// canned detail: a run with no entry here is a run whose verdict is genuinely not on record.
let verifiersByRun: Record<string, { id: string; kind: string; passed: boolean; detail: string }[]> = {};

describe("BenchmarkCellsTable", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		verifiersByRun = { "run-a": [{ id: "accuracy", kind: "exact", passed: true, detail: "Matched 'Paris'." }] };
		getRunMock.mockImplementation(({ path }: { path: { runId: string } }) =>
			Promise.resolve({
				data: {
					id: path.runId,
					projectId: "project-1",
					primaryModelName: "owner/Repo:Q4_K_M",
					judge: { state: "succeeded", score: 80, verifiers: verifiersByRun[path.runId] ?? [] },
				},
			}),
		);
	});
	afterEach(cleanup);

	it("ranks the complete combination and leaves the partial one out with its reason", () => {
		render();

		expect(screen.getByTestId("benchmark-cell-quality-cell:one:1").textContent).toBe("75");
		expect(screen.getByTestId("benchmark-cell-quality-cell:two:1").textContent).toBe("—");
		expect(screen.getByTestId("benchmark-cell-exclusion-cell:two:1").textContent).toContain("item missing");
	});

	// The point of `item-incomplete`: a cell that answered one of two questions must not be compared against one that
	// answered both, and the count is what makes that visible without opening it.
	it("counts how many of the scored items each combination answered", () => {
		render();

		expect(screen.getByTestId("benchmark-cell-items-cell:one:1").textContent).toBe("2 of 2");
		expect(screen.getByTestId("benchmark-cell-items-cell:two:1").textContent).toBe("1 of 2");
	});

	it("names the question a partial combination never answered", () => {
		render();
		fireEvent.click(screen.getByTestId("benchmark-cell-toggle-cell:two:1"));

		expect(screen.getByTestId("benchmark-cell-missing-cell:two:1").textContent).toContain("17 times 3?");
	});

	it("shows each item's own question, score and verifier evidence when a combination is opened", async () => {
		render();
		fireEvent.click(screen.getByTestId("benchmark-cell-toggle-cell:one:1"));

		expect(screen.getByTestId("benchmark-cell-item-run-a").textContent).toContain("Capital of France?");
		expect(screen.getByTestId("benchmark-cell-item-score-run-b").textContent).toBe("70");
		expect((await screen.findByTestId("benchmark-cell-verifier-run-a-accuracy")).textContent).toBeTruthy();
	});

	// The node has no per-item start, so the honest offer is the whole combination.
	it("re-runs the whole combination", () => {
		const { onRerunCell } = render();

		fireEvent.click(screen.getByTestId("benchmark-cell-rerun-cell:two:1"));

		expect(onRerunCell).toHaveBeenCalledWith(incomplete);
	});

	// Recall is measured and reported, and deliberately NOT in the mean beside it: recall at 32k and answer quality
	// are different measurements, and their average is neither. Each needle is read from its own criterion's verdict —
	// the aggregate beside it carries the rest of the rubric's weight and cannot answer "was the needle found".
	it("reports needle recall from each case's own criterion, not from its aggregate score", async () => {
		verifiersByRun = {
			// Found the needle, mediocre answer; missed it, good answer. A midpoint split on the aggregate reads BOTH
			// of these backwards.
			r1: [{ id: "recall", kind: "exact", passed: true, detail: "Found." }],
			r2: [{ id: "recall", kind: "exact", passed: false, detail: "Not found." }],
		};
		const withProbes = benchmarkCellFixture({
			cellKey: "cell:niah:1",
			quality: 80,
			rank: 1,
			items: [
				{
					runId: "r1",
					taskItemId: "n1",
					taskItemIndex: 0,
					qualityScore: 31,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
				{
					runId: "r2",
					taskItemId: "n2",
					taskItemIndex: 1,
					qualityScore: 72,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});
		render({ cells: [withProbes], items: [niahCase("n1"), niahCase("n2")], scorableItemCount: 0 });

		// The cell renders from the first paint holding "recall unknown", so the wait is on the TEXT settling, not on
		// the node appearing.
		await waitFor(() => expect(screen.getByTestId("benchmark-cell-recall-cell:niah:1").textContent).toBe("1 of 2 needles"));
		expect(screen.getByTestId("benchmark-cell-quality-cell:niah:1").textContent).toBe("80");
	});

	// Absent evidence is not a miss: "0 of 2 needles" would report a failure the suite never measured.
	it("says recall is unknown rather than zero when no verdict is on record", async () => {
		const withProbes = benchmarkCellFixture({
			cellKey: "cell:niah:2",
			items: [
				{
					runId: "r1",
					taskItemId: "n1",
					taskItemIndex: 0,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
				{
					runId: "r2",
					taskItemId: "n2",
					taskItemIndex: 1,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});
		render({ cells: [withProbes], items: [niahCase("n1"), niahCase("n2")], scorableItemCount: 0 });

		await waitFor(() => expect(getRunMock).toHaveBeenCalled());
		expect(screen.getByTestId("benchmark-cell-recall-cell:niah:2").textContent).toBe("recall unknown");
	});

	// A partly-known axis says both halves rather than quietly reporting the known ones as the whole measurement.
	it("names how many needles have no verdict beside the ones that do", async () => {
		verifiersByRun = { r1: [{ id: "recall", kind: "exact", passed: true, detail: "Found." }] };
		const withProbes = benchmarkCellFixture({
			cellKey: "cell:niah:3",
			items: [
				{
					runId: "r1",
					taskItemId: "n1",
					taskItemIndex: 0,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
				{
					runId: "r2",
					taskItemId: "n2",
					taskItemIndex: 1,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});
		render({ cells: [withProbes], items: [niahCase("n1"), niahCase("n2")], scorableItemCount: 0 });

		await waitFor(() =>
			expect(screen.getByTestId("benchmark-cell-recall-cell:niah:3").textContent).toBe("1 of 1 needles · 1 unknown"),
		);
	});

	// A pure long-context probe project has nothing to take a mean OF, so the node excludes its cells as `no-score`.
	// The ordinary reading of that reason — "set an operator score" — is wrong there: no score an operator could give
	// would enter a mean that does not exist.
	it("does not tell an operator to score a project where nothing counts", async () => {
		verifiersByRun = { r1: [{ id: "recall", kind: "exact", passed: true, detail: "Found." }] };
		const unscored = benchmarkCellFixture({
			cellKey: "cell:probe",
			quality: null,
			rank: null,
			rankExclusionReason: "no-score",
			items: [
				{
					runId: "r1",
					taskItemId: "n1",
					taskItemIndex: 0,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});
		render({ cells: [unscored], items: [niahCase("n1")], scorableItemCount: 0 });

		expect(screen.getByTestId("benchmark-cells-display-only").textContent).toContain("Recall is the measurement here");
		expect(screen.queryByTestId("benchmark-cell-exclusion-cell:probe")).toBeNull();
		await waitFor(() => expect(screen.getByTestId("benchmark-cell-recall-cell:probe").textContent).toBe("1 of 1 needles"));
	});

	it("says so when a project has nothing measured yet", () => {
		render({ cells: [] });

		expect(screen.queryByTestId("benchmark-cells-table")).toBeNull();
	});
});
