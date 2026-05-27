import { beforeEach, describe, expect, it, vi } from "vitest";

interface StreamSubscriber<T> {
	next(value: T): void;
	error(error: unknown): void;
	complete(): void;
}

interface ConnectionListener {
	onReconnected?: (connectionId: string | undefined) => void;
	onClose?: (error: Error | undefined) => void;
	onStatusChange?: (status: string) => void;
	onReconnecting?: (error: Error | undefined) => void;
}

const connectionMock = vi.hoisted(() => {
	const state = {
		currentSubscriber: undefined as StreamSubscriber<unknown> | undefined,
		lastMethod: undefined as string | undefined,
		lastPayload: undefined as unknown,
		lastArgs: [] as unknown[],
		status: "connected" as string,
		listeners: new Set<ConnectionListener>(),
	};
	const subscription = { dispose: vi.fn() };
	const connection = {
		stream: vi.fn((method: string, ...args: unknown[]) => {
			state.lastMethod = method;
			state.lastPayload = args[0];
			state.lastArgs = args;
			return {
				subscribe: vi.fn((subscriber: StreamSubscriber<unknown>) => {
					state.currentSubscriber = subscriber;
					return subscription;
				}),
			};
		}),
	};
	const nodeChatConnection = {
		ensureConnection: vi.fn<() => Promise<typeof connection>>().mockResolvedValue(connection),
		current: vi.fn(() => connection),
		subscribe: vi.fn((listener: ConnectionListener) => {
			state.listeners.add(listener);
			return () => state.listeners.delete(listener);
		}),
		get status() {
			return state.status;
		},
	};

	return { connection, subscription, state, nodeChatConnection };
});

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: connectionMock.nodeChatConnection,
}));

import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

const streamRequest = {
	conversationId: "conversation-1",
	content: "hello",
	userMessageId: "user-1",
	messageId: "assistant-1",
	requestId: "request-1",
};

function streamEvent(overrides: Partial<NodeChatStreamEventDto> = {}): NodeChatStreamEventDto {
	return {
		type: "assistant-delta",
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 1,
		occurredAtUtc: 1_700_000_001_000,
		delta: "hi",
		content: "hi",
		...overrides,
	};
}

async function settle(): Promise<void> {
	await Promise.resolve();
	await Promise.resolve();
	await Promise.resolve();
}

