// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowsPage } from "@/features/graphWorkflows/pages/GraphWorkflowsPage";
import { graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("GraphWorkflowsPage", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders the page header", () => {
		renderWithProviders(<GraphWorkflowsPage selection={{}} onSelectionChange={vi.fn()} />);

		expect(screen.getByRole("heading", { name: "Graph Workflows" })).not.toBeNull();
	});

	it("says the editor is not built when no run is selected", () => {
		renderWithProviders(<GraphWorkflowsPage selection={{ definitionId: graphWorkflowTestIds.definition }} onSelectionChange={vi.fn()} />);

		expect(screen.getByTestId("graph-workflows-empty").textContent).toMatch(/graph editor/i);
	});

	it("switches to the run message once a runId is in the selection", () => {
		renderWithProviders(<GraphWorkflowsPage selection={{ runId: graphWorkflowTestIds.run }} onSelectionChange={vi.fn()} />);

		expect(screen.getByTestId("graph-workflows-empty").textContent).toMatch(/run view/i);
	});

	it("emits an empty selection through onSelectionChange", () => {
		const onSelectionChange = vi.fn();
		renderWithProviders(<GraphWorkflowsPage selection={{ runId: graphWorkflowTestIds.run }} onSelectionChange={onSelectionChange} />);

		fireEvent.click(screen.getByTestId("graph-workflows-clear-selection"));

		expect(onSelectionChange).toHaveBeenCalledWith({});
	});

	it("offers nothing to clear when the selection is already empty", () => {
		renderWithProviders(<GraphWorkflowsPage selection={{}} onSelectionChange={vi.fn()} />);

		expect(screen.getByTestId<HTMLButtonElement>("graph-workflows-clear-selection").disabled).toBe(true);
	});
});
