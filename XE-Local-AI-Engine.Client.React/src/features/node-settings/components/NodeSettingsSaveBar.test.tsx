// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { NodeSettingsSaveBar } from "@/features/node-settings/components/NodeSettingsSaveBar";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderBar(compact: boolean): void {
	renderWithProviders(
		<NodeSettingsSaveBar
			unsavedCount={0}
			restartCount={0}
			canSave={false}
			canReset={false}
			isSaving={false}
			onSave={vi.fn()}
			onReset={vi.fn()}
			compact={compact}
		/>,
	);
}

// The bar's row container decides whether the status shares a line with the buttons. A shared nowrap row squeezed a
// long German status to a few letters on a phone, so compact mode puts the status on its own line above the buttons.
describe("NodeSettingsSaveBar", () => {
	afterEach(() => cleanup());

	it("puts the status beside the buttons on a wide viewport", () => {
		renderBar(false);

		const status = screen.getByTestId("node-settings-save-bar-status");
		const saveButtonRow = screen.getByTestId("node-settings-save-button").parentElement;
		expect(saveButtonRow?.parentElement).toBe(status.parentElement);
		expect(status.parentElement?.className).toContain("mantine-Group-root");
	});

	it("stacks the status above the buttons when compact", () => {
		renderBar(true);

		const status = screen.getByTestId("node-settings-save-bar-status");
		const saveButtonRow = screen.getByTestId("node-settings-save-button").parentElement;
		expect(status.parentElement?.className).toContain("mantine-Stack-root");
		expect(status.nextElementSibling).toBe(saveButtonRow);
	});
});
