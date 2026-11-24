// @vitest-environment jsdom

// Through the real React Flow, unlike ArchitectureExplorer.test.tsx: the screen-reader descriptions it renders are what
// is under test, and they paint even though jsdom gives the canvas no size.

import { useMantineColorScheme } from "@mantine/core";
import { act, fireEvent, screen } from "@testing-library/react";
import i18next from "i18next";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";

import { ArchitectureTemplateGraph } from "@/features/modelLab/components/ArchitectureTemplateGraph";
import type { ArchitectureTemplate } from "@/features/modelLab/models/ModelLabArchitecture";
import { loadModelLabStrings } from "@/features/modelLab/models/ModelLabStrings";
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

// One node past the graph's limit, so it lists the operations instead: two with German headings, then ids without one.
const longTemplate: ArchitectureTemplate = {
	...template,
	qkvLayout: "fused",
	nodes: [
		{ id: "input", label: "Block input (residual stream)", component: null },
		{ id: "q", label: "Query rows of the fused projection · attn_qkv", component: "attention" },
		...Array.from({ length: 79 }, (_, index) => ({ id: `extra_${index}`, label: `Extra ${index}`, component: null })),
	],
	edges: [],
};

// Its own controls switch the scheme and the template while mounted: a test rerender would drop the providers.
function Graph({ initial }: { initial: ArchitectureTemplate }) {
	const { setColorScheme } = useMantineColorScheme();
	const [shown, setShown] = useState(initial);
	return (
		<>
			<button type="button" onClick={() => setColorScheme("dark")}>
				dark
			</button>
			<button type="button" onClick={() => setShown(longTemplate)}>
				long
			</button>
			<button type="button" onClick={() => setShown(template)}>
				short
			</button>
			<ArchitectureTemplateGraph
				template={shown}
				architecture="llama"
				groupComponents={new Set(["attention"])}
				selectedComponent={null}
				onSelectNode={() => undefined}
			/>
		</>
	);
}

const flowRoot = () => document.querySelector(".react-flow");

describe("ArchitectureTemplateGraph", () => {
	afterEach(async () => {
		localStorage.clear();
		await act(() => i18next.changeLanguage("en"));
	});

	it("describes itself as read-only, and never offers to move or delete a node", () => {
		const { container } = renderWithProviders(
			<ArchitectureTemplateGraph
				template={template}
				architecture="qwen35"
				groupComponents={new Set(["attention"])}
				selectedComponent={null}
				onSelectNode={() => undefined}
			/>,
		);

		const text = container.textContent ?? "";
		expect(text).toContain(
			"Read-only graph. Select an operation with a pointer, or use the operation list below to select by keyboard.",
		);
		expect(text).not.toMatch(/delete|arrow keys|move the node/i);
	});

	it("follows the colour scheme, through a toggle and a switch to the list and back", () => {
		renderWithProviders(<Graph initial={template} />);
		expect(flowRoot()?.classList.contains("light")).toBe(true);

		fireEvent.click(screen.getByRole("button", { name: "dark" }));
		expect(flowRoot()?.classList.contains("dark")).toBe(true);

		fireEvent.click(screen.getByRole("button", { name: "long" }));
		expect(flowRoot()).toBeNull();
		expect(screen.getByTestId("model-lab-architecture-template-list")).toBeTruthy();

		fireEvent.click(screen.getByRole("button", { name: "short" }));
		expect(flowRoot()?.classList.contains("dark")).toBe(true);
	});

	it("lists the operations of an oversized template under their German headings, keeping English where none exists", async () => {
		await loadModelLabStrings("de-DE");
		await act(() => i18next.changeLanguage("de-DE"));
		renderWithProviders(<Graph initial={longTemplate} />);

		const items = [...screen.getByTestId("model-lab-architecture-template-list").querySelectorAll("li")].map(
			(item) => item.textContent,
		);
		expect(items).toHaveLength(81);
		expect(items.slice(0, 3)).toEqual([
			"Blockeingabe (Residualstrom)",
			"Query-Zeilen der zusammengefassten Projektion · attn_qkv",
			"Extra 0",
		]);
	});

	it("draws its nodes under their German headings", async () => {
		await loadModelLabStrings("de-DE");
		await act(() => i18next.changeLanguage("de-DE"));
		const { container } = renderWithProviders(<Graph initial={template} />);

		expect(container.textContent).toContain("Blockeingabe (Residualstrom)");
		expect(container.textContent).toContain("Attention über den KV-Cache");
		expect(container.textContent).not.toContain("Residual in");
	});
});
