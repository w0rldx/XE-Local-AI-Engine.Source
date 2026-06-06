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

// Mock the SignalR client so the hook builds a fake connection: `on` captures handlers, `start`/`stop` resolve.
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
				on: (name: string, handler: (payload: unknown) => void) => registeredHandlers.set(name, handler),
				off: (name: string) => registeredHandlers.delete(name),
				onreconnected: () => undefined,
				start: startSpy,
				stop: stopSpy,
			};
		}
	}
	return { HubConnectionBuilder: FakeBuilder, LogLevel: { Warning: 3 } };
});

vi.mock("@/core/auth/stores/NodeAuthStore", () => ({
	useNodeAuthStore: { getState: () => ({ accessToken: "token" }) },
}));

function emitNode(runId: string, nodeId: string, eventType: string, output: string): void {
	const handler = registeredHandlers.get(eventType);
	handler?.({ eventType, runId, nodeId, output, error: null, occurredAtUtc: 1 });
}

describe("usePreviewWorkflowHub", () => {
	beforeEach(() => {
		registeredHandlers.clear();
		startSpy.mockClear();
		stopSpy.mockClear();
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
});
