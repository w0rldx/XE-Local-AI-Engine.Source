// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClientProvider } from "@tanstack/react-query";
import { createMemoryHistory, createRootRoute, createRoute, createRouter, Outlet, RouterProvider } from "@tanstack/react-router";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AppLayout } from "@/AppLayout";
// Loaded up front, so the lazy About resolves at once and opens while the drawer is still leaving.
import "@/features/about/components/AboutDialog/AboutDialog";
import { installJsdomEnvironmentMocks } from "@/test/MantineTestRender";
import { createTestQueryClient } from "@/test/RenderWithProviders";

// The real header, drawer and dialog timing are under test; the shell's feature widgets are not.
vi.mock("@/core/operations/components/ActiveOperationsIndicator", () => ({ ActiveOperationsIndicator: () => null }));
vi.mock("@/core/operations/components/OperationToastCoordinator", () => ({ OperationToastCoordinator: () => null }));
vi.mock("@/features/chat/components/ChatConnectionStatusChip", () => ({ ChatConnectionStatusChip: () => null }));
vi.mock("@/features/onboarding/components/AiAcknowledgementGate", () => ({ AiAcknowledgementGate: () => null }));
vi.mock("@/core/dev-tools/components/DevelopmentUi/DevelopmentUi", () => ({ DevelopmentUi: () => null }));
vi.mock("@/features/model-fit/components/CpuFallbackBanner", () => ({ CpuFallbackBanner: () => null }));
vi.mock("@/features/node-settings/components/LlamaCppUpdateBanner", () => ({ LlamaCppUpdateBanner: () => null }));
vi.mock("@/features/node-settings/components/PendingRestartBanner", () => ({ PendingRestartBanner: () => null }));
vi.mock("@/core/layout/models/NavigationMenuData", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/layout/models/NavigationMenuData")>()),
	useServerDisabledNavigationCapabilities: () => new Set(),
}));
// A plain Mantine modal with the same focus return as the real About, without its data and tabs.
vi.mock("@/features/about/components/AboutDialog/AboutDialog", async () => {
	const { Modal } = await import("@mantine/core");
	return {
		AboutDialog: ({ opened, onClose }: { opened: boolean; onClose: () => void }) => (
			<Modal opened={opened} onClose={onClose} title="About stub">
				<button type="button" onClick={onClose}>
					Close about
				</button>
			</Modal>
		),
	};
});

// Real transitions: the drawer exits slower than About closes, so About closes while the drawer is still leaving.
const slowTheme = {
	components: {
		Drawer: { defaultProps: { transitionProps: { duration: 30, exitDuration: 400 } } },
		Modal: { defaultProps: { transitionProps: { duration: 30, exitDuration: 30 } } },
	},
};

function renderPhoneShell(): void {
	const rootRoute = createRootRoute({ component: Outlet });
	const layoutRoute = createRoute({ getParentRoute: () => rootRoute, id: "_layout", component: AppLayout });
	const pageRoute = createRoute({ getParentRoute: () => layoutRoute, path: "/", component: () => <p>page</p> });
	const router = createRouter({
		routeTree: rootRoute.addChildren([layoutRoute.addChildren([pageRoute])]),
		history: createMemoryHistory({ initialEntries: ["/"] }),
	});
	render(
		<MantineProvider env="default" theme={slowTheme}>
			<QueryClientProvider client={createTestQueryClient()}>
				{/* biome-ignore lint/suspicious/noExplicitAny: a locally-built route tree cannot satisfy the app's registered Router type. */}
				<RouterProvider router={router as any} />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("About opened from the mobile drawer", () => {
	const originalWidth = window.innerWidth;
	const originalHeight = window.innerHeight;

	beforeEach(() => {
		installJsdomEnvironmentMocks();
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: 390 });
		Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: 844 });
	});

	afterEach(() => {
		cleanup();
		Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: originalWidth });
		Object.defineProperty(window, "innerHeight", { writable: true, configurable: true, value: originalHeight });
	});

	// The drawer item that opened About is gone by the time About closes, so Mantine's focus return has nowhere to go.
	it("hands focus to the drawer toggle once About and the drawer have both closed", async () => {
		renderPhoneShell();
		const toggle = await screen.findByRole("button", { name: "Open navigation" });
		toggle.focus();
		fireEvent.click(toggle);
		const drawer = await screen.findByRole("dialog", { name: "Main navigation" });

		fireEvent.click(within(drawer).getByText("About"));
		const about = await screen.findByRole("dialog", { name: "About stub" });
		await waitFor(() => expect(about.contains(document.activeElement)).toBe(true));
		expect(drawer.isConnected).toBe(true);
		fireEvent.click(within(about).getByRole("button", { name: "Close about" }));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull(), { timeout: 2000 });
		await waitFor(() => expect(document.activeElement).toBe(toggle));
	});
});
