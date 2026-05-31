import {
	branchConversation,
	cancelMessage,
	createConversation,
	deleteConversation,
	getConversation,
	listConversations,
	listMessageRevisions,
	type NodeChatBranchConversationResponseDto,
	type NodeChatFeedbackRating,
	type NodeChatStreamEventDto,
	type NodeChatStreamRequestDto,
	renameConversation,
	setConversationArchived,
	setConversationPinned,
	setMessageFeedback,
	setSelectedPath,
} from "@/features/chat/api/NodeChatApi";
import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import {
	mapConversation,
	mapConversationSummary,
	mapMessageFeedback,
	mapMessageRevisions,
} from "@/features/chat/api/NodeChatMapper";
import { guardNodeChatStream } from "@/features/chat/api/NodeChatStreamGuard";
import type { ChatConversationModel, ChatMessageFeedback, ChatMessageRevisions } from "@/features/chat/models/ChatModels";

/* eslint-disable react-doctor/async-await-in-loop */

interface RequestOptions {
	signal?: AbortSignal;
}

interface ListConversationsOptions extends RequestOptions {
	includeArchived?: boolean;
}

export interface CreateConversationRequest {
	title?: string;
	userId?: string;
}

export interface CancelMessageRequest {
	conversationId: string;
	messageId: string;
	requestId: string;
}

export interface SendMessageRequest {
	conversationId: string;
	content: string;
	userMessageId?: string;
	messageId?: string;
	requestId?: string;
	model?: string;
	useLocalTools?: boolean;
	reasoningEffort?: string;
	selectedPath?: Record<string, string>;
}

