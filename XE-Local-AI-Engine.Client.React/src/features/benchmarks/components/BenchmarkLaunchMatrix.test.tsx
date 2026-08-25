// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkLaunchMatrix } from "@/features/benchmarks/components/BenchmarkLaunchMatrix";
import type { BenchmarkEligibleModel } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The matrix's whole job is turning "n models × m KV types" into ONE request with n×m items. The cross product and the
// Auto encoding are the two things a mistake here would silently get wrong: a dropped combination is a run the operator
// thinks is queued, and sending "auto" as a literal KV type would be refused by the node as an unknown type.

const model = (modelName: string): BenchmarkEligibleModel => ({
	modelName,
	maxContextTokens: 8192,
	effectiveContextTokens: 8192,
	origin: "huggingface",
	modelContentFingerprint: `v1:${modelName}`,
	supportsTools: true,
});

function renderMatrix(props: Partial<React.ComponentProps<typeof BenchmarkLaunchMatrix>> = {}) {
	const onSubmit = vi.fn();
	const onCancel = vi.fn();
	const view = renderWithProviders(
		<BenchmarkLaunchMatrix
			models={[model("owner/Repo:Q4_K_M"), model("owner/Repo:Q8_0")]}
			rejected={[]}
			isSubmitting={false}
			onSubmit={onSubmit}
			onCancel={onCancel}
			{...props}
		/>,
	);
	return { ...view, onSubmit, onCancel };
}

describe("BenchmarkLaunchMatrix", () => {
	afterEach(cleanup);

	it("submits the cross product of the selected models and KV types", () => {
		const { onSubmit } = renderMatrix();

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q8_0"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-kv-f16"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-start"));

		// Two models × (Auto, which is preselected, plus f16) = four cells. Auto rides as an OMITTED kvCacheType.
		expect(onSubmit).toHaveBeenCalledTimes(1);
		expect(onSubmit.mock.calls[0]?.[0]).toEqual({
			items: [
				{ modelName: "owner/Repo:Q4_K_M" },
				{ modelName: "owner/Repo:Q4_K_M", kvCacheType: "f16" },
				{ modelName: "owner/Repo:Q8_0" },
				{ modelName: "owner/Repo:Q8_0", kvCacheType: "f16" },
			],
			repeatCount: 1,
			warmup: false,
			repeatMode: "Throughput",
			answerVarianceTemperature: null,
		});
	});

	// The count is the only place an operator sees what they are about to commit the box to for the next hour.
	it("counts the warm-up in the runs it says it will start", () => {
		renderMatrix();

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		expect(screen.getByTestId("benchmark-matrix-summary").textContent).toBe("1 combinations × 1 runs = 1 runs");

		fireEvent.click(screen.getByTestId("benchmark-matrix-warmup"));
		expect(screen.getByTestId("benchmark-matrix-summary").textContent).toBe("1 combinations × 2 runs = 2 runs");
	});

	// Submitting nothing would send an empty batch the node answers with a 400 — the button says so instead.
	it("refuses to submit with no model selected", () => {
		const { onSubmit } = renderMatrix();

		expect(screen.getByTestId("benchmark-matrix-start").hasAttribute("disabled")).toBe(true);
		fireEvent.click(screen.getByTestId("benchmark-matrix-start"));

		expect(onSubmit).not.toHaveBeenCalled();
	});

	// Per-item rejections come back inside a 200, so they have nowhere to surface unless the dialog shows them.
	it("lists the combinations the node refused", () => {
		renderMatrix({
			rejected: [{ modelName: "owner/Repo:Q8_0", kvCacheType: "q4_0", code: "UnsupportedKvCacheType", message: "Needs a GPU build." }],
		});

		expect(screen.getByTestId("benchmark-matrix-rejected").textContent).toContain("owner/Repo:Q8_0 · q4_0 — Needs a GPU build.");
	});

	// Answer variance is a different experiment, not a flag on the same one: the request carries the mode, and the
	// temperature only when that mode uses it.
	it("submits the answer-variance mode with its temperature", () => {
		const { onSubmit } = renderMatrix();

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.click(screen.getByTestId("benchmark-repeat-mode"));
		fireEvent.click(screen.getByRole("option", { name: "Answer variance" }));
		fireEvent.click(screen.getByTestId("benchmark-matrix-start"));

		expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ repeatMode: "AnswerVariance", answerVarianceTemperature: null });
	});
});
