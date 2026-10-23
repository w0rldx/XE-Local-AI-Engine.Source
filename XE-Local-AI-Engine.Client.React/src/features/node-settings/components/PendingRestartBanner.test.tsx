// @vitest-environment jsdom

import { focusManager, type QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { mocks } = vi.hoisted(() => ({
	mocks: {
		nodeInfo: vi.fn(),
		nodeSettings: vi.fn(),
		restart: vi.fn(),
		authStatus: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	getNodeSettingsOptions: () => ({ queryKey: [{ _id: "getNodeSettings" }], queryFn: mocks.nodeSettings }),
	getNodeInfoOptions: () => ({ queryKey: ["getNodeInfo"], queryFn: mocks.nodeInfo }),
}));

vi.mock("@/core/api/generated/sdk.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/sdk.gen")>()),
	nodeAuthStatus: mocks.authStatus,
	restartNode: mocks.restart,
}));

import { PendingRestartBanner } from "@/features/node-settings/components/PendingRestartBanner";
import { createTestQueryClient, renderWithProviders } from "@/test/RenderWithProviders";

const nodeSettingsKey = [{ _id: "getNodeSettings" }];

interface Host {
	readonly isLocalMode: boolean;
	readonly isShellOwned: boolean;
}

const browserHost: Host = { isLocalMode: false, isShellOwned: false };
const desktopShellHost: Host = { isLocalMode: true, isShellOwned: true };

function renderBanner(pendingRestartFields: string[], host: Host): QueryClient {
	mocks.nodeInfo.mockResolvedValue(host);
	mocks.nodeSettings.mockResolvedValue({ pendingRestartFields });
	const queryClient = createTestQueryClient();
	// The layout guard primes this query; the banner's mount refetch answers the same.
	queryClient.setQueryData(nodeSettingsKey, { pendingRestartFields });
	renderWithProviders(<PendingRestartBanner />, { queryClient });
	return queryClient;
}

