// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkScorePicker } from "@/features/benchmarks/components/BenchmarkScorePicker";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The operator score is a 1..5 star row. It is a real form control, not decoration: each star carries its own
// accessible name and pressed state, and the whole row must lock while the score is saving or the run is not
// scoreable yet — a second click during the save would post a competing score against a stale run version.

describe("BenchmarkScorePicker", () => {
	afterEach(cleanup);

	it("offers exactly five scores", () => {
		renderWithProviders(<BenchmarkScorePicker value={null} disabled={false} onChange={vi.fn()} />);

		expect([1, 2, 3, 4, 5].map((score) => screen.getByTestId(`benchmark-score-${score}`))).toHaveLength(5);
		expect(screen.queryByTestId("benchmark-score-6")).toBeNull();
		expect(screen.queryByTestId("benchmark-score-0")).toBeNull();
	});

	it("marks only the selected score as pressed", () => {
		renderWithProviders(<BenchmarkScorePicker value={3} disabled={false} onChange={vi.fn()} />);

		expect(screen.getByTestId("benchmark-score-3").getAttribute("aria-pressed")).toBe("true");
		expect(screen.getByTestId("benchmark-score-2").getAttribute("aria-pressed")).toBe("false");
		expect(screen.getByTestId("benchmark-score-4").getAttribute("aria-pressed")).toBe("false");
	});

	it("marks nothing as pressed when the run is unscored", () => {
		renderWithProviders(<BenchmarkScorePicker value={null} disabled={false} onChange={vi.fn()} />);

		for (const score of [1, 2, 3, 4, 5]) {
			expect(screen.getByTestId(`benchmark-score-${score}`).getAttribute("aria-pressed")).toBe("false");
		}
	});

	it("reports the clicked score", () => {
		const onChange = vi.fn();
		renderWithProviders(<BenchmarkScorePicker value={null} disabled={false} onChange={onChange} />);

		fireEvent.click(screen.getByTestId("benchmark-score-4"));

		expect(onChange).toHaveBeenCalledExactlyOnceWith(4);
	});

	// Re-picking the score already stored still reports it; suppressing that would strand a failed save.
	it("reports a re-click of the current score", () => {
		const onChange = vi.fn();
		renderWithProviders(<BenchmarkScorePicker value={2} disabled={false} onChange={onChange} />);

		fireEvent.click(screen.getByTestId("benchmark-score-2"));

		expect(onChange).toHaveBeenCalledExactlyOnceWith(2);
	});

	it.each([
		["disabled", { disabled: true, isSaving: false }],
		["saving", { disabled: false, isSaving: true }],
	])("locks every star while %s", (_case, props) => {
		const onChange = vi.fn();
		renderWithProviders(<BenchmarkScorePicker value={null} onChange={onChange} {...props} />);

		for (const score of [1, 2, 3, 4, 5]) {
			expect((screen.getByTestId(`benchmark-score-${score}`) as HTMLButtonElement).disabled).toBe(true);
		}

		fireEvent.click(screen.getByTestId("benchmark-score-5"));

		expect(onChange).not.toHaveBeenCalled();
	});

	it("exposes the row and each star to assistive tech", () => {
		renderWithProviders(<BenchmarkScorePicker value={null} disabled={false} onChange={vi.fn()} />);

		expect(screen.getByRole("group", { name: "Operator score" })).toBeTruthy();
		expect(screen.getByLabelText("Score 3 of 5")).toBeTruthy();
	});
});
