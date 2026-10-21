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
			agents={[
				{ id: "agent-variant", name: "Assistant (terse)" },
				{ id: "agent-project", name: "Assistant" },
			]}
			projectAgentId="agent-project"
			leafItemCount={1}
			medianRunMs={null}
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

	// A suite multiplies the whole matrix: four combinations of a three-item project is twelve model loads, and the
	// dialog is the only place that arithmetic is done before the GPU-hours are committed.
	it("multiplies the combinations by the project's task items and extrapolates the time", () => {
		renderMatrix({ leafItemCount: 3, medianRunMs: 30_000 });

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q8_0"));

		const summary = screen.getByTestId("benchmark-matrix-summary").textContent ?? "";
		expect(summary).toContain("2 combinations × 3 task items × 1 runs = 6 runs");
		expect(summary).toContain("about 3m 0s");
	});

	// The node refuses the whole freeze above its per-request cap, so the dialog refuses it rather than sending a
	// request that comes back naming a number the operator never saw computed.
	it("refuses a matrix past the node's per-request run cap", () => {
		renderMatrix({ leafItemCount: 20 });

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q8_0"));
		fireEvent.change(screen.getByTestId("benchmark-matrix-repeat-count"), { target: { value: "5" } });

		expect(screen.getByTestId("benchmark-matrix-over-cap")).toBeTruthy();
		expect((screen.getByTestId("benchmark-matrix-start") as HTMLButtonElement).disabled).toBe(true);
	});

	// An extrapolation with nothing to extrapolate from is omitted rather than guessed.
	it("says nothing about time on a project with no completed run", () => {
		renderMatrix({ leafItemCount: 2, medianRunMs: null });

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));

		expect(screen.getByTestId("benchmark-matrix-summary").textContent).not.toContain("about");
	});

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
		expect(screen.getByTestId("benchmark-matrix-summary").textContent).toBe("1 combinations × 1 task items × 1 runs = 1 runs");

		fireEvent.click(screen.getByTestId("benchmark-matrix-warmup"));
		expect(screen.getByTestId("benchmark-matrix-summary").textContent).toBe("1 combinations × 1 task items × 2 runs = 2 runs");
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
			rejected: [
				{
					modelName: "owner/Repo:Q8_0",
					kvCacheType: "q4_0",
					agentDefinitionId: null,
					code: "UnsupportedKvCacheType",
					message: "Needs a GPU build.",
				},
			],
		});

		expect(screen.getByTestId("benchmark-matrix-rejected").textContent).toContain("owner/Repo:Q8_0 · q4_0 — Needs a GPU build.");
	});

	// The project's agent is the default variant: preselected, listed first, and the last one standing.
	it("preselects the project's agent first and never lets the agent axis go empty", () => {
		renderMatrix();

		const agentBoxes = screen.getAllByTestId(/^benchmark-matrix-agent-/).map((box) => box.getAttribute("data-testid"));
		expect(agentBoxes).toEqual(["benchmark-matrix-agent-agent-project", "benchmark-matrix-agent-agent-variant"]);
		const projectAgent = screen.getByTestId("benchmark-matrix-agent-agent-project") as HTMLInputElement;
		expect(projectAgent.checked).toBe(true);

		fireEvent.click(projectAgent);

		expect(projectAgent.checked).toBe(true);
	});

	// Each extra agent runs every model × KV combination again, so the count and the request both multiply by it.
	it("multiplies the combinations by the agent variants and names a variant only when it is not the project's agent", () => {
		const { onSubmit } = renderMatrix({ leafItemCount: 2 });

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q8_0"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-kv-f16"));
		fireEvent.click(screen.getByTestId("benchmark-matrix-agent-agent-variant"));

		expect(screen.getByTestId("benchmark-matrix-summary").textContent).toBe("8 combinations × 2 task items × 1 runs = 16 runs");

		fireEvent.click(screen.getByTestId("benchmark-matrix-start"));

		// Two models × (Auto, f16) × (project agent, variant). The project's agent rides as an OMITTED id, which the node
		// reads as the project's own agent.
		expect(onSubmit.mock.calls[0]?.[0].items).toEqual([
			{ modelName: "owner/Repo:Q4_K_M" },
			{ modelName: "owner/Repo:Q4_K_M", agentDefinitionId: "agent-variant" },
			{ modelName: "owner/Repo:Q4_K_M", kvCacheType: "f16" },
			{ modelName: "owner/Repo:Q4_K_M", kvCacheType: "f16", agentDefinitionId: "agent-variant" },
			{ modelName: "owner/Repo:Q8_0" },
			{ modelName: "owner/Repo:Q8_0", agentDefinitionId: "agent-variant" },
			{ modelName: "owner/Repo:Q8_0", kvCacheType: "f16" },
			{ modelName: "owner/Repo:Q8_0", kvCacheType: "f16", agentDefinitionId: "agent-variant" },
		]);
	});

	// A second agent doubles the runs, so a matrix under the cap with one agent can cross it with two.
	it("counts the agent variants against the node's per-request run cap", () => {
		renderMatrix({ leafItemCount: 20 });

		fireEvent.click(screen.getByTestId("benchmark-matrix-model-owner/Repo:Q4_K_M"));
		fireEvent.change(screen.getByTestId("benchmark-matrix-repeat-count"), { target: { value: "3" } });
		expect(screen.queryByTestId("benchmark-matrix-over-cap")).toBeNull();

		fireEvent.click(screen.getByTestId("benchmark-matrix-agent-agent-variant"));

		expect(screen.getByTestId("benchmark-matrix-over-cap").textContent).toContain("agent variants");
		expect((screen.getByTestId("benchmark-matrix-start") as HTMLButtonElement).disabled).toBe(true);
	});

	it("names the agent variant of a refused combination", () => {
		renderMatrix({
			rejected: [
				{
					modelName: "owner/Repo:Q8_0",
					kvCacheType: null,
					agentDefinitionId: "agent-variant",
					code: "InvalidRequest",
					message: "Not a single agent.",
				},
			],
		});

		expect(screen.getByTestId("benchmark-matrix-rejected").textContent).toContain(
			"owner/Repo:Q8_0 · auto · Assistant (terse) — Not a single agent.",
		);
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
