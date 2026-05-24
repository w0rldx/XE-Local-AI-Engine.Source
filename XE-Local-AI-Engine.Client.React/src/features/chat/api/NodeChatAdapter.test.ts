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
	HttpTransportType: { LongPolling: 4 },
	LogLevel: { Warning: 3 },
}));

import { localOperatorHeaderName } from "@/core/api/auth/LocalOperatorToken";
import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

const localOperatorTokenGlobalKey = "__XE_LOCAL_OPERATOR_TOKEN__";
const localGlobal = globalThis as unknown as Partial<Record<typeof localOperatorTokenGlobalKey, string>>;

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
		localGlobal[localOperatorTokenGlobalKey] = "local-token";
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

	it("uses header-authenticated long polling so the browser does not put the local token in a WebSocket query string", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(expect.stringContaining("/api/local/v1/chat/hub"), {
			headers: { [localOperatorHeaderName]: "local-token" },
			transport: 4,
		});
		expect(signalRMock.connection.stream).toHaveBeenCalledWith("SendMessage", streamRequest);

		signalRMock.state.currentSubscriber?.next(streamEvent);
		await expect(first).resolves.toEqual({ value: streamEvent, done: false });

		const completed = iterator.next();
		signalRMock.state.currentSubscriber?.complete();
		await expect(completed).resolves.toMatchObject({ done: true });
		expect(signalRMock.subscription.dispose).toHaveBeenCalled();
		expect(signalRMock.connection.stop).toHaveBeenCalled();
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
