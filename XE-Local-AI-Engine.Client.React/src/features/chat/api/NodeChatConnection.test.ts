import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const signalRMock = vi.hoisted(() => {
	const handlers = {
		reconnecting: undefined as ((error?: Error) => void) | undefined,
		reconnected: undefined as ((connectionId?: string) => void) | undefined,
		close: undefined as ((error?: Error) => void) | undefined,
	};
	const connection = {
		state: "Disconnected" as string,
		connectionId: undefined as string | undefined,
		start: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
		stream: vi.fn(),
		onreconnecting: vi.fn((cb: (error?: Error) => void) => {
			handlers.reconnecting = cb;
		}),
		onreconnected: vi.fn((cb: (connectionId?: string) => void) => {
			handlers.reconnected = cb;
		}),
		onclose: vi.fn((cb: (error?: Error) => void) => {
			handlers.close = cb;
		}),
	};
	const builder = {
		withUrl: vi.fn(),
		withAutomaticReconnect: vi.fn(),
		configureLogging: vi.fn(),
		build: vi.fn(),
	};
	builder.withUrl.mockReturnValue(builder);
	builder.withAutomaticReconnect.mockReturnValue(builder);
	builder.configureLogging.mockReturnValue(builder);
	builder.build.mockReturnValue(connection);

	return { builder, connection, handlers };
});

const refreshMock = vi.hoisted(() => vi.fn());

vi.mock("@microsoft/signalr", () => ({
	HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
		return signalRMock.builder;
	}),
	HubConnectionState: {
		Disconnected: "Disconnected",
		Connecting: "Connecting",
		Connected: "Connected",
		Disconnecting: "Disconnecting",
		Reconnecting: "Reconnecting",
	},
	LogLevel: { Warning: 3 },
}));

vi.mock("@/core/auth/api/NodeAuthApi", () => ({
	refreshNodeAuthToken: refreshMock,
}));

import type { useNodeAuthStore as UseNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";

// vi.resetModules() gives each test a fresh connection singleton; the auth store must come
// from the same reset module graph so the connection's accessTokenFactory reads the token we set.
async function loadModules(): Promise<{
	nodeChatConnection: typeof import("@/features/chat/api/NodeChatConnection").nodeChatConnection;
	useNodeAuthStore: typeof UseNodeAuthStore;
}> {
	const connectionModule = await import("@/features/chat/api/NodeChatConnection");
	const authModule = await import("@/core/auth/stores/NodeAuthStore");
	return { nodeChatConnection: connectionModule.nodeChatConnection, useNodeAuthStore: authModule.useNodeAuthStore };
}

const farFuture = new Date(Date.now() + 60 * 60 * 1000).toISOString();

describe("nodeChatConnection", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		vi.resetModules();
		signalRMock.builder.withUrl.mockReturnValue(signalRMock.builder);
		signalRMock.builder.withAutomaticReconnect.mockReturnValue(signalRMock.builder);
		signalRMock.builder.configureLogging.mockReturnValue(signalRMock.builder);
		signalRMock.builder.build.mockReturnValue(signalRMock.connection);
		signalRMock.connection.state = "Disconnected";
		signalRMock.connection.connectionId = undefined;
		signalRMock.connection.start.mockResolvedValue(undefined);
		signalRMock.handlers.reconnecting = undefined;
		signalRMock.handlers.reconnected = undefined;
		signalRMock.handlers.close = undefined;
		refreshMock.mockReset();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it("builds a single connection with automatic reconnect and reuses it", async () => {
		const { nodeChatConnection } = await loadModules();
		signalRMock.connection.start.mockImplementation(async () => {
			signalRMock.connection.state = "Connected";
		});

		const first = await nodeChatConnection.ensureConnection();
		const second = await nodeChatConnection.ensureConnection();

		expect(first).toBe(second);
		expect(signalRMock.builder.build).toHaveBeenCalledTimes(1);
		expect(signalRMock.connection.start).toHaveBeenCalledTimes(1);
		expect(signalRMock.builder.withAutomaticReconnect).toHaveBeenCalledWith([0, 2_000, 5_000, 10_000, 30_000]);
		expect(signalRMock.connection.onreconnecting).toHaveBeenCalled();
		expect(signalRMock.connection.onreconnected).toHaveBeenCalled();
		expect(signalRMock.connection.onclose).toHaveBeenCalled();
	});

	it("uses an existing valid token without refreshing", async () => {
		const { nodeChatConnection, useNodeAuthStore } = await loadModules();
		signalRMock.connection.start.mockImplementation(async () => {
			signalRMock.connection.state = "Connected";
		});
		useNodeAuthStore.getState().actions.setToken({ accessToken: "valid-token", expiresAtUtc: farFuture });

		await nodeChatConnection.ensureConnection();
		const factory = signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory as () => Promise<string>;

		await expect(factory()).resolves.toBe("valid-token");
		expect(refreshMock).not.toHaveBeenCalled();
	});

	it("renews an expired token through the refresh endpoint", async () => {
		const { nodeChatConnection, useNodeAuthStore } = await loadModules();
		signalRMock.connection.start.mockImplementation(async () => {
			signalRMock.connection.state = "Connected";
		});
		useNodeAuthStore.getState().actions.setToken({ accessToken: "stale", expiresAtUtc: "2020-01-01T00:00:00Z" });
		refreshMock.mockResolvedValue({ accessToken: "fresh-token", expiresAtUtc: farFuture });

		await nodeChatConnection.ensureConnection();
		const factory = signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory as () => Promise<string>;

		await expect(factory()).resolves.toBe("fresh-token");
		expect(refreshMock).toHaveBeenCalledTimes(1);
		expect(useNodeAuthStore.getState().accessToken).toBe("fresh-token");
	});

	it("retries the initial start with backoff when the first attempt fails", async () => {
		vi.useFakeTimers();
		const { nodeChatConnection } = await loadModules();
		let attempts = 0;
		signalRMock.connection.start.mockImplementation(async () => {
			attempts += 1;
			if (attempts < 3) {
				throw new Error("negotiate failed");
			}
			signalRMock.connection.state = "Connected";
		});

		const ensurePromise = nodeChatConnection.ensureConnection();
		await vi.runAllTimersAsync();
		await ensurePromise;

		expect(signalRMock.connection.start).toHaveBeenCalledTimes(3);
	});

	it("reports status and connection id through subscribers on reconnect", async () => {
		const { nodeChatConnection } = await loadModules();
		signalRMock.connection.start.mockImplementation(async () => {
			signalRMock.connection.state = "Connected";
		});
		const statuses: string[] = [];
		let reconnectedId: string | undefined;
		nodeChatConnection.subscribe({
			onStatusChange: (status) => statuses.push(status),
			onReconnected: (id) => {
				reconnectedId = id;
			},
		});

		await nodeChatConnection.ensureConnection();
		signalRMock.handlers.reconnecting?.(new Error("dropped"));
		signalRMock.handlers.reconnected?.("conn-2");

		expect(statuses).toContain("connecting");
		expect(statuses).toContain("connected");
		expect(statuses).toContain("reconnecting");
		expect(reconnectedId).toBe("conn-2");
	});
});
