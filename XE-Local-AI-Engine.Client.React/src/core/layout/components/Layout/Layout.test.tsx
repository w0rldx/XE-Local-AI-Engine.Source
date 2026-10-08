// @vitest-environment jsdom

import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from "@tanstack/react-router";
import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { Layout } from "@/core/layout/components/Layout/Layout";
import en from "@/locales/en.json";
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
// The page-crash fallback reads stored snapshots and node info; neither matters for where it renders.
vi.mock("@/features/diagnostics/UseSnapshots", () => ({ useSnapshots: () => ({ data: [] }) }));
vi.mock("@/features/diagnostics/queries/useNodeInfo", () => ({ useNodeInfo: () => ({ data: undefined }) }));

function ThrowingPage(): never {
	throw new Error("page exploded");
}

// The real `/_layout` shape: a pathless layout route rendering Layout, with a page under it.
function renderLayoutWithCrashingPage(): void {
	const rootRoute = createRootRoute({ component: Outlet });
	const layoutRoute = createRoute({ getParentRoute: () => rootRoute, id: "_layout", component: Layout });
	const pageRoute = createRoute({ getParentRoute: () => layoutRoute, path: "/", component: ThrowingPage });
	const router = createRouter({
		routeTree: rootRoute.addChildren([layoutRoute.addChildren([pageRoute])]),
		history: createMemoryHistory({ initialEntries: ["/"] }),
	});
	// biome-ignore lint/suspicious/noExplicitAny: a locally-built route tree cannot satisfy the app's registered Router type.
	renderWithProviders(<RouterProvider router={router as any} />);
}

describe("Layout", () => {
	afterEach(() => {
		cleanup();
		vi.restoreAllMocks();
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

	// Without a boundary inside the shell, the root route's error component replaced the navigation too, and on the
	// desktop WebView (no back button, no URL bar) Retry re-rendered the same crash.
	it("keeps the navigation when a page crashes and shows the fallback inside the main landmark", async () => {
		vi.spyOn(console, "error").mockImplementation(() => undefined);
		renderLayoutWithCrashingPage();

		const goHome = await screen.findByRole("button", { name: en.app.errorFallback.goHome });
		expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeTruthy();
		expect(screen.getByRole("main").contains(goHome)).toBe(true);
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
