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

import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

const streamRequest = {
	conversationId: "conversation-1",
	content: "hello",
	userMessageId: "user-1",
	messageId: "assistant-1",
	requestId: "request-1",
};

const streamEvent: NodeChatStreamEventDto = {
	type: "assistant-delta",
	conversationId: "conversation-1",
	messageId: "assistant-1",
	requestId: "request-1",
	status: "streaming",
	sequence: 1,
	occurredAtUtc: 1_700_000_001_000,
	delta: "hi",
	content: "hi",
};

async function settle(): Promise<void> {
	await Promise.resolve();
	await Promise.resolve();
}

describe("nodeChatAdapter SignalR streaming", () => {
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

	it("lets SignalR negotiate the best transport for streaming", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/chat/hub"),
			expect.objectContaining({
				accessTokenFactory: expect.any(Function),
			}),
		);
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1]).not.toHaveProperty("transport");
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("");
		expect(signalRMock.connection.stream).toHaveBeenCalledWith("SendMessage", streamRequest);

		signalRMock.state.currentSubscriber?.next(streamEvent);
		await expect(first).resolves.toEqual({ value: streamEvent, done: false });

		const completed = iterator.next();
		signalRMock.state.currentSubscriber?.complete();
		await expect(completed).resolves.toMatchObject({ done: true });
		expect(signalRMock.subscription.dispose).toHaveBeenCalled();
		expect(signalRMock.connection.stop).toHaveBeenCalled();
	});

	it("supplies the current access token to SignalR", async () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "access-token", expiresAtUtc: "2026-05-25T12:00:00Z" });
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("access-token");

		signalRMock.state.currentSubscriber?.complete();
		await expect(first).resolves.toMatchObject({ done: true });
	});

	it("disposes the SignalR subscription when the send aborts", async () => {
		const abortController = new AbortController();
		const iterator = nodeChatAdapter.sendMessage(streamRequest, abortController.signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		abortController.abort();

		await expect(pending).resolves.toMatchObject({ done: true });
		expect(signalRMock.subscription.dispose).toHaveBeenCalled();
		expect(signalRMock.connection.stop).toHaveBeenCalled();
	});
});
