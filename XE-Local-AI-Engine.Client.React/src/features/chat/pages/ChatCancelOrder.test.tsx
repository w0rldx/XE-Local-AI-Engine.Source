// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { act, cleanup, fireEvent, renderHook, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { StreamWatchdogError } from "@/features/chat/api/NodeChatStreamGuard";
import { nodeChatStreamEventTypes } from "@/features/chat/api/NodeChatStreamState";
import { useChatStreamController } from "@/features/chat/hooks/useChatStreamController";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";
import { createProvidersWrapper, renderWithProviders } from "@/test/RenderWithProviders";

// The no-installed-model guidance renders a TanStack-router Link to /models whenever the fixture's model list
// is empty (the default below). Stub the router module so Chat mounts without a RouterProvider (mirrors
// ChatMessage.test.tsx's ModelNotInstalled Link stub).
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
			<a href={to} {...props}>
				{children}
			</a>
		),
	};
});

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: {
		listConversations: vi.fn(),
		getConversation: vi.fn(),
		sendMessage: vi.fn(),
		regenerateMessage: vi.fn(),
		// Chat re-attaches on every conversation open; an idle conversation gets an empty stream back.
		resumeConversation: vi.fn(() => ({
			[Symbol.asyncIterator]: () => ({ next: async () => ({ done: true, value: undefined }) }),
		})),
		deleteConversation: vi.fn(),
		renameConversation: vi.fn(),
		setConversationPinned: vi.fn(),
		setConversationArchived: vi.fn(),
		branchConversation: vi.fn(),
		listMessageRevisions: vi.fn(),
		setMessageFeedback: vi.fn(),
		createConversation: vi.fn(),
		cancelMessage: vi.fn(),
		persistSelectedPath: vi.fn(),
	},
}));

const { listLocalModelsQueryFn } = vi.hoisted(() => ({
	listLocalModelsQueryFn: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	...(await import("@/test/ChatWorkflowQueryStubs")).chatWorkflowQueryStubs,
	listLocalModelsOptions: vi.fn(() => ({
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: () => listLocalModelsQueryFn(),
	})),
	getLocalModelDetailsOptions: vi.fn(() => ({
		queryKey: [{ _id: "getLocalModelDetails" }],
		queryFn: async () => ({}),
	})),
}));

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: {
		status: "connected",
		subscribe: vi.fn(() => () => undefined),
		ensureConnection: vi.fn(() => Promise.resolve(undefined)),
	},
}));

const adapter = vi.mocked(nodeChatAdapter);

function conversation(): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "Streaming thread",
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
	};
}

function deltaEvent(): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantStreaming,
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 1,
		occurredAtUtc: 1_700_000_000_000,
		delta: "partial",
		content: "partial",
	};
}

function renderChat(): { queryClient: QueryClient } {
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };

	return renderWithProviders(
		<ConfirmContext.Provider value={confirmValue}>
			<Chat />
		</ConfirmContext.Provider>,
	);
}

