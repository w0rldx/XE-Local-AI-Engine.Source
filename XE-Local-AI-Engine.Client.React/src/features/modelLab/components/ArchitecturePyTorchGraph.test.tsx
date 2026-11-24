// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { ArchitecturePyTorchGraph } from "@/features/modelLab/components/ArchitecturePyTorchGraph";
import { testMantineTheme } from "@/test/MantineTestRender";
import { renderWithProviders } from "@/test/RenderWithProviders";

// jsdom gives a canvas no size, so the mock only reports the colour mode the graph hands React Flow.
vi.mock("@xyflow/react", () => ({
	Background: () => null,
	Controls: () => null,
	ReactFlow: ({ colorMode }: { colorMode?: string }) => <div data-testid="react-flow" data-color-mode={colorMode} />,
}));

describe("ArchitecturePyTorchGraph", () => {
	it.each(["light", "dark"] as const)("draws its canvas in the app's %s scheme", (scheme) => {
		renderWithProviders(
			<MantineProvider env="test" theme={testMantineTheme} forceColorScheme={scheme}>
				<ArchitecturePyTorchGraph
					graph={{ graphId: "g", nodes: [{ id: "a", label: "linear" }], edges: [] }}
					rootName="Block"
					selectedNodeId={null}
					onSelectNode={vi.fn()}
				/>
			</MantineProvider>,
		);

		expect(screen.getByTestId("react-flow").getAttribute("data-color-mode")).toBe(scheme);
	});
});
