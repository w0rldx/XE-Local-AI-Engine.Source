// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { t } from "i18next";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// ONE shared connection per hub path, as the real manager hands out, with the server's group membership modelled: a
// Subscribe joins the connection to the id's group, an Unsubscribe leaves it, and a group event reaches the connection's
// handlers only while it is a member. That is the per-connection rule two consumers of one connection share.
const server = vi.hoisted(() => {
	const members = new Set<string>();
	const handlers = new Map<string, Set<(payload: unknown) => void>>();
	const reconnected = new Set<() => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((name: string, handler: (payload: unknown) => void) => {
			handlers.set(name, (handlers.get(name) ?? new Set()).add(handler));
		}),
		off: vi.fn((name: string, handler: (payload: unknown) => void) => handlers.get(name)?.delete(handler)),
		invoke: vi.fn(async (method: string, id: string) => {
			if (method === "Subscribe") {
				members.add(id);
			} else if (method === "Unsubscribe") {
				members.delete(id);
			}
		}),
	};
	const acquire = vi.fn(() => ({
		connection,
		whenStarted: Promise.resolve(),
		onReconnected: (callback: () => void) => {
			reconnected.add(callback);
			return () => reconnected.delete(callback);
		},
		onReconnecting: () => () => undefined,
		onClosed: () => () => undefined,
		release: vi.fn(),
	}));
	/** A group event for `id`: delivered only while this connection is in the id's group. */
	const emit = (name: string, id: string, payload: unknown): void => {
		if (members.has(id)) {
			for (const handler of handlers.get(name) ?? []) {
				handler(payload);
			}
		}
	};
	/** A reconnect: the server forgot every membership, and the manager fans `onReconnected` out. */
	const reconnect = (): void => {
		members.clear();
		for (const callback of [...reconnected]) {
			callback();
		}
	};
	return { members, connection, acquire, emit, reconnect };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: server.acquire }));

import { listTrainingRunsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { useRunStreamSubscription } from "@/core/api/signalr/useRunStreamSubscription";
import { useTrainingRunOperations } from "@/core/operations/hooks/useJobOperations";

// The feed's active-only read.
const trainingKey = listTrainingRunsOptions({ query: { page: 1, pageSize: 200, activeOnly: true } }).queryKey;
let queryClient: QueryClient;

function wrapper({ children }: { children: ReactNode }) {
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

beforeEach(() => {
	server.members.clear();
	server.connection.invoke.mockClear();
	queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY } } });
	queryClient.setQueryData(trainingKey, {
		items: [{ id: "run-1", status: "Training" }],
		totalCount: 1,
		page: 1,
		pageSize: 25,
	} as never);
});

afterEach(cleanup);

function renderFeedAndPage() {
	const feed = renderHook(() => useTrainingRunOperations({ enabled: true, watch: true, t }), { wrapper });
	const page = renderHook(
		() => useRunStreamSubscription("run-1", { onEvent: vi.fn(), onResync: vi.fn(), onLiveChange: vi.fn() }),
		{ wrapper },
	);
	return { feed, page };
}

const stateEvent = { runId: "run-1", sequence: 1, kind: "State", payload: { state: "Succeeded" } };

describe("shared hub groups", () => {
	it("keeps the feed in a run's group when the run page that shares its connection closes", async () => {
		const { page } = renderFeedAndPage();
		await waitFor(() => expect(server.members.has("run-1")).toBe(true));
		await waitFor(() => expect(server.connection.invoke.mock.calls.filter(([method]) => method === "Subscribe")).toHaveLength(2));

		page.unmount();
		act(() => server.emit("trainingRun.event", "run-1", stateEvent));

		expect(server.connection.invoke).not.toHaveBeenCalledWith("Unsubscribe", "run-1");
		expect(queryClient.getQueryState(trainingKey)?.isInvalidated).toBe(true);
	});

	it("leaves the group once its last holder leaves", async () => {
		const { feed, page } = renderFeedAndPage();
		await waitFor(() => expect(server.members.has("run-1")).toBe(true));

		page.unmount();
		feed.unmount();

		expect(server.connection.invoke.mock.calls.filter(([method]) => method === "Unsubscribe")).toEqual([
			["Unsubscribe", "run-1"],
		]);
		expect(server.members.has("run-1")).toBe(false);
	});

	it("re-joins every held group after a reconnect, each holder with its own subscribe", async () => {
		renderFeedAndPage();
		await waitFor(() => expect(server.members.has("run-1")).toBe(true));
		server.connection.invoke.mockClear();

		act(() => server.reconnect());

		await waitFor(() => expect(server.members.has("run-1")).toBe(true));
		const subscribes = server.connection.invoke.mock.calls.filter(([method]) => method === "Subscribe");
		// The page resumes from its cursor (0 here), the feed asks for live events only.
		expect(subscribes).toEqual(
			expect.arrayContaining([
				["Subscribe", "run-1", 0],
				["Subscribe", "run-1", Number.MAX_SAFE_INTEGER],
			]),
		);
	});
});
