import { beforeEach, describe, expect, it, vi } from "vitest";

interface StreamSubscriber<T> {
	next(value: T): void;
	error(error: unknown): void;
	complete(): void;
}

const signalRMock = vi.hoisted(() => {
	const connection = {
		start: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
		stop: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
		stream: vi.fn(),
	};
	const subscription = {
		dispose: vi.fn(),
	};
	const builder = {
		withUrl: vi.fn(),
		configureLogging: vi.fn(),
		build: vi.fn(),
	};
	const state = {
		currentSubscriber: undefined as StreamSubscriber<unknown> | undefined,
	};

	builder.withUrl.mockReturnValue(builder);
	builder.configureLogging.mockReturnValue(builder);
	builder.build.mockReturnValue(connection);
	connection.stream.mockReturnValue({
		subscribe: vi.fn((subscriber: StreamSubscriber<unknown>) => {
			state.currentSubscriber = subscriber;
			return subscription;
		}),
	});

	return { builder, connection, subscription, state };
});

vi.mock("@microsoft/signalr", () => ({
	HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
		return signalRMock.builder;
	}),
	LogLevel: { Warning: 3 },
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { streamRuntimeLogs, type RuntimeLogLineDto } from "@/features/runtime-manager/api/RuntimeLogStream";

const logLine: RuntimeLogLineDto = {
	containerName: "ollama",
	stream: "stdout",
	line: "ready",
	observedAt: "2026-05-24T12:00:00Z",
};

async function settle(): Promise<void> {
	await Promise.resolve();
	await Promise.resolve();
}

describe("RuntimeLogStream log streaming", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
		signalRMock.state.currentSubscriber = undefined;
		signalRMock.builder.withUrl.mockReturnValue(signalRMock.builder);
		signalRMock.builder.configureLogging.mockReturnValue(signalRMock.builder);
		signalRMock.builder.build.mockReturnValue(signalRMock.connection);
		signalRMock.connection.start.mockResolvedValue(undefined);
		signalRMock.connection.stop.mockResolvedValue(undefined);
		signalRMock.connection.stream.mockReturnValue({
			subscribe: vi.fn((subscriber: StreamSubscriber<unknown>) => {
				signalRMock.state.currentSubscriber = subscriber;
				return signalRMock.subscription;
			}),
		});
	});

	it("lets SignalR negotiate the best transport for runtime logs", async () => {
		const request = { containerName: "ollama", tailLines: 200, follow: true };
		const iterator = streamRuntimeLogs(request, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/runtime/hub"),
			expect.objectContaining({
				accessTokenFactory: expect.any(Function),
			}),
		);
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1]).not.toHaveProperty("transport");
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("");
		expect(signalRMock.connection.stream).toHaveBeenCalledWith("StreamLogs", request);

		signalRMock.state.currentSubscriber?.next(logLine);
		await expect(first).resolves.toEqual({ value: logLine, done: false });

		const completed = iterator.next();
		signalRMock.state.currentSubscriber?.complete();
		await expect(completed).resolves.toMatchObject({ done: true });
		expect(signalRMock.subscription.dispose).toHaveBeenCalled();
		expect(signalRMock.connection.stop).toHaveBeenCalled();
	});

	it("supplies the current access token to SignalR", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "runtime-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		const iterator = streamRuntimeLogs({ containerName: "ollama" }, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("runtime-token");

		signalRMock.state.currentSubscriber?.complete();
		await expect(first).resolves.toMatchObject({ done: true });
	});

	it("disposes the SignalR subscription when log follow aborts", async () => {
		const abortController = new AbortController();
		const iterator = streamRuntimeLogs({ containerName: "ollama" }, abortController.signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		abortController.abort();

		await expect(pending).resolves.toMatchObject({ done: true });
		expect(signalRMock.subscription.dispose).toHaveBeenCalled();
		expect(signalRMock.connection.stop).toHaveBeenCalled();
	});
});
