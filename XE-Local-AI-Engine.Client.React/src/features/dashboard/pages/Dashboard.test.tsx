// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse } from "@/core/api/generated";

const connectedStatus: XeLocalAiEngineClientEndpointsConnectionV1ConnectionStatusResponse = {
	state: "connected",
	lastUpdatedAt: "2026-05-24T12:00:00Z",
	isPaired: true,
	autoConnectOnStart: false,
	bindingMethod: "qr-code",
	lastKnownNodeName: "node-alpha",
	tokenExpiresAt: null,
	canConnect: false,
	canDisconnect: true,
	canEnableAutoConnect: true,
	canDisableAutoConnect: false,
};

const { generatedMock } = vi.hoisted(() => ({
	generatedMock: {
		getConnectionStatusOptions: vi.fn(),
		getConnectionStatusQueryKey: vi.fn(() => ["getConnectionStatus"]),
		connectConnectionMutation: vi.fn(),
		disconnectConnectionMutation: vi.fn(),
		enableAutoConnectMutation: vi.fn(),
		disableAutoConnectMutation: vi.fn(),
		disconnectFn: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getConnectionStatusOptions: generatedMock.getConnectionStatusOptions,
	getConnectionStatusQueryKey: generatedMock.getConnectionStatusQueryKey,
	connectConnectionMutation: generatedMock.connectConnectionMutation,
	disconnectConnectionMutation: generatedMock.disconnectConnectionMutation,
	enableAutoConnectMutation: generatedMock.enableAutoConnectMutation,
	disableAutoConnectMutation: generatedMock.disableAutoConnectMutation,
}));

import { Dashboard } from "@/features/dashboard/pages/Dashboard";

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
}

function renderPage(): void {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});

	const wrapper = ({ children }: { children: ReactNode }) => (
		<QueryClientProvider client={queryClient}>
			<MantineProvider>{children}</MantineProvider>
		</QueryClientProvider>
	);

	render(<Dashboard />, { wrapper });
}

describe("Dashboard (generated hey-api data layer)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		generatedMock.getConnectionStatusOptions.mockReturnValue({
			queryKey: ["getConnectionStatus"],
			queryFn: async () => connectedStatus,
		});
		generatedMock.disconnectFn.mockResolvedValue(connectedStatus);
		generatedMock.connectConnectionMutation.mockReturnValue({ mutationFn: vi.fn().mockResolvedValue(connectedStatus) });
		generatedMock.disconnectConnectionMutation.mockReturnValue({ mutationFn: generatedMock.disconnectFn });
		generatedMock.enableAutoConnectMutation.mockReturnValue({ mutationFn: vi.fn().mockResolvedValue(connectedStatus) });
		generatedMock.disableAutoConnectMutation.mockReturnValue({ mutationFn: vi.fn().mockResolvedValue(connectedStatus) });
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("loads connection status through the generated query options and maps the view model", async () => {
		renderPage();

		expect(generatedMock.getConnectionStatusOptions).toHaveBeenCalled();
		// The mapped view-model surfaces the node name + binding method from the (optional-field) wire response.
		expect(await screen.findByText("node-alpha")).toBeTruthy();
		expect(screen.getByText("qr-code")).toBeTruthy();
	});

	it("disconnects through the generated mutation", async () => {
		renderPage();
		await screen.findByText("node-alpha");

		// `canDisconnect` is true, so the disconnect button is enabled. Its accessible name is the i18n key
		// (no i18n provider in tests), so match the button by that key.
		fireEvent.click(screen.getByRole("button", { name: "pages.dashboard.platformConnection.disconnect" }));

		await waitFor(() => {
			expect(generatedMock.disconnectFn).toHaveBeenCalled();
		});
	});
});
