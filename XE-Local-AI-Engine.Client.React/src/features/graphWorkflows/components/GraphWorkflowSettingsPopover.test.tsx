// @vitest-environment jsdom

// The graph-level settings are part of the saved document, so the popover's contract is what it hands the editor:
// the kind it switches to, and a chat block that only ever carries the member the operator touched.

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowSettingsPopover } from "@/features/graphWorkflows/components/GraphWorkflowSettingsPopover";
import type { GraphWorkflowGraphSettings } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function open(settings: GraphWorkflowGraphSettings, onChange = vi.fn()) {
	renderWithProviders(<GraphWorkflowSettingsPopover settings={settings} onChange={onChange} />);
	fireEvent.click(screen.getByTestId("gw-page-settings"));
	return onChange;
}

describe("GraphWorkflowSettingsPopover", () => {
	it("switches a Standard graph to Chat, and hides the chat settings until it is one", async () => {
		const onChange = open({ kind: "Standard" });

		await screen.findByTestId("gw-settings-dropdown");
		expect(screen.getByText("Workflow kind")).toBeTruthy();
		expect(screen.queryByTestId("gw-settings-accepts-attachments")).toBeNull();
		fireEvent.click(screen.getByText("Chat"));

		expect(onChange).toHaveBeenCalledWith({ kind: "Chat" });
	});

	it("shows the parser's defaults for an absent chat block and writes only the member changed", async () => {
		const onChange = open({ kind: "Chat" });

		const accepts = (await screen.findByTestId("gw-settings-accepts-attachments")) as HTMLInputElement;
		const confirmRerun = screen.getByTestId("gw-settings-require-rerun-confirmation") as HTMLInputElement;
		expect(accepts.checked).toBe(false);
		expect(confirmRerun.checked).toBe(true);
		fireEvent.click(accepts);

		expect(onChange).toHaveBeenCalledWith({ kind: "Chat", chat: { acceptsAttachments: true } });
	});
});
