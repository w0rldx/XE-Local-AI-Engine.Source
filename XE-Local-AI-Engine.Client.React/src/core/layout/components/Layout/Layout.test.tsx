// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { Layout } from "@/core/layout/components/Layout/Layout";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Layout's job here is the page skeleton: the landmark, the skip link and the scroll container. Its children are
// full features that each pull in SignalR hubs, queries and stores; stubbing them keeps this a test of the
// skeleton rather than of everything mounted inside it.
vi.mock("@/core/layout/components/HeaderBar/HeaderBar", () => ({ HeaderBar: () => <div>header</div> }));
vi.mock("@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar", () => ({
	DesktopNavigationBar: () => <nav aria-label="Main navigation" />,
}));
vi.mock("@/features/chat/components/ChatConnectionStatusChip", () => ({ ChatConnectionStatusChip: () => null }));
vi.mock("@/features/model-fit/components/CpuFallbackBanner", () => ({ CpuFallbackBanner: () => null }));
vi.mock("@/features/node-settings/components/LlamaCppUpdateBanner", () => ({ LlamaCppUpdateBanner: () => null }));
vi.mock("@/features/node-settings/components/RuntimeAcquisitionBanner", () => ({ RuntimeAcquisitionBanner: () => null }));

describe("Layout", () => {
	afterEach(() => {
		cleanup();
	});

	// The routed content had no landmark at all, so a screen reader's "jump to main" found nothing and every
	// region of the page read as generic. One <main> serves both layouts — the desktop rail and the mobile
	// drawer render around this same element.
	it("renders the routed content inside a focusable main landmark", async () => {
		renderWithProviders(<Layout />, { route: "/" });

		const main = await screen.findByRole("main");
		expect(main.id).toBe("main-content");
		// Focusable on purpose: the skip link has to move the caret, not just the scroll position.
		expect(main.getAttribute("tabindex")).toBe("-1");
		// The scroll container moved onto <main>; losing these would silently break page scrolling.
		expect(main.className).toContain("overflow-y-auto");
		expect(main.className).toContain("min-h-0");
	});

	// The link must come FIRST in the tab order, which is the whole point: a keyboard user reaching it after the
	// sidebar has already tabbed through everything it was meant to skip.
	it("puts a skip-to-content link ahead of everything else and points it at the landmark", async () => {
		const { container } = renderWithProviders(<Layout />, { route: "/" });

		const skipLink = await screen.findByRole("link", { name: "Skip to main content" });
		expect(skipLink.getAttribute("href")).toBe("#main-content");

		const focusable = container.querySelectorAll("a[href], button, main[tabindex]");
		expect(focusable[0]).toBe(skipLink);
	});
});
