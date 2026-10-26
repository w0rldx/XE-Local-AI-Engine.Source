// @vitest-environment jsdom

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import classes from "@/features/chat/components/ChatInputArea/ChatComposerToolbar.module.css";
import { WorkflowSelectorCard } from "@/features/chat/workflow/WorkflowSelectorCard";
import { renderWithProviders } from "@/test/RenderWithProviders";

const options = [
	{ id: "def-1", name: "Support triage", description: "Routes a request to the right helper." },
	{ id: "def-2", name: "Code review", description: null },
];

describe("WorkflowSelectorCard", () => {
	it("reads 'No workflow' until one is picked, then names it", () => {
		const { unmount } = renderWithProviders(
			<WorkflowSelectorCard options={options} selectedDefinitionId="" onSelect={vi.fn()} />,
		);

		expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("No workflow");

		unmount();
		renderWithProviders(<WorkflowSelectorCard options={options} selectedDefinitionId="def-2" onSelect={vi.fn()} />);
		expect(screen.getByTestId("chat-workflow-selector-trigger").textContent).toBe("Code review");
	});

	it("lists every chat workflow with a Chat badge and reports the pick, including 'No workflow'", () => {
		const onSelect = vi.fn();
		renderWithProviders(<WorkflowSelectorCard options={options} selectedDefinitionId="def-1" onSelect={onSelect} />);

		fireEvent.click(screen.getByTestId("chat-workflow-selector-trigger"));
		const option = screen.getByTestId("chat-workflow-selector-option-def-2");
		expect(option.textContent).toContain("Code review");
		expect(option.textContent).toContain("Chat");

		fireEvent.click(option);
		expect(onSelect).toHaveBeenCalledWith("def-2");

		fireEvent.click(screen.getByTestId("chat-workflow-selector-trigger"));
		fireEvent.click(screen.getByTestId("chat-workflow-selector-option-none"));
		expect(onSelect).toHaveBeenLastCalledWith("");
	});

	// Folded to an icon on a narrow composer (the toolbar hides the label and chevron): the accessible name and
	// the tooltip carry the current choice, and the same menu opens.
	it("names the current workflow in its accessible name and tooltip, so the folded icon-only trigger still says it", async () => {
		renderWithProviders(<WorkflowSelectorCard options={options} selectedDefinitionId="def-1" onSelect={vi.fn()} />);

		const trigger = screen.getByRole("button", { name: "Workflow: Support triage" });
		expect(trigger.classList.contains(classes["select-trigger"] ?? "")).toBe(true);
		expect(trigger.querySelector(`.${classes["select-label"]}`)?.textContent).toBe("Support triage");

		fireEvent.focus(trigger);
		expect((await screen.findByRole("tooltip")).textContent).toBe("Workflow: Support triage");

		fireEvent.click(trigger);
		expect(screen.getByTestId("chat-workflow-selector-option-def-2").textContent).toContain("Code review");
	});

	it("says where to create one when there are no chat workflows", () => {
		renderWithProviders(<WorkflowSelectorCard options={[]} selectedDefinitionId="" onSelect={vi.fn()} />);

		fireEvent.click(screen.getByTestId("chat-workflow-selector-trigger"));
		expect(screen.getByTestId("chat-workflow-selector-empty").textContent).toBe(
			"No chat workflows yet. Create one on the Graph Workflows page.",
		);
	});
});
