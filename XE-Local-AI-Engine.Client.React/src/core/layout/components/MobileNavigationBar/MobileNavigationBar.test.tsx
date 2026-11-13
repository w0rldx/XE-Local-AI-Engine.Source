// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("MobileNavigationBar", () => {
	afterEach(() => {
		cleanup();
	});

	// The drawer is the only navigation on a phone, so External Apps has to carry the same experimental badge the
	// desktop rail shows (ADR 0010 amendment 2026-11-12), on that entry alone.
	it("marks the External Apps group as experimental, and only that entry", async () => {
		renderWithProviders(
			<MobileNavigationBar
				drawerOpen={true}
				setDrawerOpen={vi.fn()}
				drawerActions={{ reportProblem: vi.fn(), reportProblemPending: false, openAbout: vi.fn() }}
			/>,
			{ route: "/" },
		);

		const badge = await screen.findByText("Experimental");
		expect(badge.closest("button")?.textContent).toContain("External Apps");
		expect(screen.getAllByText("Experimental")).toHaveLength(1);
	});
});
