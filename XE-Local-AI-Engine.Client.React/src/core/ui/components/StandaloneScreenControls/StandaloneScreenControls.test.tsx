// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { StandaloneScreenControls } from "@/core/ui/components/StandaloneScreenControls/StandaloneScreenControls";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("StandaloneScreenControls", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders the theme toggle and the language menu", () => {
		renderWithProviders(<StandaloneScreenControls />);

		expect(screen.getByRole("button", { name: "Toggle color scheme" })).toBeTruthy();
		expect(screen.getByRole("button", { name: "Change language" })).toBeTruthy();
	});

	// Pinned top-right, the controls covered the centered title on a phone and on a phone in landscape.
	it.each([
		{ width: 390, height: 844, inFlow: "true" },
		{ width: 844, height: 390, inFlow: "true" },
		{ width: 1280, height: 720, inFlow: "false" },
	])("sits in the flow on a compact viewport ($width x $height -> $inFlow)", ({ width, height, inFlow }) => {
		Object.defineProperty(globalThis, "innerWidth", { configurable: true, value: width });
		Object.defineProperty(globalThis, "innerHeight", { configurable: true, value: height });

		renderWithProviders(<StandaloneScreenControls />);

		expect(screen.getByTestId("standalone-screen-controls").dataset["inFlow"]).toBe(inFlow);
	});
});
