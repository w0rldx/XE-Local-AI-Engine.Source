// @vitest-environment jsdom

// The dialog owns the two fields that are not part of the graph document. What it owes is that a submit carries a
// trimmed name and a null (not an empty) description, and that the server's own bounds are enforced here rather than
// discovered as a 400 after the graph has already been built.

import { fireEvent, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowDefinitionMetaDialog } from "@/features/graphWorkflows/components/GraphWorkflowDefinitionMetaDialog";
import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

type SubmitHandler = (values: { name: string; description: string | null; sampleId: string | null }) => void;

const samples = en.pages.graphWorkflows.samples;

function renderDialog(onSubmit: SubmitHandler, initial?: { name: string; description?: string | null }, offerSamples = false) {
	return renderWithProviders(
		<GraphWorkflowDefinitionMetaDialog
			opened={true}
			initial={initial}
			offerSamples={offerSamples}
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

	it("accepts a name at the server's 200-character bound", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "n".repeat(200) } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(onSubmit).toHaveBeenCalledWith({ name: "n".repeat(200), description: null, sampleId: null });
	});

	it("refuses a name past the server's 200-character bound", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		// `maxLength` stops typing past the bound in a browser; a paste or an autofill still gets through, so the
		// schema is what actually refuses it.
		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "n".repeat(201) } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(screen.getByText("Use at most 200 characters.")).toBeTruthy();
		expect(onSubmit).not.toHaveBeenCalled();
	});

	it("submits a trimmed name and a null description when none was typed", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "  Nightly triage  " } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(onSubmit).toHaveBeenCalledWith({ name: "Nightly triage", description: null, sampleId: null });
	});

	it("submits the description when one was typed", () => {
		const onSubmit = vi.fn<SubmitHandler>();
		renderDialog(onSubmit);

		fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "Release notes" } });
		fireEvent.change(screen.getByTestId("gw-definition-meta-description"), { target: { value: " Drafts the notes " } });
		fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

		expect(onSubmit).toHaveBeenCalledWith({ name: "Release notes", description: "Drafts the notes", sampleId: null });
	});

	describe("Start from", () => {
		function pickSample(label: string): void {
			fireEvent.click(screen.getByTestId("gw-definition-meta-sample"));
			const listbox = screen.getByRole("listbox", { name: samples.startFromLabel, hidden: true });
			fireEvent.click(within(listbox).getByRole("option", { name: label, hidden: true }));
		}

		it("is not offered when the dialog renames or saves as", () => {
			renderDialog(vi.fn(), { name: "Nightly triage" });

			expect(screen.queryByTestId("gw-definition-meta-sample")).toBeNull();
		});

		it("offers a blank workflow and every sample when creating", () => {
			renderDialog(vi.fn(), undefined, true);

			fireEvent.click(screen.getByTestId("gw-definition-meta-sample"));
			const listbox = screen.getByRole("listbox", { name: samples.startFromLabel, hidden: true });
			const options = within(listbox)
				.getAllByRole("option", { hidden: true })
				.map((option) => option.textContent);

			expect(options).toEqual([
				samples.blank,
				samples.summarizeWithApproval.name,
				samples.triageAndRoute.name,
				samples.parallelPerspectives.name,
				samples.briefWithReviewLoop.name,
			]);
		});

		it("prefills name and description from the picked sample and submits its id", () => {
			const onSubmit = vi.fn<SubmitHandler>();
			renderDialog(onSubmit, undefined, true);

			pickSample(samples.triageAndRoute.name);

			expect((screen.getByTestId("gw-definition-meta-name") as HTMLInputElement).value).toBe(samples.triageAndRoute.name);
			expect((screen.getByTestId("gw-definition-meta-description") as HTMLTextAreaElement).value).toBe(
				samples.triageAndRoute.description,
			);
			fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));
			expect(onSubmit).toHaveBeenCalledWith({
				name: samples.triageAndRoute.name,
				description: samples.triageAndRoute.description,
				sampleId: "triage-and-route",
			});
		});

		it("replaces a prefilled name on a second pick but keeps a name the operator typed", () => {
			renderDialog(vi.fn(), undefined, true);

			pickSample(samples.triageAndRoute.name);
			pickSample(samples.parallelPerspectives.name);
			expect((screen.getByTestId("gw-definition-meta-name") as HTMLInputElement).value).toBe(samples.parallelPerspectives.name);

			fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "Board prep" } });
			pickSample(samples.summarizeWithApproval.name);
			expect((screen.getByTestId("gw-definition-meta-name") as HTMLInputElement).value).toBe("Board prep");
			expect((screen.getByTestId("gw-definition-meta-description") as HTMLTextAreaElement).value).toBe(
				samples.summarizeWithApproval.description,
			);
		});

		it("clears the prefill and submits no sample when switched back to blank", () => {
			const onSubmit = vi.fn<SubmitHandler>();
			renderDialog(onSubmit, undefined, true);

			pickSample(samples.triageAndRoute.name);
			pickSample(samples.blank);
			fireEvent.change(screen.getByTestId("gw-definition-meta-name"), { target: { value: "Mine" } });
			fireEvent.click(screen.getByTestId("gw-definition-meta-submit"));

			expect(onSubmit).toHaveBeenCalledWith({ name: "Mine", description: null, sampleId: null });
		});
	});
});