export interface NodeChatAdapter {
	listConversations(options?: ListConversationsOptions): Promise<ChatConversationModel[]>;
	getConversation(conversationId: string, options?: RequestOptions): Promise<ChatConversationModel>;
	createConversation(request?: CreateConversationRequest, options?: RequestOptions): Promise<ChatConversationModel>;
	deleteConversation(conversationId: string, purgeImmediately?: boolean, options?: RequestOptions): Promise<void>;
	renameConversation(conversationId: string, title: string, options?: RequestOptions): Promise<ChatConversationModel>;
	setConversationPinned(conversationId: string, isPinned: boolean, options?: RequestOptions): Promise<ChatConversationModel>;
	setConversationArchived(conversationId: string, archived: boolean, options?: RequestOptions): Promise<ChatConversationModel>;
	branchConversation(
		conversationId: string,
		messageId: string,
		options?: RequestOptions,
	): Promise<NodeChatBranchConversationResponseDto>;
	listMessageRevisions(conversationId: string, messageId: string, options?: RequestOptions): Promise<ChatMessageRevisions | null>;
	setMessageFeedback(
		conversationId: string,
		messageId: string,
		rating: NodeChatFeedbackRating,
		comment: string | undefined,
		options?: RequestOptions,
	): Promise<ChatMessageFeedback>;
	cancelMessage(request: CancelMessageRequest, options?: RequestOptions): Promise<void>;
	persistSelectedPath(
		conversationId: string,
		selectedPath: Record<string, string>,
		options?: RequestOptions,
	): Promise<Record<string, string>>;
	sendMessage(request: SendMessageRequest, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto>;
	regenerateMessage(
		conversationId: string,
		originalMessageId: string,
		reasoningEffort: string | undefined,
		useLocalTools: boolean,
		selectedPath: Record<string, string> | undefined,
		signal: AbortSignal,
	): AsyncIterable<NodeChatStreamEventDto>;
}

function toStreamRequest(request: SendMessageRequest): NodeChatStreamRequestDto {
	return {
		conversationId: request.conversationId,
		content: request.content,
		userMessageId: request.userMessageId,
		messageId: request.messageId,
		requestId: request.requestId,
		model: request.model,
		useLocalTools: request.useLocalTools,
		reasoningEffort: request.reasoningEffort,
		selectedPath: request.selectedPath,
	};
}

// A terminal stream event ends the invocation; after one of these there is nothing left to resume.
const terminalStreamEventTypes = new Set<string>([
	"assistant-completed",
	"assistant-cancelled",
	"assistant-failed",
	"assistant-interrupted",
]);

function isTerminalStreamEvent(event: NodeChatStreamEventDto): boolean {
	return terminalStreamEventTypes.has(event.type);
}

/** The opening hub call for a stream: a local send, a server-driven regenerate, or a re-attach to a run. */
interface StreamOpening {
	method: "SendMessage" | "RegenerateMessage" | "ResumeMessage";
	args: unknown[];
	// The assistant message id the caller renders into. Known up front for a send; for a regenerate it is the
	// server-minted variant id, latched from the first event; for a resume it is the target id that resume
	// events (stamped with the invocation id) are remapped onto.
	assistantMessageId?: string;
	// The invocation/resume key (== requestId). Known up front for a send and a direct resume; for a regenerate
	// it is latched from the first event's requestId (server-minted run), enabling resume after a reconnect.
	knownInvocationId?: string;
	// True when the OPENING call is itself a ResumeMessage (re-attaching to a server-driven run). Then an
	// opening error means "unknown/terminal invocation" → complete cleanly, rather than surfacing a failure.
	isResumeOpening?: boolean;
}

/**
 * Streams a chat turn over the persistent connection and transparently resumes after a reconnect. When the
 * underlying connection drops mid-stream, the active SignalR subscription errors; rather than failing the
 * turn, we wait for the connection to reconnect with a new id and re-attach via the hub's `ResumeMessage`
 * (keyed by the invocation/request id). Resumed events carry the invocation id as their message id, so they
 * are remapped back to the assistant message id the caller is rendering. Used for both local sends and
 * server-driven regenerations (assistant revision flow) — the regenerate latches its server-minted ids from the first event.
 */
function signalRStream(opening: StreamOpening, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto> {
	return {
		async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
			const values: NodeChatStreamEventDto[] = [];
			let completed = false;
			let failure: unknown;
			let wake: (() => void) | undefined;
			let reachedTerminal = false;
			let activeSubscription: { dispose: () => void } | undefined;
			// The request id doubles as the invocation id the resume registry keys on. Known up front for sends and
			// direct resumes; otherwise latched from the first event below.
			let invocationId = opening.knownInvocationId;
			let assistantMessageId = opening.assistantMessageId;

			const notify = (): void => {
				wake?.();
				wake = undefined;
			};

			const pushEvent = (event: NodeChatStreamEventDto): void => {
				// Latch ids from the first event when not known up front, so a reconnect can resume the run.
				invocationId ??= event.requestId || undefined;
				assistantMessageId ??= event.messageId || undefined;
				if (isTerminalStreamEvent(event)) {
					reachedTerminal = true;
				}
				values.push(event);
				notify();
			};

			const subscribe = (
				methodName: "SendMessage" | "RegenerateMessage" | "ResumeMessage",
				args: unknown[],
				isResume: boolean,
			): void => {
				const connection = nodeChatConnection.current();
				if (!connection) {
					return;
				}

				activeSubscription = connection.stream<NodeChatStreamEventDto>(methodName, ...args).subscribe({
					next: (value) => {
						// Resume events stamp the invocation id as the message id; remap to the assistant id
						// so the caller updates the same message instead of spawning a new one.
						pushEvent(isResume && assistantMessageId ? { ...value, messageId: assistantMessageId } : value);
					},
					error: (error) => {
						activeSubscription = undefined;
						// A ResumeMessage stream throws when the invocation is unknown/terminal — the response already
						// finished server-side. Complete cleanly so the caller refetches the persisted conversation
						// instead of showing a spurious failure.
						if (isResume) {
							completed = true;
							notify();
							return;
						}
						// A drop while the connection is reconnecting is recoverable via resume; otherwise fail.
						if (signal.aborted || reachedTerminal || !invocationId || nodeChatConnection.status === "disconnected") {
							failure = error;
							completed = true;
						}
						notify();
					},
					complete: () => {
						completed = true;
						notify();
					},
				});
			};

			const resumeAfterReconnect = (): void => {
				if (completed || reachedTerminal || !invocationId || activeSubscription) {
					return;
				}
				subscribe("ResumeMessage", [invocationId], true);
			};

			const unsubscribeFromConnection = nodeChatConnection.subscribe({
				onReconnected: () => resumeAfterReconnect(),
				onClose: (error) => {
					if (!reachedTerminal && !signal.aborted) {
						failure = error ?? new Error("The local chat connection closed before the response completed.");
						completed = true;
						notify();
					}
				},
			});

			const abort = (): void => {
				activeSubscription?.dispose();
				activeSubscription = undefined;
				completed = true;
				notify();
			};

			signal.addEventListener("abort", abort, { once: true });

			await nodeChatConnection.ensureConnection();
			subscribe(opening.method, opening.args, opening.isResumeOpening ?? false);

			try {
				while (!completed || values.length > 0) {
					const value = values.shift();
					if (value) {
						yield value;
						continue;
					}

					if (failure) {
						throw failure;
					}

					// biome-ignore lint/performance/noAwaitInLoops: AsyncIterable bridge waits for the next SignalR push before yielding again.
					await new Promise<void>((resolve) => {
						wake = resolve;
					});
				}

				if (failure) {
					throw failure;
				}
			} finally {
				// The connection is shared and long-lived; only the per-send subscription and the reconnect
				// listener are torn down here. The persistent connection is reused for the next send.
				signal.removeEventListener("abort", abort);
				unsubscribeFromConnection();
				activeSubscription?.dispose();
			}
		},
	};
}

export const nodeChatAdapter: NodeChatAdapter = {
	async listConversations(options) {
		const response = await listConversations({ includeArchived: options?.includeArchived ?? false }, { signal: options?.signal });
		return response.items.map(mapConversationSummary);
	},
	async getConversation(conversationId, options) {
		return mapConversation(await getConversation(conversationId, { signal: options?.signal }));
	},
	async createConversation(request, options) {
		return mapConversation(await createConversation(request, { signal: options?.signal }));
	},
	async deleteConversation(conversationId, purgeImmediately, options) {
		await deleteConversation(conversationId, purgeImmediately ?? false, { signal: options?.signal });
	},
	async renameConversation(conversationId, title, options) {
		return mapConversation(await renameConversation(conversationId, { title }, { signal: options?.signal }));
	},
	async setConversationPinned(conversationId, isPinned, options) {
		return mapConversation(await setConversationPinned(conversationId, { isPinned }, { signal: options?.signal }));
	},
	async setConversationArchived(conversationId, archived, options) {
		return mapConversation(await setConversationArchived(conversationId, { archived }, { signal: options?.signal }));
	},
	async branchConversation(conversationId, messageId, options) {
		return branchConversation(conversationId, messageId, { signal: options?.signal });
	},
	async listMessageRevisions(conversationId, messageId, options) {
		const response = await listMessageRevisions(conversationId, messageId, { signal: options?.signal });
		return response ? mapMessageRevisions(response) : null;
	},
	async setMessageFeedback(conversationId, messageId, rating, comment, options) {
		return mapMessageFeedback(
			await setMessageFeedback(conversationId, messageId, { rating, comment }, { signal: options?.signal }),
		);
	},
	async cancelMessage(request, options) {
		await cancelMessage(request, { signal: options?.signal });
	},
	async persistSelectedPath(conversationId, selectedPath, options) {
		const response = await setSelectedPath(conversationId, { selectedPath }, { signal: options?.signal });
		return response.selectedPath;
	},
	sendMessage(request, signal) {
		const streamRequest = toStreamRequest(request);
		return guardNodeChatStream(
			signalRStream(
				{
					method: "SendMessage",
					args: [streamRequest],
					assistantMessageId: streamRequest.messageId,
					knownInvocationId: streamRequest.requestId,
				},
				signal,
			),
		);
	},
	regenerateMessage(conversationId, originalMessageId, reasoningEffort, useLocalTools, selectedPath, signal) {
		// Server mints the sibling variant + drives the run (assistant revision flow); the variant messageId + requestId arrive
		// on the stream events and are latched for reconnect/resume. Streams exactly like a send, and honors the
		// current reasoning + local-tools selection plus the active conversation-tree path via the hub args
		// (RegenerateMessage(conversationId, messageId, effort, useLocalTools, selectedPath)).
		return guardNodeChatStream(
			signalRStream(
				{ method: "RegenerateMessage", args: [conversationId, originalMessageId, reasoningEffort, useLocalTools, selectedPath ?? null] },
				signal,
			),
		);
	},
};
