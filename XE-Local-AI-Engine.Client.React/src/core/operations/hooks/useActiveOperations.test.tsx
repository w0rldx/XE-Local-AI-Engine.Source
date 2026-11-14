// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const acquire = vi.hoisted(() =>
	vi.fn(() => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() },
		whenStarted: Promise.resolve(),
		onReconnected: () => () => undefined,
		onReconnecting: () => () => undefined,
		onClosed: () => () => undefined,
		release: vi.fn(),
	})),
);

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: acquire }));
// The GGUF and runtime sources have their own tests and hubs; here only the job sources' gating is under test.
vi.mock("@/core/model-downloads/queries/useGgufAcquisitions", () => ({ useActiveGgufAcquisitions: () => new Map() }));
vi.mock("@/core/runtime-acquisition/hooks/useRuntimeAcquisitionHub", () => ({ useRuntimeAcquisitionHub: () => undefined }));

import { getNodeSettingsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useOperations } from "@/core/operations/hooks/useActiveOperations";

let queryClient: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

function leasedPaths(): unknown[] {
	return acquire.mock.calls.map((call) => (call as unknown[])[0]);
}

beforeEach(() => {
	acquire.mockClear();
	// Empty cached lists and settings: the hooks read them and nothing goes to the network.
	queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY, queryFn: () => ({ items: [] }) } },
	});
	useNodeAuthStore.setState({ accessToken: "test-access-token" });
});

afterEach(() => {
	cleanup();
	useNodeAuthStore.setState({ accessToken: undefined });
});

describe("useOperations hub gating", () => {
	it("leases nothing before sign-in", () => {
		useNodeAuthStore.setState({ accessToken: undefined });

		renderHook(() => useOperations({ watch: true }), { wrapper });

		expect(acquire).not.toHaveBeenCalled();
	});

	it("leases the always-on push hubs only for the watching caller", () => {
		queryClient.setQueryData(getNodeSettingsOptions().queryKey, { schedulerEnabled: true } as never);

		renderHook(() => useOperations(), { wrapper });
		expect(acquire).not.toHaveBeenCalled();

		renderHook(() => useOperations({ watch: true }), { wrapper });
		// images/hub twice: image jobs and image model downloads share its one connection.
		expect(leasedPaths().toSorted()).toEqual([
			"images/hub",
			"images/hub",
			"knowledge-base/hub",
			"scheduler/hub",
			"training/datasets/hub",
			"training/runs/hub",
		]);
	});

	it("leases the transcription hub for model downloads only while the node's transcription switch is on", () => {
		queryClient.setQueryData(getNodeSettingsOptions().queryKey, { transcriptionEnabled: false } as never);
		renderHook(() => useOperations({ watch: true }), { wrapper });
		expect(leasedPaths()).not.toContain("transcription/hub");

		queryClient.setQueryData(getNodeSettingsOptions().queryKey, { transcriptionEnabled: true } as never);
		renderHook(() => useOperations({ watch: true }), { wrapper });
		expect(leasedPaths()).toContain("transcription/hub");
	});

	it("leaves the scheduler hub alone while the node's scheduler switch is off or not yet known", () => {
		renderHook(() => useOperations({ watch: true }), { wrapper });
		expect(leasedPaths()).not.toContain("scheduler/hub");

		queryClient.setQueryData(getNodeSettingsOptions().queryKey, { schedulerEnabled: false } as never);
		renderHook(() => useOperations({ watch: true }), { wrapper });
		expect(leasedPaths()).not.toContain("scheduler/hub");
		expect(leasedPaths()).toContain("knowledge-base/hub");
	});
});
