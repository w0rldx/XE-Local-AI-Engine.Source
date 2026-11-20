// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
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

	function renderOpenDrawer() {
		const setDrawerOpen = vi.fn();
		renderWithProviders(
			<MobileNavigationBar
				drawerOpen={true}
				setDrawerOpen={setDrawerOpen}
				drawerActions={{ reportProblem: vi.fn(), reportProblemPending: false, openAbout: vi.fn() }}
			/>,
			{ route: "/" },
		);
		return setDrawerOpen;
	}

	it("closes on Escape when no sub-panel is open", async () => {
		const setDrawerOpen = renderOpenDrawer();
		await screen.findByRole("dialog", { name: "Main navigation" });

		fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });

		expect(setDrawerOpen).toHaveBeenCalledWith(false);
	});

	// Both Drawers listen for Escape on the window; without the hand-off one key press closed the whole menu and focus
	// had nowhere sensible to go.
	it("closes only the open sub-panel on Escape and returns focus to its opener", async () => {
		const setDrawerOpen = renderOpenDrawer();
		const opener = await screen.findByRole("button", { name: "Theme" });
		opener.focus();
		fireEvent.click(opener);
		const panel = await screen.findByRole("dialog", { name: "Theme" });
		await waitFor(() => expect(panel.contains(document.activeElement)).toBe(true));

		fireEvent.keyDown(document.activeElement as Element, { key: "Escape" });

		await waitFor(() => expect(screen.queryByRole("dialog", { name: "Theme" })).toBeNull());
		expect(screen.getByRole("dialog", { name: "Main navigation" })).toBeTruthy();
		expect(setDrawerOpen).not.toHaveBeenCalled();
		await waitFor(() => expect(document.activeElement).toBe(opener));

		// Back on the parent, Escape is the parent's again.
		fireEvent.keyDown(document.activeElement ?? document.body, { key: "Escape" });
		expect(setDrawerOpen).toHaveBeenCalledWith(false);
	});
});
