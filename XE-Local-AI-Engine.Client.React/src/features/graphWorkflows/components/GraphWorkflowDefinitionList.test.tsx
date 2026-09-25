// @vitest-environment jsdom

// The list is pure presentation, so what it owes is that every row action reaches the page with the right id — a
// delete that reports the wrong definition is the one bug here that is not recoverable.

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowDefinitionList } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionList";
import { graphWorkflowDefinitionSummary } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const first = graphWorkflowDefinitionSummary({ id: "def-1", name: "Nightly triage", nodeCount: 8, version: 3 });
const second = graphWorkflowDefinitionSummary({ id: "def-2", name: "Release notes", nodeCount: 4, version: 1 });

describe("GraphWorkflowDefinitionList", () => {
	it("lists every definition it was given", () => {
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[first, second]} onSelect={vi.fn()} onCreate={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByTestId("gw-definition-name-def-1").textContent).toBe("Nightly triage");
		expect(screen.getByTestId("gw-definition-name-def-2").textContent).toBe("Release notes");
		// Rows, not a table: five columns in the fixed-width rail forced a horizontal scrollbar.
		expect(screen.queryByRole("table")).toBeNull();
		expect(screen.queryByTestId("gw-definition-list-empty")).toBeNull();
	});

	it("marks a Chat definition with a Chat badge and leaves a Standard one unmarked", () => {
		const chat = graphWorkflowDefinitionSummary({ id: "def-3", name: "Support triage", kind: "Chat" });
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[first, chat]} onSelect={vi.fn()} onCreate={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByTestId("gw-definition-chat-badge-def-3").textContent).toBe("Chat");
		expect(screen.queryByTestId("gw-definition-chat-badge-def-1")).toBeNull();
	});

	it("summarises nodes, version and update time on one meta line, the name carrying its full text as a title", () => {
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[first, second]} onSelect={vi.fn()} onCreate={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByTestId("gw-definition-meta-def-1").textContent).toMatch(/^8 nodes · v3 · updated /);
		expect(screen.getByTestId("gw-definition-name-def-2").getAttribute("title")).toBe("Release notes");
	});

	it("marks only the selected row as current", () => {
		renderWithProviders(
			<GraphWorkflowDefinitionList
				definitions={[first, second]}
				selectedId="def-2"
				onSelect={vi.fn()}
				onCreate={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("gw-definition-open-def-2").getAttribute("aria-current")).toBe("true");
		expect(screen.getByTestId("gw-definition-open-def-1").getAttribute("aria-current")).toBeNull();
		expect(screen.getByTestId("gw-definition-row-def-2").getAttribute("data-selected")).toBe("true");
	});

	it("reports the row the operator opened and the row they deleted", () => {
		const onSelect = vi.fn();
		const onDelete = vi.fn();
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[first, second]} onSelect={onSelect} onCreate={vi.fn()} onDelete={onDelete} />,
		);

		fireEvent.click(screen.getByTestId("gw-definition-open-def-2"));
		fireEvent.click(screen.getByTestId("gw-definition-delete-def-1"));

		expect(onSelect).toHaveBeenCalledWith("def-2");
		expect(onDelete).toHaveBeenCalledWith("def-1");
	});

	it("asks the page to open the meta dialog for a new definition", () => {
		const onCreate = vi.fn();
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[]} onSelect={vi.fn()} onCreate={onCreate} onDelete={vi.fn()} />,
		);

		fireEvent.click(screen.getByTestId("gw-definition-create"));

		expect(onCreate).toHaveBeenCalledTimes(1);
	});

	it("shows the empty state rather than an empty list", () => {
		renderWithProviders(
			<GraphWorkflowDefinitionList definitions={[]} onSelect={vi.fn()} onCreate={vi.fn()} onDelete={vi.fn()} />,
		);

		expect(screen.getByTestId("gw-definition-list-empty").textContent).toBe("No workflows yet. Create one to start authoring.");
		expect(screen.queryByTestId("gw-definition-rows")).toBeNull();
	});

	it("renders a load failure as an alert instead of an empty list", () => {
		renderWithProviders(
			<GraphWorkflowDefinitionList
				definitions={[]}
				error={new Error("boom")}
				onSelect={vi.fn()}
				onCreate={vi.fn()}
				onDelete={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("gw-definition-list-error")).toBeTruthy();
	});
});
