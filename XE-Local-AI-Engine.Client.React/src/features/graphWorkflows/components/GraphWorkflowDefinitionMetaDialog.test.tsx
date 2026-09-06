// @vitest-environment jsdom

// The dialog owns the two fields that are not part of the graph document. What it owes is that a submit carries a
// trimmed name and a null (not an empty) description, and that the server's own bounds are enforced here rather than
// discovered as a 400 after the graph has already been built.

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowDefinitionMetaDialog } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionMetaDialog";
import { renderWithProviders } from "@/test/RenderWithProviders";

type SubmitHandler = (values: { name: string; description: string | null }) => void;

function renderDialog(onSubmit: SubmitHandler, initial?: { name: string; description?: string | null }) {
	return renderWithProviders(
		<GraphWorkflowDefinitionMetaDialog
			opened={true}
			initial={initial}
			title="New workflow"
			submitLabel="Create"
			onSubmit={onSubmit}
			onClose={vi.fn()}
		/>,
	);
}

describe("GraphWorkflowDefinitionMetaDialog", () => {
	it("seeds both fields from the definition it was opened over", () => {
		renderDialog(vi.fn(), { name: "Nightly triage", description: "Runs at 02:00" });

		expect((screen.getByTestId("gw-definition-meta-name") as HTMLInputElement).value).toBe("Nightly triage");
		expect((screen.getByTestId("gw-definition-meta-description") as HTMLTextAreaElement).value).toBe("Runs at 02:00");
	});

	it("refuses an empty name and submits nothing", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(screen.getByText("Enter a name.")).toBeTruthy();
		expect(onSubmit).not.toHaveBeenCalled();
	});

	it("refuses a name past the server's 120-character bound", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		// `maxLength` stops typing past the bound in a browser; a paste or an autofill still gets through, so the
		// schema is what actually refuses it.
		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "n".repeat(121) } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(screen.getByText("Use at most 120 characters.")).toBeTruthy();
		expect(onSubmit).not.toHaveBeenCalled();
	});

	it("submits a trimmed name and a null description when none was typed", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "  Nightly triage  " } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(onSubmit).toHaveBeenCalledWith({ name: "Nightly triage", description: null });
	});

	it("submits the description when one was typed", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "Release notes" } });
		fireEvent.change(screen.getByTestId("gw-definition-meta-description"), { target: { value: " Drafts the notes " } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(onSubmit).toHaveBeenCalledWith({ name: "Release notes", description: "Drafts the notes" });
	});
});
