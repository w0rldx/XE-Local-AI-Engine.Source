// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NetworkError } from "@/core/api/errors/NetworkError";
import type { NodeChatConnectionEvents, NodeChatConnectionStatus } from "@/features/chat/api/NodeChatConnection";
import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import { useNodeChatConnectionReadiness } from "@/features/chat/api/useNodeChatConnectionReadiness";

vi.mock("@/features/chat/api/NodeChatConnection", () => {
	const manager = {
		currentStatus: "disconnected" as NodeChatConnectionStatus,
		listener: undefined as NodeChatConnectionEvents | undefined,
		get status(): NodeChatConnectionStatus {
			return manager.currentStatus;
		},
		subscribe: vi.fn((events: NodeChatConnectionEvents) => {
			manager.listener = events;
			return () => {
				manager.listener = undefined;
			};
		}),
		ensureConnection: vi.fn(() => Promise.resolve(undefined)),
	};
	return { nodeChatConnection: manager };
});

interface MockManager {
	currentStatus: NodeChatConnectionStatus;
	listener: NodeChatConnectionEvents | undefined;
	subscribe: ReturnType<typeof vi.fn>;
	ensureConnection: ReturnType<typeof vi.fn>;
}

const manager = nodeChatConnection as unknown as MockManager;

function emitStatus(status: NodeChatConnectionStatus): void {
	manager.currentStatus = status;
	act(() => manager.listener?.onStatusChange?.(status));
}

describe("useNodeChatConnectionReadiness", () => {
	beforeEach(() => {
		manager.currentStatus = "disconnected";
		manager.listener = undefined;
		manager.subscribe.mockClear();
		manager.ensureConnection.mockReset();
		manager.ensureConnection.mockReturnValue(Promise.resolve(undefined));
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("starts connecting and eager-connects the hub on mount", () => {
		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		expect(result.current.readiness).toBe("connecting");
		expect(manager.ensureConnection).toHaveBeenCalledTimes(1);
	});

	it("becomes ready when the hub reports connected", () => {
		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		emitStatus("connected");

		expect(result.current.readiness).toBe("ready");
		expect(result.current.error).toBeUndefined();
	});

	it("starts ready when the hub is already connected", () => {
		manager.currentStatus = "connected";

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		expect(result.current.readiness).toBe("ready");
		expect(manager.ensureConnection).not.toHaveBeenCalled();
	});

	it("surfaces an error when the initial connection fails", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject(new Error("hub down")));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		await waitFor(() => expect(result.current.readiness).toBe("error"));
		expect(result.current.error).toBe("hub down");
	});

	it("does not downgrade once connected when a transient reconnect occurs", () => {
		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		emitStatus("connected");
		emitStatus("reconnecting");

		expect(result.current.readiness).toBe("ready");
	});

	it("re-attempts the connection on retry", async () => {
		manager.ensureConnection.mockReturnValueOnce(Promise.reject(new Error("hub down")));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());
		await waitFor(() => expect(result.current.readiness).toBe("error"));

		act(() => result.current.retry());

		expect(manager.ensureConnection).toHaveBeenCalledTimes(2);
		expect(result.current.readiness).toBe("connecting");
	});

	it("clears the error and becomes ready when the subscriber fires connected after an error", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject(new Error("hub down")));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());
		await waitFor(() => expect(result.current.readiness).toBe("error"));

		emitStatus("connected");

		expect(result.current.readiness).toBe("ready");
		expect(result.current.error).toBeUndefined();
	});

	it("retry skips ensureConnection and immediately shows ready when already connected via subscriber", async () => {
		manager.ensureConnection.mockReturnValueOnce(Promise.reject(new Error("hub down")));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());
		await waitFor(() => expect(result.current.readiness).toBe("error"));

		// Hub reconnects on its own before the user clicks Retry.
		emitStatus("connected");
		expect(result.current.readiness).toBe("ready");

		// Clicking Retry should be a cheap no-op: hasConnectedRef is latched, no ensureConnection call.
		act(() => result.current.retry());

		expect(manager.ensureConnection).toHaveBeenCalledTimes(1);
		expect(result.current.readiness).toBe("ready");
	});

	it("formats a plain-string error without wrapping it", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject("plain string error"));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		await waitFor(() => expect(result.current.readiness).toBe("error"));
		expect(result.current.error).toBe("plain string error");
	});

	it("falls back to a generic message for unknown error shapes", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject({ code: 42 }));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		await waitFor(() => expect(result.current.readiness).toBe("error"));
		expect(result.current.error).toBe("Unable to connect to the local chat hub.");
	});

	// The chat gate renders this as `connectionError ?? localizedFallback`, and an empty string satisfies `??` — so an
	// Error with no message produced a BLANK "Local chat unavailable" panel on the app's main screen. NetworkError is
	// the app's canonical instance of that shape (its message is deliberately empty); the hub's own start failures do
	// not travel through axios, so what this pins is the shape, not that specific type's route to here.
	it("falls back to the generic message for an Error carrying no message", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject(new NetworkError()));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		await waitFor(() => expect(result.current.readiness).toBe("error"));
		expect(result.current.error).toBe("Unable to connect to the local chat hub.");
	});

	it("falls back to the generic message for a blank string rejection", async () => {
		manager.ensureConnection.mockReturnValue(Promise.reject("   "));

		const { result } = renderHook(() => useNodeChatConnectionReadiness());

		await waitFor(() => expect(result.current.readiness).toBe("error"));
		expect(result.current.error).toBe("Unable to connect to the local chat hub.");
	});
});
