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

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

const streamRequest = {
	conversationId: "conversation-1",
	content: "hello",
	userMessageId: "user-1",
	messageId: "assistant-1",
	requestId: "request-1",
};

// Defaults to a wire-accurate `assistant-delta`: delta plus the offset it begins at, and NO accumulated
// `content` — the full text rides only on `assistant-snapshot` and the terminals.
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
		contentOffset: 0,
		...overrides,
	};
}

// The frame a resumed stream opens with: the authoritative full text, no delta, offsets equal to its lengths.
function snapshotEvent(content: string, overrides: Partial<NodeChatStreamEventDto> = {}): NodeChatStreamEventDto {
	return streamEvent({
		type: "assistant-snapshot",
		delta: undefined,
		content,
		contentOffset: content.length,
		...overrides,
	});
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
		nodeChatAdapter
			.sendMessage({ ...streamRequest, reasoningEffort: "none" }, new AbortController().signal)
			[Symbol.asyncIterator]()
			.next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect(connectionMock.state.lastPayload).toMatchObject({ reasoningEffort: "none" });
	});

	it("forwards useLocalTools on the SendMessage stream payload", async () => {
		nodeChatAdapter
			.sendMessage({ ...streamRequest, useLocalTools: true }, new AbortController().signal)
			[Symbol.asyncIterator]()
			.next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect(connectionMock.state.lastPayload).toMatchObject({ useLocalTools: true });
	});

	it("forwards non-empty attachmentFileIds on the SendMessage stream payload", async () => {
		nodeChatAdapter
			.sendMessage({ ...streamRequest, attachmentFileIds: ["file-1", "file-2"] }, new AbortController().signal)
			[Symbol.asyncIterator]()
			.next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect(connectionMock.state.lastPayload).toMatchObject({ attachmentFileIds: ["file-1", "file-2"] });
	});

	it("omits attachmentFileIds from the payload when there are no attachments", async () => {
		nodeChatAdapter.sendMessage({ ...streamRequest, attachmentFileIds: [] }, new AbortController().signal)[Symbol.asyncIterator]().next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("SendMessage");
		expect((connectionMock.state.lastPayload as { attachmentFileIds?: unknown }).attachmentFileIds).toBeUndefined();
	});

	it("resumes via ResumeMessage after a reconnect and remaps the message id", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		// First delta over the original SendMessage stream. A delta carries only its delta plus the offset it
		// begins at — never the accumulated content.
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, delta: "hi", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "hi" }, done: false });

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

		// The resume registry stamps the invocation id as the message id AND restarts its sequence numbering at
		// zero (it cannot see the original stream's counter). The adapter remaps the message id back and rebases
		// the sequences past the delivered high-water mark — without the rebase the dedupe guard drops every
		// resumed event (terminal included) as a stale duplicate and the message sticks with no error.
		const resumed = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("hi there", { messageId: "request-1", sequence: 0 }));
		await expect(resumed).resolves.toMatchObject({
			value: { messageId: "assistant-1", content: "hi there", sequence: 2 },
			done: false,
		});

		const done = iterator.next();
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ type: "assistant-completed", messageId: "request-1", sequence: 1, status: "completed", content: "hi there" }),
		);
		await settle();
		connectionMock.state.currentSubscriber?.complete();
		await expect(done).resolves.toMatchObject({
			value: { type: "assistant-completed", messageId: "assistant-1", sequence: 3 },
		});
	});

	it("delivers a second resume after another reconnect with sequences rebased again", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "a", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		// First drop: resume delivers its zero-based events rebased to 1, 2, ...
		connectionMock.state.status = "reconnecting";
		connectionMock.state.currentSubscriber?.error(new Error("connection lost"));
		connectionMock.state.status = "connected";
		for (const listener of connectionMock.state.listeners) {
			listener.onReconnected?.("connection-2");
		}
		await settle();
		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");

		const second = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("ab", { messageId: "request-1", sequence: 0 }));
		await expect(second).resolves.toMatchObject({ value: { sequence: 1, content: "ab" }, done: false });

		// Second drop: the next resume restarts at zero again and must rebase past the new high-water mark.
		connectionMock.state.status = "reconnecting";
		connectionMock.state.currentSubscriber?.error(new Error("connection lost again"));
		connectionMock.state.status = "connected";
		for (const listener of connectionMock.state.listeners) {
			listener.onReconnected?.("connection-3");
		}
		await settle();

		const third = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("abc", { messageId: "request-1", sequence: 0 }));
		await expect(third).resolves.toMatchObject({ value: { sequence: 2, content: "abc" }, done: false });

		const done = iterator.next();
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ type: "assistant-completed", messageId: "request-1", sequence: 1, status: "completed", content: "abc" }),
		);
		await settle();
		connectionMock.state.currentSubscriber?.complete();
		await expect(done).resolves.toMatchObject({ value: { type: "assistant-completed", sequence: 3 } });
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

	it("fails the send immediately when the subscription errors while the connection is still connected", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		// A hub/application error thrown during turn setup: no terminal event, invocation id known,
		// not aborted, and the connection is still stably "connected" (no transport drop). This is NOT
		// recoverable via resume, so the stream must reject instead of hanging until the watchdog trips.
		expect(connectionMock.state.status).toBe("connected");
		connectionMock.state.currentSubscriber?.error(new Error("model failed to load"));

		await expect(pending).rejects.toThrow("model failed to load");
	});

	it("streams a regenerate over RegenerateMessage and yields the server-minted variant", async () => {
		const iterator = nodeChatAdapter
			.regenerateMessage("conversation-1", "assistant-1", "high", true, true, undefined, undefined, new AbortController().signal)
			[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(connectionMock.state.lastMethod).toBe("RegenerateMessage");
		// Hub args are (conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath,
		// samplingOptions): the current reasoning, local-tools, knowledge-base, conversation-tree, and developer-mode
		// sampling selections all travel as positional args so regenerate honors them like a send. An absent selection
		// map or sampling block is sent as null.
		expect(connectionMock.state.lastArgs).toEqual(["conversation-1", "assistant-1", "high", true, true, null, null]);
		expect(connectionMock.state.lastPayload).toBe("conversation-1");

		// The server mints a fresh variant id + requestId; the adapter surfaces it unchanged on the fresh stream.
		const variantEvent = streamEvent({ messageId: "variant-9", requestId: "request-9" });
		connectionMock.state.currentSubscriber?.next(variantEvent);
		await expect(first).resolves.toEqual({ value: variantEvent, done: false });
	});

	it("carries the developer-mode sampling overrides as the trailing regenerate hub arg", async () => {
		// G2: the per-send sampling overrides were dropped on regenerate — the rerun silently ignored the knobs the
		// original send used.
		const iterator = nodeChatAdapter
			.regenerateMessage(
				"conversation-1",
				"assistant-1",
				"high",
				false,
				false,
				{ "group-1": "variant-1" },
				{ temperature: 0.7, seed: "1234" },
				new AbortController().signal,
			)
			[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		expect(connectionMock.state.lastArgs).toEqual([
			"conversation-1",
			"assistant-1",
			"high",
			false,
			false,
			{ "group-1": "variant-1" },
			{ temperature: 0.7, seed: "1234" },
		]);

		// Drain the opened stream so the test leaves no pending subscription behind.
		const variantEvent = streamEvent({ messageId: "variant-9", requestId: "request-9" });
		connectionMock.state.currentSubscriber?.next(variantEvent);
		await expect(first).resolves.toEqual({ value: variantEvent, done: false });
	});

	it("resumes a regenerate via ResumeMessage using the invocation id latched from the first event", async () => {
		const iterator = nodeChatAdapter
			.regenerateMessage("conversation-1", "assistant-1", "medium", false, false, undefined, undefined, new AbortController().signal)
			[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		// Latch the server-minted variant id + requestId from the first event.
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ messageId: "variant-9", requestId: "request-9", sequence: 1, delta: "draft", contentOffset: 0 }),
		);
		await expect(first).resolves.toMatchObject({ value: { messageId: "variant-9", delta: "draft" }, done: false });

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

		// Resume events stamp the invocation id as the message id and restart their numbering at zero; the
		// adapter remaps to the latched variant id and rebases the sequence past the delivered high-water mark.
		const resumed = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("draft done", { messageId: "request-9", sequence: 0 }));
		await expect(resumed).resolves.toMatchObject({
			value: { messageId: "variant-9", content: "draft done", sequence: 2 },
			done: false,
		});
	});

	it("forwards a contiguous run of deltas without re-subscribing", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "ab", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "ab" }, done: false });

		const second = iterator.next();
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, delta: "cd", contentOffset: 2 }));
		await expect(second).resolves.toMatchObject({ value: { delta: "cd" }, done: false });

		const third = iterator.next();
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 2, delta: "ef", contentOffset: 4 }));
		await expect(third).resolves.toMatchObject({ value: { delta: "ef" }, done: false });

		// Each offset continued exactly where the previous delta ended, so nothing was lost and the original
		// subscription is still the only one open.
		expect(connectionMock.connection.stream).toHaveBeenCalledTimes(1);
		expect(connectionMock.state.lastMethod).toBe("SendMessage");
	});

	it("re-enters via ResumeMessage when a delta's offset skips ahead, and never forwards the gapped frame", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "ab", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "ab" }, done: false });

		// A frame that begins at character 5 when the client holds only 2: a frame was lost, so appending this
		// delta would silently corrupt the turn. Only a snapshot can repair it.
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, delta: "cd", contentOffset: 5 }));
		await settle();

		expect(connectionMock.connection.stream).toHaveBeenCalledTimes(2);
		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");
		expect(connectionMock.state.lastPayload).toBe("request-1");
		expect(connectionMock.subscription.dispose).toHaveBeenCalled();

		// The dropped frame's sequence was deliberately left unconsumed, so the resumed stream's restarted
		// numbering rebases straight onto it and the ordering guard never stalls on the hole. The next event the
		// caller sees is the snapshot — never the gapped delta.
		const repaired = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("abcdef", { messageId: "request-1", sequence: 0 }));
		await expect(repaired).resolves.toMatchObject({
			value: { type: "assistant-snapshot", messageId: "assistant-1", content: "abcdef", sequence: 1 },
			done: false,
		});
	});

	it("re-enters via ResumeMessage when a delta's offset overlaps the previous frame", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "ab", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "ab" }, done: false });

		// A benign duplicate replay (offset 1 when the client holds 2) takes the SAME repair path as a gap
		// rather than a partial-slice heuristic — one code path, and the snapshot settles the truth either way.
		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, delta: "b", contentOffset: 1 }));
		await settle();

		expect(connectionMock.connection.stream).toHaveBeenCalledTimes(2);
		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");
	});

	it("re-enters via ResumeMessage on a reasoning-offset mismatch as well", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(
			streamEvent({ sequence: 0, delta: "", contentOffset: 0, reasoningDelta: "think", reasoningOffset: 0 }),
		);
		await expect(first).resolves.toMatchObject({ value: { reasoningDelta: "think" }, done: false });

		// Content is still contiguous here — only the reasoning stream lost a frame, and that is equally fatal
		// to an append-only merge.
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ sequence: 1, delta: "", contentOffset: 0, reasoningDelta: "ing", reasoningOffset: 99 }),
		);
		await settle();

		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");
	});

	it("consumes an assistant-reconcile without forwarding it and re-enters via ResumeMessage", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "ab", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "ab" }, done: false });

		// The server could not enqueue an event and is asking the client to resynchronize. It is an adapter
		// instruction, not a message mutation, so it must never reach the stream reducer.
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ type: "assistant-reconcile", sequence: 1, delta: undefined, contentOffset: undefined }),
		);
		await settle();

		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");

		const repaired = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("abcdef", { messageId: "request-1", sequence: 0 }));
		await expect(repaired).resolves.toMatchObject({
			value: { type: "assistant-snapshot", content: "abcdef", sequence: 1 },
			done: false,
		});
	});

	it("re-bases the offsets from a resume snapshot so the following delta is not mistaken for a gap", async () => {
		const iterator = nodeChatAdapter.sendMessage(streamRequest, new AbortController().signal)[Symbol.asyncIterator]();
		const first = iterator.next();
		await settle();

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 0, delta: "ab", contentOffset: 0 }));
		await expect(first).resolves.toMatchObject({ value: { delta: "ab" }, done: false });

		connectionMock.state.currentSubscriber?.next(streamEvent({ sequence: 1, delta: "cd", contentOffset: 5 }));
		await settle();
		expect(connectionMock.state.lastMethod).toBe("ResumeMessage");

		const repaired = iterator.next();
		connectionMock.state.currentSubscriber?.next(snapshotEvent("abcdef", { messageId: "request-1", sequence: 0 }));
		await expect(repaired).resolves.toMatchObject({ value: { content: "abcdef", sequence: 1 }, done: false });

		// The snapshot carried 6 characters, so a delta continuing at 6 is contiguous — one repair total, not a
		// second one against the pre-resume position.
		const next = iterator.next();
		connectionMock.state.currentSubscriber?.next(
			streamEvent({ messageId: "request-1", sequence: 1, delta: "gh", contentOffset: 6 }),
		);
		await expect(next).resolves.toMatchObject({ value: { delta: "gh", sequence: 2 }, done: false });
		expect(connectionMock.connection.stream).toHaveBeenCalledTimes(2);
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

	it("does not subscribe when the send aborts while the connection is still being established", async () => {
		// ensureConnection resolves AFTER the abort fires: the adapter must re-check the aborted state and skip
		// subscribe(), or it opens a subscription (and a server run) the caller has already given up on.
		const abortController = new AbortController();
		let resolveConnection!: () => void;
		connectionMock.nodeChatConnection.ensureConnection.mockReturnValueOnce(
			new Promise<typeof connectionMock.connection>((resolve) => {
				resolveConnection = () => resolve(connectionMock.connection);
			}),
		);

		const iterator = nodeChatAdapter.sendMessage(streamRequest, abortController.signal)[Symbol.asyncIterator]();
		const pending = iterator.next();
		await settle();

		// Abort while ensureConnection is still pending, then let it resolve.
		abortController.abort();
		resolveConnection();
		await settle();

		await expect(pending).resolves.toMatchObject({ done: true });
		expect(connectionMock.connection.stream).not.toHaveBeenCalled();
	});
});