describe("nodeChatAdapter SignalR streaming", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		connectionMock.state.currentSubscriber = undefined;
		connectionMock.state.lastMethod = undefined;
		connectionMock.state.lastPayload = undefined;
		connectionMock.state.lastArgs = [];
		connectionMock.state.status = "connected";
		connectionMock.state.listeners.clear();
		connectionMock.nodeChatConnection.ensureConnection.mockResolvedValue(connectionMock.connection);
		connectionMock.nodeChatConnection.current.mockReturnValue(connectionMock.connection);
		connectionMock.connection.stream.mockImplementation((method: string, ...args: unknown[]) => {
			connectionMock.state.lastMethod = method;
			connectionMock.state.lastPayload = args[0];
			connectionMock.state.lastArgs = args;
			return {
				subscribe: vi.fn((subscriber: StreamSubscriber<unknown>) => {
					connectionMock.state.currentSubscriber = subscriber;
					return connectionMock.subscription;
				}),
			};
		});
	});

	it("streams over the shared persistent connection", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(connectionMock.nodeChatConnection.ensureConnection).toHaveBeenCalledTimes(1);
		expect(connectionMock.state.lastMethod).toBe("SendMessage");

		connectionMock.state.currentSubscriber?.next(streamEvent());
		await expect(first).resolves.toEqual({ value: streamEvent(), done: false });

		const completed = iterator.next();
		connectionMock.state.currentSubscriber?.complete();
		await expect(completed).resolves.toMatchObject({ done: true });
		expect(connectionMock.subscription.dispose).toHaveBeenCalled();
	});

	it("threads the selected reasoning effort into the SendMessage stream request", async () => {
		nodeChatAdapter.sendMessage({ ...streamRequest, reasoningEffort: "none" }, new AbortController().signal)[Symbol.asyncIterator]().next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect(connectionMock.state.lastPayload).toMatchObject({ reasoningEffort: "none" });
	});

	it("forwards useLocalTools on the SendMessage stream payload", async () => {
		nodeChatAdapter.sendMessage({ ...streamRequest, useLocalTools: true }, new AbortController().signal)[Symbol.asyncIterator]().next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect(connectionMock.state.lastPayload).toMatchObject({ useLocalTools: true });
	});

	it("resumes via ResumeMessage after a reconnect and remaps the message id", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		// First delta over the original SendMessage stream.
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, content: "hi", delta: "hi" }));
		await expect(first).resolves.toMatchObject({ value: { content: "hi" }, done: false });

		// Connection drops mid-stream while reconnecting; the subscription errors but the send must not fail.
		connectionMock.state.status = "reconnecting";
		connectionMock.state.currentSubscriber?.error(new Error("connection lost"));

		// Reconnected with a new id -> adapter re-attaches via ResumeMessage(invocationId == requestId).
		connectionMock.state.status = "connected";
		for (const listener of connectionMock.state.listeners) {
			listener.onReconnected?.("connection-2");
		}
		await settle();

		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");
		expect(connectionMock.state.lastPayload).toBe("request-1");

		// The resume registry stamps the invocation id as the message id; the adapter remaps it back.
		const resumed = iterator.next();
		connectionMock.state.currentSubscriber?.next(streamEvent({ messageId: "request-1", sequence: 2, content: "hi there", delta: " there" }));
		await expect(resumed).resolves.toMatchObject({ value: { messageId: "assistant-1", content: "hi there" }, done: false });

		const done = iterator.next();
		connectionMock.state.currentSubscriber?.next(streamEvent({ type: "assistant-completed", messageId: "request-1", sequence: 3, status: "completed", content: "hi there" }));
		await settle();
		connectionMock.state.currentSubscriber?.complete();
		await expect(done).resolves.toMatchObject({ value: { type: "assistant-completed", messageId: "assistant-1" } });
	});

	it("completes cleanly (no error) when ResumeMessage throws an unknown/terminal invocation", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, content: "hi", delta: "hi" }));
		await expect(first).resolves.toMatchObject({ value: { content: "hi" }, done: false });

		// Drop + reconnect -> adapter re-attaches via ResumeMessage.
		connectionMock.state.status = "reconnecting";
		connectionMock.state.currentSubscriber?.error(new Error("connection lost"));
		connectionMock.state.status = "connected";
		for (const listener of connectionMock.state.listeners) {
			listener.onReconnected?.("connection-2");
		}
		await settle();
		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");

		// The registry throws because the invocation already finished server-side. The stream must end
		// cleanly (done, no rejection) so the caller refetches the persisted conversation.
		const ended = iterator.next();
		connectionMock.state.currentSubscriber?.error(new Error("Invocation request-1 is not resumable."));
		await expect(ended).resolves.toMatchObject({ done: true });
	});

	it("fails the send when the connection closes without reconnecting before completion", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		connectionMock.state.status = "disconnected";
		for (const listener of connectionMock.state.listeners) {
			listener.onClose?.(new Error("closed"));
		}

		await expect(pending).rejects.toThrow("closed");
	});

	it("streams a regenerate over RegenerateMessage and yields the server-minted variant", async () => {
		const iterator = nodeChatAdapter
			.regenerateMessage("conversation-1", "assistant-1", "high", new AbortController().signal)
			[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("RegenerateMessage");
		// Hub args are (conversationId, originalMessageId, reasoningEffort): the current reasoning selection
		// must travel as the third positional arg so regenerate honors it like a send.
		expect(connectionMock.state.lastArgs).toEqual(["conversation-1", "assistant-1", "high"]);
		expect(connectionMock.state.lastPayload).toBe("conversation-1");

		// The server mints a fresh variant id + requestId; the adapter surfaces it unchanged on the fresh stream.
		const variantEvent = streamEvent({ messageId: "variant-9", requestId: "request-9" });
		connectionMock.state.currentSubscriber?.next(variantEvent);
		await expect(first).resolves.toEqual({ value: variantEvent, done: false });
	});

	it("resumes a regenerate via ResumeMessage using the invocation id latched from the first event", async () => {
		const iterator = nodeChatAdapter
			.regenerateMessage("conversation-1", "assistant-1", "medium", new AbortController().signal)
			[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		// Latch the server-minted variant id + requestId from the first event.
		connectionMock.state.currentSubscriber?.next(streamEvent({ messageId: "variant-9", requestId: "request-9", sequence: 1, content: "draft", delta: "draft" }));
		await expect(first).resolves.toMatchObject({ value: { messageId: "variant-9", content: "draft" }, done: false });

		// Drop + reconnect -> adapter re-attaches via ResumeMessage keyed by the latched requestId.
		connectionMock.state.status = "reconnecting";
		connectionMock.state.currentSubscriber?.error(new Error("connection lost"));
		connectionMock.state.status = "connected";
		for (const listener of connectionMock.state.listeners) {
			listener.onReconnected?.("connection-2");
		}
		await settle();

		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");
		expect(connectionMock.state.lastPayload).toBe("request-9");

		// Resume events stamp the invocation id as the message id; the adapter remaps to the latched variant id.
		const resumed = iterator.next();
		connectionMock.state.currentSubscriber?.next(streamEvent({ messageId: "request-9", sequence: 2, content: "draft done", delta: " done" }));
		await expect(resumed).resolves.toMatchObject({ value: { messageId: "variant-9", content: "draft done" }, done: false });
	});

	it("disposes the SignalR subscription when the send aborts", async () => {
		const abortController = new AbortController();
		const iterator = nodeChatAdapter.sendMessage(streamRequest, abortController.signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		abortController.abort();

		await expect(pending).resolves.toMatchObject({ done: true });
		expect(connectionMock.subscription.dispose).toHaveBeenCalled();
	});
});
