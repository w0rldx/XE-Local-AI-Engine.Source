// @vitest-environment jsdom

// What a DecisionModel run view may say: the server's own numbers per question type, the "uncalibrated" caption
// whenever a model-reported number is on screen, and nothing invented when the provider (llm) reported none.

import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { GraphWorkflowDecisionModelResult } from "@/features/graphWorkflows/components/GraphWorkflowDecisionModelResult";
import { renderWithProviders } from "@/test/RenderWithProviders";

function rowLabels(): string[] {
	return within(screen.getByTestId("graph-workflow-node-panel-probabilities"))
		.getAllByTestId(/^graph-workflow-node-panel-probability-/)
		.map((row) => row.textContent ?? "");
}

describe("GraphWorkflowDecisionModelResult", () => {
	afterEach(() => {
		cleanup();
	});

	it("keeps today's single line for an llm choice and claims no probabilities", () => {
		renderWithProviders(<GraphWorkflowDecisionModelResult output={{ choice: "coding", provider: "llm" }} />);

		expect(screen.getByTestId("graph-workflow-node-panel-choice").textContent).toBe("Chose coding");
		expect(screen.queryByTestId("graph-workflow-node-panel-probabilities")).toBeNull();
		expect(screen.queryByTestId("graph-workflow-node-panel-confidence")).toBeNull();
		expect(screen.queryByTestId("graph-workflow-node-panel-uncalibrated")).toBeNull();
	});

	it("lists a systemone choice's probabilities most likely first, with confidence and the caption", () => {
		renderWithProviders(
			<GraphWorkflowDecisionModelResult
				output={{
					type: "choice",
					choice: "billing",
					probabilities: { shipping: 0.05, billing: 0.9, technical: 0.05 },
					confidence: 0.9,
					provider: "systemone",
				}}
			/>,
		);

		expect(screen.getByTestId("graph-workflow-node-panel-choice").textContent).toBe("Chose billing");
		expect(rowLabels()).toEqual(["billing90%", "shipping5%", "technical5%"]);
		expect(screen.getByTestId("graph-workflow-node-panel-confidence").textContent).toBe("Confidence 90%");
		expect(screen.getByTestId("graph-workflow-node-panel-uncalibrated").textContent).toBe("Model-reported, uncalibrated.");
	});

	it("shows a score with its nearest level, the legend, and per-level probabilities in level order", () => {
		renderWithProviders(
			<GraphWorkflowDecisionModelResult
				output={{
					type: "score",
					choice: "soon",
					score: 1.234,
					legend: { "0": "can wait", "1": "soon", "2": "now" },
					probabilities: { "0": 0.2, "1": 0.5, "2": 0.3 },
					confidence: 0.5,
				}}
			/>,
		);

		expect(screen.getByTestId("graph-workflow-node-panel-score").textContent).toBe("Score 1.23, nearest level soon");
		expect(screen.getByTestId("graph-workflow-node-panel-legend").textContent).toBe("0: can wait1: soon2: now");
		expect(rowLabels()).toEqual(["can wait20%", "soon50%", "now30%"]);
		expect(screen.queryByTestId("graph-workflow-node-panel-choice")).toBeNull();
		expect(screen.getByTestId("graph-workflow-node-panel-uncalibrated")).toBeTruthy();
	});

	it("reads a noul answer as P(yes) next to the server's own routing literal, never a recomputed one", () => {
		// 0.5 with "no" on purpose: the readout is whatever the server routed on, not the client's own threshold.
		const routed = renderWithProviders(<GraphWorkflowDecisionModelResult output={{ type: "noul", choice: "no", noul: 0.5 }} />);
		expect(screen.getByTestId("graph-workflow-node-panel-noul").textContent).toBe("P(yes) 50%: no");
		expect(screen.getByTestId("graph-workflow-node-panel-uncalibrated")).toBeTruthy();
		expect(screen.queryByTestId("graph-workflow-node-panel-choice")).toBeNull();
		routed.unmount();

		renderWithProviders(<GraphWorkflowDecisionModelResult output={{ type: "noul", noul: 0.123 }} />);
		expect(screen.getByTestId("graph-workflow-node-panel-noul").textContent).toBe("P(yes) 12.3%");
	});

	it("says a result type it does not know is unsupported instead of reading it as a choice", () => {
		renderWithProviders(<GraphWorkflowDecisionModelResult output={{ type: "ranking", choice: "a" }} />);

		expect(screen.getByTestId("graph-workflow-node-panel-decision-unsupported").textContent).toBe(
			"This version cannot show this result type. The raw output is below.",
		);
		expect(screen.queryByTestId("graph-workflow-node-panel-choice")).toBeNull();
	});
});
