// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("MobileNavigationBar", () => {
	afterEach(() => {
		cleanup();
	});

	// The drawer has no visible title (its header is the logo), so without a label it announced as a bare "dialog".
	it("names the navigation drawer", async () => {
		renderWithProviders(
			<MobileNavigationBar
				drawerOpen={true}
				setDrawerOpen={vi.fn()}
				drawerActions={{ reportProblem: vi.fn(), reportProblemPending: false, openAbout: vi.fn() }}
			/>,
			{ route: "/" },
		);

		expect(await screen.findByRole("dialog", { name: "Main navigation" })).toBeTruthy();
	});
});