describe("Chat cancel ordering", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			selectedModelName: null,
			configuredDefaultModelName: null,
		});
		adapter.getConversation.mockResolvedValue(conversation());
		adapter.listConversations.mockResolvedValue({ conversations: [conversation()] });
	});

	afterEach(() => {
		cleanup();
		vi.useRealTimers();
	});

	it("aborts the local stream before issuing the server cancel request", async () => {
		let capturedSignal: AbortSignal | undefined;
		let releaseStream: (() => void) | undefined;
		const streamGate = new Promise<void>((resolve) => {
			releaseStream = resolve;
		});
		adapter.sendMessage.mockImplementation((_request, signal) => {
			capturedSignal = signal;
			signal.addEventListener("abort", () => releaseStream?.(), { once: true });
			return {
				async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
					yield deltaEvent();
					await streamGate;
				},
			};
		});

		// Record whether the local stream was already aborted at the moment the server cancel REST call fired.
		let abortedWhenCancelCalled: boolean | undefined;
		adapter.cancelMessage.mockImplementation(async () => {
			abortedWhenCancelCalled = capturedSignal?.aborted ?? false;
		});

		const { queryClient } = renderChat();

		await screen.findByTestId("conversation-item-conversation-1");
		const input = await screen.findByTestId("chat-input");
		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		// The stream is open and has delivered its first event, so the send-button is now the in-flight Stop button.
		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toBeDefined());
		expect(capturedSignal?.aborted).toBe(false);

		// Click Stop → handleCancel: it must abort the local stream FIRST, then fire the best-effort server cancel.
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.cancelMessage).toHaveBeenCalledTimes(1));
		expect(abortedWhenCancelCalled).toBe(true);
		expect(capturedSignal?.aborted).toBe(true);

		// Let the aborted iterator unwind so the streaming loop's finally block runs.
		releaseStream?.();
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalled());
	});

	it("shows Stop for a persisted live turn that no local stream owns, and Stop cancels it", async () => {
		// After a remount the resume came back empty while the server run goes on: only the persisted row says it is live.
		adapter.getConversation.mockResolvedValue({
			...conversation(),
			messages: [
				{
					id: "assistant-1",
					conversationId: "conversation-1",
					requestId: "request-1",
					role: "assistant",
					content: "partial",
					status: "streaming",
					createdAt: "2026-05-24T00:00:00.000Z",
					sortOrder: 1,
				},
			],
		});

		renderChat();

		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalled());
		const button = await screen.findByTestId("chat-send-button");
		await waitFor(() => expect(button.getAttribute("aria-label")).toBe("Stop"));

		fireEvent.click(button);

		await waitFor(() =>
			expect(adapter.cancelMessage).toHaveBeenCalledWith({
				conversationId: "conversation-1",
				messageId: "assistant-1",
				requestId: "request-1",
			}),
		);
	});

	it("re-reads a persisted live turn no local stream owns until it completes, then unblocks the composer", async () => {
		// Only the poll interval is faked; waitFor and the query client keep their real timeouts.
		vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
		let assistantRow: ChatConversationModel["messages"][number] = {
			id: "assistant-1",
			conversationId: "conversation-1",
			requestId: "request-1",
			role: "assistant",
			content: "partial",
			status: "streaming",
			createdAt: "2026-05-24T00:00:00.000Z",
			sortOrder: 1,
		};
		adapter.getConversation.mockImplementation(async () => ({ ...conversation(), messages: [assistantRow] }));

		renderChat();

		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalled());
		await waitFor(() => expect(screen.getByTestId("chat-send-button").getAttribute("aria-label")).toBe("Stop"));

		// The server finishes the turn; nothing streams that to this page.
		assistantRow = { ...assistantRow, content: "final answer", status: "completed" };
		act(() => {
			vi.advanceTimersByTime(3_000);
		});

		expect(await screen.findByText("final answer")).toBeDefined();
		await waitFor(() => expect(screen.getByTestId("chat-send-button").getAttribute("aria-label")).toBe("Send"));
		fireEvent.change(screen.getByTestId("chat-input"), { target: { value: "next question" } });
		expect((screen.getByTestId("chat-send-button") as HTMLButtonElement).disabled).toBe(false);
	});

	it("lets a slow re-read of a persisted live turn land instead of restarting it every interval", async () => {
		vi.useFakeTimers({ toFake: ["setInterval", "clearInterval"] });
		const streamingRow: ChatConversationModel["messages"][number] = {
			id: "assistant-1",
			conversationId: "conversation-1",
			requestId: "request-1",
			role: "assistant",
			content: "partial",
			status: "streaming",
			createdAt: "2026-05-24T00:00:00.000Z",
			sortOrder: 1,
		};
		adapter.getConversation.mockResolvedValueOnce({ ...conversation(), messages: [streamingRow] });

		renderChat();

		await waitFor(() => expect(screen.getByTestId("chat-send-button").getAttribute("aria-label")).toBe("Stop"));
		const readsBefore = adapter.getConversation.mock.calls.length;
		let resolveSlowRead: ((value: ChatConversationModel) => void) | undefined;
		const slowRead = new Promise<ChatConversationModel>((resolve) => {
			resolveSlowRead = resolve;
		});
		adapter.getConversation.mockImplementation(() => slowRead);

		// Four intervals pass while the first re-read is still in flight.
		for (let tick = 0; tick < 4; tick++) {
			act(() => {
				vi.advanceTimersByTime(3_000);
			});
		}
		expect(adapter.getConversation).toHaveBeenCalledTimes(readsBefore + 1);

		resolveSlowRead?.({ ...conversation(), messages: [{ ...streamingRow, content: "final answer", status: "completed" }] });

		expect(await screen.findByText("final answer")).toBeDefined();
		await waitFor(() => expect(screen.getByTestId("chat-send-button").getAttribute("aria-label")).toBe("Send"));
	});

	it("does not re-read a persisted live turn while an attached resume owns it", async () => {
		// The stream commits once per animation frame, so frames are stepped by hand alongside the poll interval.
		vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "requestAnimationFrame", "cancelAnimationFrame"] });
		adapter.getConversation.mockResolvedValue({
			...conversation(),
			messages: [
				{
					id: "assistant-1",
					conversationId: "conversation-1",
					requestId: "request-1",
					role: "assistant",
					content: "",
					status: "streaming",
					createdAt: "2026-05-24T00:00:00.000Z",
					sortOrder: 1,
				},
			],
		});
		let releaseStream: (() => void) | undefined;
		const streamGate = new Promise<void>((resolve) => {
			releaseStream = resolve;
		});
		adapter.resumeConversation.mockImplementationOnce(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield deltaEvent();
				await streamGate;
			},
		}));

		const { queryClient } = renderChat();

		// The resumed delta was committed onto the persisted row, so the resume now owns the turn.
		await waitFor(() => {
			act(() => {
				vi.advanceTimersToNextFrame();
			});
			expect(
				queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"))?.messages[0]?.updatedAt,
			).toBe(new Date(deltaEvent().occurredAtUtc).toISOString());
		});
		const readsBefore = adapter.getConversation.mock.calls.length;

		act(() => {
			vi.advanceTimersByTime(30_000);
		});

		expect(adapter.getConversation).toHaveBeenCalledTimes(readsBefore);
		releaseStream?.();
		await waitFor(() => expect(adapter.getConversation.mock.calls.length).toBeGreaterThan(readsBefore));
	});

	it("still cancels the run server-side when the local stream handle is lost", async () => {
		// The SignalR stream ended (or the view remounted) while the server kept the turn running: no local stream owns
		// it, but the persisted row is still live. Stop must reach the server with the row's persisted request id.
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } });
		queryClient.setQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"), {
			...conversation(),
			messages: [
				{
					id: "assistant-1",
					conversationId: "conversation-1",
					requestId: "request-1",
					role: "assistant",
					content: "partial",
					status: "streaming",
					createdAt: "2026-05-24T00:00:00.000Z",
					sortOrder: 1,
				},
			],
		});
		const refreshConversation = vi.fn(async () => undefined);
		const { wrapper } = createProvidersWrapper({ queryClient });
		const { result } = renderHook(
			() =>
				useChatStreamController({
					setStreamError: vi.fn(),
					cacheConversation: (updated) => queryClient.setQueryData(nodeChatQueryKeys.conversation(updated.id), updated),
					cacheConversationDetail: vi.fn(),
					refreshConversation,
					resolveSendConversation: vi.fn(),
					selectedConversationId: "conversation-1",
					displayConversations: [],
					modelOptions: [],
					cloudModelOptions: [],
					agentModeEnabled: false,
					selectedAgentId: "",
					agentOptions: [],
					activeRevisionByGroup: {},
					attachmentFileIds: [],
					toolsEnabled: false,
					knowledgeBaseEnabled: false,
					reasoningEffort: "medium",
					onVoiceTurnStart: vi.fn(),
					onVoiceAnswerProgress: vi.fn(),
					loadedSelectedConversationId: "",
				}),
			{ wrapper },
		);

		await act(() => result.current.handleCancel());

		expect(adapter.cancelMessage).toHaveBeenCalledWith({
			conversationId: "conversation-1",
			messageId: "assistant-1",
			requestId: "request-1",
		});
		expect(refreshConversation).toHaveBeenCalledWith("conversation-1");
		expect(
			queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"))?.messages[0]?.status,
		).toBe("cancelled");
	});

	it("regenerate stamps the original's group on the variant, refreshes once after the stream, and releases the stream", async () => {
		const original: ChatConversationModel = {
			...conversation(),
			messages: [
				{
					id: "assistant-1",
					conversationId: "conversation-1",
					requestId: "request-1",
					role: "assistant",
					content: "first answer",
					status: "completed",
					variantGroupId: "group-1",
					createdAt: "2026-05-24T00:00:00.000Z",
					sortOrder: 1,
				},
			],
		};
		const variantEvents: NodeChatStreamEventDto[] = [
			{ ...deltaEvent(), messageId: "variant-1", requestId: "request-2" },
			{
				type: nodeChatStreamEventTypes.assistantCompleted,
				conversationId: "conversation-1",
				messageId: "variant-1",
				requestId: "request-2",
				status: "completed",
				sequence: 2,
				occurredAtUtc: 1_700_000_000_001,
				content: "second answer",
			},
		];
		adapter.regenerateMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield* variantEvents;
			},
		}));
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				// A following send only has to start: the loop owning `activeStream` would return before reaching the adapter.
			},
		}));
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } });
		queryClient.setQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"), original);
		const readVariant = () =>
			queryClient
				.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"))
				?.messages.find((message) => message.id === "variant-1");
		const variantStatusAtRefresh: (string | undefined)[] = [];
		const refreshConversation = vi.fn(async () => {
			variantStatusAtRefresh.push(readVariant()?.status);
		});
		const writeConversation = (updated: ChatConversationModel) =>
			queryClient.setQueryData(nodeChatQueryKeys.conversation(updated.id), updated);
		const { wrapper } = createProvidersWrapper({ queryClient });
		const { result } = renderHook(
			() =>
				useChatStreamController({
					setStreamError: vi.fn(),
					cacheConversation: writeConversation,
					cacheConversationDetail: writeConversation,
					refreshConversation,
					resolveSendConversation: vi.fn(async () => original),
					selectedConversationId: "conversation-1",
					displayConversations: [],
					modelOptions: [],
					cloudModelOptions: [],
					agentModeEnabled: false,
					selectedAgentId: "",
					agentOptions: [],
					activeRevisionByGroup: {},
					attachmentFileIds: [],
					toolsEnabled: false,
					knowledgeBaseEnabled: false,
					reasoningEffort: "medium",
					onVoiceTurnStart: vi.fn(),
					onVoiceAnswerProgress: vi.fn(),
					loadedSelectedConversationId: "",
				}),
			{ wrapper },
		);

		act(() => result.current.handleRegenerate("assistant-1"));

		await waitFor(() => expect(refreshConversation).toHaveBeenCalledTimes(1));
		expect(refreshConversation).toHaveBeenCalledWith("conversation-1");
		expect(variantStatusAtRefresh).toEqual(["completed"]);
		expect(readVariant()?.variantGroupId).toBe("group-1");

		await act(() => result.current.handleSend("next question", "medium", ""));

		expect(adapter.sendMessage).toHaveBeenCalledTimes(1);
		expect(refreshConversation).toHaveBeenCalledTimes(2);
	});

	const hubRefusal = (token: string): Error =>
		new Error(`An error occurred on the server while streaming results. HubException: ${token}: refused by the node.`);

	it.each([
		[
			"the first-chunk watchdog",
			new StreamWatchdogError("no-first-chunk", "Local chat stream timed out (no-first-chunk)."),
			"Connection watchdog: the response never started arriving in this browser.",
		],
		[
			"the stall watchdog",
			new StreamWatchdogError("inter-chunk-stall", "Local chat stream timed out (inter-chunk-stall)."),
			"Connection watchdog: the response stopped arriving in this browser.",
		],
		[
			"a live workflow run",
			hubRefusal("GraphWorkflowRunLiveInConversation"),
			"This conversation is running a workflow; answer through it or stop it first.",
		],
		[
			"a parked turn",
			hubRefusal("ConversationTurnParked"),
			"A reply in this conversation is waiting for your answer. Answer the waiting card, then send again.",
		],
		[
			"a read-only conversation",
			hubRefusal("ReadOnlyConversation"),
			"This conversation was started from a paired client and is view-only on this node.",
		],
		["any other failure", hubRefusal("Backend"), "Backend: refused by the node."],
	])(
		"regenerate maps %s to its banner, leaves the original turn alone and releases the stream",
		async (_name, failure, banner) => {
			const original: ChatConversationModel = {
				...conversation(),
				messages: [
					{
						id: "assistant-1",
						conversationId: "conversation-1",
						requestId: "request-1",
						role: "assistant",
						content: "first answer",
						status: "completed",
						createdAt: "2026-05-24T00:00:00.000Z",
						sortOrder: 1,
					},
				],
			};
			adapter.regenerateMessage.mockImplementation(() => ({
				[Symbol.asyncIterator]: () => ({
					next: async () => {
						throw failure;
					},
				}),
			}));
			const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } });
			queryClient.setQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation("conversation-1"), original);
			const setStreamError = vi.fn();
			const refreshConversation = vi.fn(async () => undefined);
			const writeConversation = (updated: ChatConversationModel) =>
				queryClient.setQueryData(nodeChatQueryKeys.conversation(updated.id), updated);
			const { wrapper } = createProvidersWrapper({ queryClient });
			const { result } = renderHook(
				() =>
					useChatStreamController({
						setStreamError,
						cacheConversation: writeConversation,
						cacheConversationDetail: writeConversation,
						refreshConversation,
						resolveSendConversation: vi.fn(),
						selectedConversationId: "conversation-1",
						displayConversations: [],
						modelOptions: [],
						cloudModelOptions: [],
						agentModeEnabled: false,
						selectedAgentId: "",
						agentOptions: [],
						activeRevisionByGroup: {},
						attachmentFileIds: [],
						toolsEnabled: false,
						knowledgeBaseEnabled: false,
						reasoningEffort: "medium",
						onVoiceTurnStart: vi.fn(),
						onVoiceAnswerProgress: vi.fn(),
						loadedSelectedConversationId: "",
					}),
				{ wrapper },
			);

			act(() => result.current.handleRegenerate("assistant-1"));

			await waitFor(() => expect(refreshConversation).toHaveBeenCalledTimes(1));
			expect(setStreamError).toHaveBeenLastCalledWith(banner);
			// Unlike a send, a refused regenerate has no turn of its own to fail: the original answer stays as it was.
			expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toEqual(original);

			// The stream slot is free again: a stuck `activeStream` would return before reaching the adapter.
			act(() => result.current.handleRegenerate("assistant-1"));
			await waitFor(() => expect(refreshConversation).toHaveBeenCalledTimes(2));
			expect(adapter.regenerateMessage).toHaveBeenCalledTimes(2);
		},
	);
});
