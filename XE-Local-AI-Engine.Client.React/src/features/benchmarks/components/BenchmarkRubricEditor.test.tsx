// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRubricEditor } from "@/features/benchmarks/components/BenchmarkRubricEditor";
import type { BenchmarkRubric, BenchmarkRubricCriterion } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRubricPresets } from "@/features/benchmarks/queries/useBenchmarks";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The editor's own contract is the node's bounds: 1..8 criteria, and a rubric that would be refused server-side must
// be refused here, on the row that carries the offending field. The add/remove/preset/validation FLOW through a save
// is covered by BenchmarkProjectForm's suite; this pins the edges that suite cannot reach.

const criterion = (index: number): BenchmarkRubricCriterion => ({
	id: `c${index}`,
	title: `Criterion ${index}`,
	description: "Look for this.",
	weight: 10,
});
const rubric = (count: number): BenchmarkRubric => ({
	version: 1,
	criteria: Array.from({ length: count }, (_, index) => criterion(index)),
});
const presets: BenchmarkRubricPresets = {
	default: rubric(1),
	programming: rubric(2),
	reasoning: rubric(1),
	verifiable: rubric(1),
};

function renderEditor(props: Partial<React.ComponentProps<typeof BenchmarkRubricEditor>> = {}) {
	const onChange = vi.fn();
	const view = renderWithProviders(
		<BenchmarkRubricEditor rubric={rubric(2)} presets={presets} issue={null} onChange={onChange} {...props} />,
	);
	return { ...view, onChange };
}

describe("BenchmarkRubricEditor", () => {
	afterEach(cleanup);

	// A preset button that "works" before the presets have loaded would silently blank the rubric.
	it("keeps the preset buttons inert until the presets have loaded", () => {
		const { onChange } = renderEditor({ presets: undefined });

		expect((screen.getByTestId("benchmark-rubric-preset-default") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("benchmark-rubric-preset-default"));

		expect(onChange).not.toHaveBeenCalled();
	});

	it.each([
		[8, "benchmark-rubric-add", "at the maximum"],
		[1, "benchmark-rubric-remove-0", "at the minimum"],
	])("locks the list %s criteria (%s)", (count, testId) => {
		renderEditor({ rubric: rubric(count) });

		expect((screen.getByTestId(testId) as HTMLButtonElement).disabled).toBe(true);
	});

	it("counts the criteria against the node's ceiling", () => {
		renderEditor({ rubric: rubric(3) });

		expect(screen.getByText("3 of 8 criteria")).toBeTruthy();
	});

	// The issue carries a row index; putting it on the wrong row would send the operator to the wrong field.
	it("shows an issue only on the criterion it belongs to", () => {
		renderEditor({ rubric: rubric(2), issue: { code: "weight", index: 1 } });

		const message = "The weight must be between 1 and 100.";
		expect(screen.getAllByText(message)).toHaveLength(1);
		expect(screen.getByTestId("benchmark-rubric-criterion-1").textContent).toContain(message);
		expect(screen.getByTestId("benchmark-rubric-criterion-0").textContent).not.toContain(message);
	});

	// A rubric-level issue has no row to attach to and must still be visible.
	it("reports a rubric-level issue above the rows", () => {
		renderEditor({ issue: { code: "count", index: -1 } });

		expect(screen.getByText("A rubric needs between 1 and 8 criteria.")).toBeTruthy();
	});

	it("edits one criterion without touching its siblings", () => {
		const { onChange } = renderEditor();

		fireEvent.change(screen.getAllByLabelText("Weight")[1] as HTMLElement, { target: { value: "42" } });

		expect(onChange).toHaveBeenCalledWith({
			version: 1,
			criteria: [criterion(0), { ...criterion(1), weight: 42 }],
		});
	});
});

// C4: each criterion now says HOW it is decided. The rubric editor's own job here is narrow — mount the per-criterion
// verifier editor and report the preset that makes every criterion server-decided.
describe("BenchmarkRubricEditor verifiable criteria", () => {
	afterEach(cleanup);

	it("offers the all-verifiable preset, which judges with no llama-server spawn at all", () => {
		const { onChange } = renderEditor();

		fireEvent.click(screen.getByTestId("benchmark-rubric-preset-verifiable"));

		expect(onChange).toHaveBeenCalledWith(presets.verifiable);
	});

	it("mounts a kind selector on every criterion", () => {
		renderEditor();

		expect(screen.getByTestId("benchmark-verifier-0-kind")).toBeTruthy();
		expect(screen.getByTestId("benchmark-verifier-1-kind")).toBeTruthy();
	});

	it("carries a criterion's kind and config back to the caller unchanged apart from the edit", () => {
		const { onChange } = renderEditor({
			rubric: {
				version: 1,
				criteria: [{ id: "answer", title: "Answer", description: "", weight: 10, kind: "exact", config: '{"expected":""}' }],
			},
		});

		fireEvent.change(screen.getByTestId("benchmark-verifier-0-expected"), { target: { value: "42" } });

		expect(onChange).toHaveBeenCalledWith({
			version: 1,
			criteria: [
				{ id: "answer", title: "Answer", description: "", weight: 10, kind: "exact", config: '{"expected":"42"}' },
			],
		});
	});
});
