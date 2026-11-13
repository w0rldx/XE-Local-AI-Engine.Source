// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import type { NodeChatConnectionStatus } from "@/features/chat/api/NodeChatConnection";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const { hub, listeners, ensureConnection, current, state } = vi.hoisted(() => {
	const hub = { on: vi.fn(), off: vi.fn() };
	return {
		hub,
		listeners: [] as { onStatusChange?: (status: NodeChatConnectionStatus) => void }[],
		ensureConnection: vi.fn(async () => hub),
		current: vi.fn(() => hub),
		state: { status: "connected" as NodeChatConnectionStatus },
	};
});

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: {
		get status() {
			return state.status;
		},
		ensureConnection,
		current,
		subscribe: vi.fn((events: { onStatusChange?: (status: NodeChatConnectionStatus) => void }) => {
			listeners.push(events);
			return () => undefined;
		}),
	},
}));

import { CONVERSATIONS_CHANGED, useConversationListPush } from "@/features/chat/hooks/useConversationListPush";

const listKey = { queryKey: nodeChatQueryKeys.conversationLists() };

function renderPush(statusAtMount: NodeChatConnectionStatus) {
	state.status = statusAtMount;
	const queryClient = new QueryClient();
	const invalidate = vi.spyOn(queryClient, "invalidateQueries");
	const { wrapper } = createProvidersWrapper({ queryClient });
	const { unmount } = renderHook(() => useConversationListPush(), { wrapper });
	const emit = (status: NodeChatConnectionStatus): void => listeners.at(-1)?.onStatusChange?.(status);
	return { invalidate, unmount, emit };
}

describe("useConversationListPush", () => {
	afterEach(() => {
		vi.clearAllMocks();
		listeners.length = 0;
	});

	it("refreshes the list on the node's push and after a dropped connection recovers, and stops listening on unmount", async () => {
		const { invalidate, unmount, emit } = renderPush("connected");

		await waitFor(() => expect(hub.on).toHaveBeenCalledWith(CONVERSATIONS_CHANGED, expect.any(Function)));
		const handler = hub.on.mock.calls[0]?.[1] as () => void;
		handler();
		expect(invalidate).toHaveBeenLastCalledWith(listKey);

		invalidate.mockClear();
		emit("reconnecting");
		emit("connected");
		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith(listKey);
		expect(hub.on).toHaveBeenCalledTimes(1);

		unmount();
		expect(hub.off).toHaveBeenCalledWith(CONVERSATIONS_CHANGED, handler);
	});

	it("attaches once to a connection a later send starts after the first connect failed, and refreshes the list once", async () => {
		ensureConnection.mockRejectedValueOnce(new Error("Unable to start the local chat connection."));
		const { invalidate, emit } = renderPush("disconnected");
		await waitFor(() => expect(ensureConnection).toHaveBeenCalled());
		emit("disconnected");
		expect(hub.on).not.toHaveBeenCalled();

		// A send starts the shared connection: the manager reports "connected", never a reconnect.
		emit("connecting");
		emit("connected");
		emit("connected");

		expect(hub.on).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith(listKey);
	});

	it("refreshes once when the first start succeeds after a retry", async () => {
		let resolveStart: (connection: typeof hub) => void = () => undefined;
		ensureConnection.mockImplementationOnce(
			() =>
				new Promise((resolve) => {
					resolveStart = resolve;
				}),
		);
		const { invalidate, emit } = renderPush("disconnected");

		// startWithRetry reports "connecting" per attempt, then "connected"; ensureConnection resolves after it.
		emit("connecting");
		emit("connecting");
		emit("connected");
		await act(async () => resolveStart(hub));

		expect(hub.on).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith(listKey);
	});

	it("refreshes once when the first start from not-connected succeeds at once", async () => {
		const { invalidate } = renderPush("disconnected");

		await waitFor(() => expect(hub.on).toHaveBeenCalledTimes(1));

		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith(listKey);
	});

	it("does not refresh on a mount onto an open connection", async () => {
		const { invalidate } = renderPush("connected");

		await waitFor(() => expect(hub.on).toHaveBeenCalledTimes(1));

		expect(invalidate).not.toHaveBeenCalled();
	});
});