describe("PendingRestartBanner", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	afterEach(() => {
		cleanup();
		vi.restoreAllMocks();
		focusManager.setFocused(undefined);
	});

	it("renders nothing when no saved setting waits for a restart", () => {
		renderBanner([], desktopShellHost);

		expect(screen.queryByTestId("pending-restart-banner")).toBeNull();
		expect(mocks.nodeInfo).not.toHaveBeenCalled();
	});

	it("shows a change saved elsewhere once the window regains focus", async () => {
		renderBanner([], browserHost);
		await waitFor(() => expect(mocks.nodeSettings).toHaveBeenCalled());
		expect(screen.queryByTestId("pending-restart-banner")).toBeNull();

		// Saved through MCP or another browser: nothing pushes it, the focus refetch picks it up.
		mocks.nodeSettings.mockResolvedValue({ pendingRestartFields: ["kvCacheType"] });
		act(() => {
			focusManager.setFocused(false);
			focusManager.setFocused(true);
		});

		expect(await screen.findByText("1 setting waits for a node restart")).toBeTruthy();
	});

	it("counts the pending settings and names them with the form's labels", () => {
		renderBanner(["kvCacheType", "speculativeDraftModelName"], browserHost);

		expect(screen.getByText("2 settings wait for a node restart")).toBeTruthy();
		expect(screen.getByTestId("pending-restart-banner-fields").textContent).toBe("KV cache type, Draft model");
	});

	it("stays hidden once dismissed until another setting starts waiting", async () => {
		const queryClient = renderBanner(["kvCacheType"], browserHost);
		await waitFor(() => expect(queryClient.isFetching()).toBe(0));
		expect(screen.getByText("1 setting waits for a node restart")).toBeTruthy();

		fireEvent.click(screen.getByTestId("pending-restart-banner-dismiss"));
		expect(screen.queryByTestId("pending-restart-banner")).toBeNull();

		act(() => {
			queryClient.setQueryData(nodeSettingsKey, { pendingRestartFields: ["kvCacheType", "chatCacheReuse"] });
		});
		expect(await screen.findByText("2 settings wait for a node restart")).toBeTruthy();
	});

	it.each([
		["a browser session", browserHost],
		["a local-mode engine no desktop shell owns", { isLocalMode: true, isShellOwned: false }],
	])("tells %s to restart the node, without a button", async (_name, host) => {
		renderBanner(["kvCacheType"], host);

		await waitFor(() => expect(mocks.nodeInfo).toHaveBeenCalled());
		expect(screen.getByText("Restart the node to apply.")).toBeTruthy();
		expect(screen.queryByTestId("pending-restart-banner-restart")).toBeNull();
	});

	it("offers the restart inside the desktop app, posts it and waits for the node to come back", async () => {
		mocks.restart.mockResolvedValue(undefined);
		mocks.authStatus.mockResolvedValue({ data: { processGeneration: "gen-1" } });
		vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("node restarting"));
		renderBanner(["kvCacheType"], desktopShellHost);

		const button = await screen.findByTestId("pending-restart-banner-restart");
		expect(screen.queryByText("Restart the node to apply.")).toBeNull();
		await act(async () => {
			fireEvent.click(button);
		});

		expect(mocks.restart).toHaveBeenCalledOnce();
		expect(await screen.findByText("Restarting…")).toBeTruthy();
		expect(screen.queryByTestId("pending-restart-banner-dismiss")).toBeNull();
	});

	it("can be dismissed after the restart poll timed out", async () => {
		mocks.restart.mockResolvedValue(undefined);
		mocks.authStatus.mockResolvedValue({ data: { processGeneration: "gen-1" } });
		vi.spyOn(globalThis, "fetch").mockRejectedValue(new TypeError("node never came back"));
		renderBanner(["kvCacheType"], desktopShellHost);
		const button = await screen.findByTestId("pending-restart-banner-restart");

		vi.useFakeTimers();
		try {
			await act(async () => {
				fireEvent.click(button);
				await Promise.resolve();
			});
			// Past the poll's 60 s budget: every probe failed, so the hook reports a timeout.
			await act(async () => {
				await vi.advanceTimersByTimeAsync(62_000);
			});
			expect(screen.getByTestId("pending-restart-banner-dismiss")).toBeTruthy();

			await act(async () => {
				fireEvent.click(screen.getByTestId("pending-restart-banner-dismiss"));
			});
			expect(screen.queryByText("1 setting waits for a node restart")).toBeNull();
		} finally {
			vi.useRealTimers();
		}
	});

	it("keeps polling while the same process answers and reloads once the generation changes", async () => {
		mocks.restart.mockResolvedValue(undefined);
		// Captured before the restart, then the old process still answering, then the new one (possibly locked).
		mocks.authStatus
			.mockResolvedValueOnce({ data: { processGeneration: "gen-1" } })
			.mockResolvedValueOnce({ data: { processGeneration: "gen-1" } })
			.mockResolvedValue({ data: { processGeneration: "gen-2" } });
		const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue({ ok: true } as Response);
		const reload = vi.fn();
		vi.spyOn(window, "location", "get").mockReturnValue({ ...window.location, reload });
		renderBanner(["kvCacheType"], desktopShellHost);
		const button = await screen.findByTestId("pending-restart-banner-restart");

		vi.useFakeTimers();
		try {
			await act(async () => {
				fireEvent.click(button);
				await Promise.resolve();
			});
			await act(async () => {
				await vi.advanceTimersByTimeAsync(2000);
			});
			expect(fetchSpy).toHaveBeenCalledWith("/api/local/v1/auth/status", { cache: "no-store" });
			expect(reload).not.toHaveBeenCalled();
			expect(screen.getByText("Restarting…")).toBeTruthy();

			await act(async () => {
				await vi.advanceTimersByTimeAsync(2000);
			});
			expect(reload).toHaveBeenCalledOnce();
		} finally {
			vi.useRealTimers();
		}
	});
});
