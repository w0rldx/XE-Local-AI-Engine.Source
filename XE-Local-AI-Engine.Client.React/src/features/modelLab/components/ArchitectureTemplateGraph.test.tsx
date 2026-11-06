// @vitest-environment jsdom

// Through the real React Flow, unlike ArchitectureExplorer.test.tsx: the screen-reader descriptions it renders are what
// is under test, and they paint even though jsdom gives the canvas no size.

import { describe, expect, it } from "vitest";

import { ArchitectureTemplateGraph } from "@/features/modelLab/components/ArchitectureTemplateGraph";
import { renderWithProviders } from "@/test/RenderWithProviders";

const template = {
	kind: "fullAttention",
	provenance: "inferred",
	source: "llama.cpp src/models/qwen35.cpp @ b12014",
	nodes: [
		{ id: "input", label: "Residual in", component: null },
		{ id: "attn", label: "Gated attention", component: "attention" },
	],
	edges: [{ from: "input", to: "attn" }],
};

describe("ArchitectureTemplateGraph", () => {
	it("describes itself as read-only, and never offers to move or delete a node", () => {
		const { container } = renderWithProviders(
			<ArchitectureTemplateGraph
				template={template}
				groupComponents={new Set(["attention"])}
				selectedComponent={null}
				onSelectComponent={() => undefined}
			/>,
		);

		const text = container.textContent ?? "";
		expect(text).toContain(
			"Read-only graph. Select an operation with a pointer, or use the tensor groups to select by keyboard.",
		);
		expect(text).not.toMatch(/delete|arrow keys|move the node/i);
	});
});
