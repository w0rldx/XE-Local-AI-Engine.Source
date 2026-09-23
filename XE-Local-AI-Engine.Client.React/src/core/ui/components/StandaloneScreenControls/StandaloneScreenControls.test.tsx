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
});
