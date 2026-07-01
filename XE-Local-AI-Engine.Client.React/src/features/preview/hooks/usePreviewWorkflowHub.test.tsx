// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { previewHubEvents } from "@/features/preview/models/PreviewWorkflowModels";
import { usePreviewWorkflowHub } from "@/features/preview/hooks/usePreviewWorkflowHub";
import { usePreviewRunStore } from "@/features/preview/stores/PreviewRunStore";

// Captured event handlers registered via connection.on, so a test can drive a server push by name.
const registeredHandlers = new Map<string, (payload: unknown) => void>();
const stopSpy = vi.fn(() => Promise.resolve());
const startSpy = vi.fn(() => Promise.resolve());
const invokeSpy = vi.fn(() => Promise.resolve());
// The hook registers a reconnected callback; capture it so a test can simulate a transient drop + reconnect.
let reconnectedCallback: (() => void) | undefined;
// Drives the mock connection's reported state — Disconnected until start resolves, so the hook only invokes group
// joins on a Connected hub (mirrors the production guard).
let connectionState = "Disconnected";

// Mock the SignalR client so the hook builds a fake connection: `on` captures handlers, `start`/`stop` resolve,
// `invoke` is spied, and `state`/`onreconnected` model the connect lifecycle the subscribe logic guards on.
vi.mock("@microsoft/signalr", () => {
	class FakeBuilder {
		withUrl() {
			return this;
		}
		withAutomaticReconnect() {
			return this;
		}
		configureLogging() {
			return this;
		}
		build() {
			return {
				get state() {
					return connectionState;
				},
				on: (name: string, handler: (payload: unknown) => void) => registeredHandlers.set(name, handler),
				off: (name: string) => registeredHandlers.delete(name),
				onreconnected: (callback: () => void) => {
					reconnectedCallback = callback;
				},
				start: () => {
					connectionState = "Connected";
					return startSpy();
				},
				stop: stopSpy,
				invoke: invokeSpy,
			};
		}
	}
	return {
		HubConnectionBuilder: FakeBuilder,
		HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
		LogLevel: { Warning: 3 },
	};
});

vi.mock("@/core/auth/stores/NodeAuthStore", () => ({
	useNodeAuthStore: { getState: () => ({ accessToken: "token" }) },
}));

function emitNode(runId: string, nodeId: string, eventType: string, output: string): void {
	const handler = registeredHandlers.get(eventType);
	handler?.({ eventType, runId, nodeId, output, error: null, occurredAtUtc: 1, seq: 0 });
}

describe("usePreviewWorkflowHub", () => {
	beforeEach(() => {
		registeredHandlers.clear();
		startSpy.mockClear();
		stopSpy.mockClear();
		invokeSpy.mockClear();
		reconnectedCallback = undefined;
		connectionState = "Disconnected";
		usePreviewRunStore.getState().actions.reset();
	});

	afterEach(() => {
		usePreviewRunStore.getState().actions.reset();
	});

	it("applies a pushed node event by runId into the store", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		renderHook(() => usePreviewWorkflowHub());

		await waitFor(() => expect(registeredHandlers.has(previewHubEvents.nodeOutput)).toBe(true));
		emitNode("run-1", "agent-1", previewHubEvents.nodeOutput, "streamed");

		expect(usePreviewRunStore.getState().runs["run-1"]?.nodes["agent-1"]?.output).toBe("streamed");
	});

	it("ignores a pushed event for a foreign runId the tab did not register", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		renderHook(() => usePreviewWorkflowHub());

		await waitFor(() => expect(registeredHandlers.has(previewHubEvents.nodeOutput)).toBe(true));
		emitNode("run-foreign", "agent-1", previewHubEvents.nodeOutput, "leak");

		expect(usePreviewRunStore.getState().runs["run-foreign"]).toBeUndefined();
	});

	it("stops the connection on unmount and clears run state when the page resets", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		const { unmount } = renderHook(() => usePreviewWorkflowHub());

		await waitFor(() => expect(startSpy).toHaveBeenCalled());
		unmount();

		// The hook defers stop() to the start promise; await a tick so the .finally fires.
		await waitFor(() => expect(stopSpy).toHaveBeenCalled());

		// The PAGE owns reset-on-unmount (the store reset), simulated here — after it, the store is empty.
		usePreviewRunStore.getState().actions.reset();
		expect(usePreviewRunStore.getState().runs).toEqual({});
	});

	it("subscribes to a run that was already active when the connection started", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		renderHook(() => usePreviewWorkflowHub());

		// After start resolves, the hook re-applies the desired set and joins the active run's group.
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "run-1"));
	});

	it("subscribes to a run that becomes active after the connection started", async () => {
		renderHook(() => usePreviewWorkflowHub());
		await waitFor(() => expect(startSpy).toHaveBeenCalled());

		// A new run registered while connected triggers a Subscribe via the store subscription.
		usePreviewRunStore.getState().actions.registerRun("run-2");
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "run-2"));
	});

	it("unsubscribes from a run when it is removed from the store", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		renderHook(() => usePreviewWorkflowHub());
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "run-1"));

		// Clearing the store (page reset) leaves the run's group.
		usePreviewRunStore.getState().actions.reset();
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Unsubscribe", "run-1"));
	});

	it("re-subscribes to active runs after a reconnect", async () => {
		usePreviewRunStore.getState().actions.registerRun("run-1");
		renderHook(() => usePreviewWorkflowHub());
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "run-1"));
		invokeSpy.mockClear();

		// A transient drop + automatic reconnect loses server-side group membership; the hook re-joins every active run.
		reconnectedCallback?.();
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "run-1"));
	});
});
