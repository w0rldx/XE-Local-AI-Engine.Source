// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { NodeSettingsUiModeCard } from "@/features/node-settings/components/NodeSettingsUiModeCard";
import { renderWithProviders } from "@/test/RenderWithProviders";

// SegmentedControl renders one native radio input per option, so the selected mode is the input that is `checked` —
// there is no aria-checked to read.
function checkedMode(): string | undefined {
	return ["Simple", "Advanced"].find((label) => (screen.getByRole("radio", { name: label }) as HTMLInputElement).checked);
}

// The card is a controlled draft field: the page's save bar persists it (NodeSettings.test.tsx covers the save and the
// cache seeding that re-renders the navigation rail).
describe("NodeSettingsUiModeCard", () => {
	afterEach(() => cleanup());

	it("shows the draft mode", () => {
		renderWithProviders(<NodeSettingsUiModeCard value="simple" onChange={vi.fn()} />);

		expect(checkedMode()).toBe("Simple");
	});

	it("reads an undecided node as Advanced", () => {
		renderWithProviders(<NodeSettingsUiModeCard value="" onChange={vi.fn()} />);

		expect(checkedMode()).toBe("Advanced");
	});

	it("reports a pick to the draft instead of saving it", () => {
		const onChange = vi.fn();
		renderWithProviders(<NodeSettingsUiModeCard value="advanced" onChange={onChange} />);

		fireEvent.click(screen.getByRole("radio", { name: "Simple" }));

		expect(onChange).toHaveBeenCalledWith("simple");
	});
});
