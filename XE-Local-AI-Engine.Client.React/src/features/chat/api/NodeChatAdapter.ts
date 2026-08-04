import { ApiError } from "@/core/api/errors/ApiError";
import type { WireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import {
	archiveNodeChatConversation,
	branchNodeChatConversation,
	cancelNodeChatMessage,
	compactNodeChatConversation,
	createNodeChatConversation,
	deleteNodeChatConversation,
	getNodeChatConversation,
	listNodeChatConversations,
	listNodeChatMessageRevisions,
	pinNodeChatConversation,
	renameNodeChatConversation,
	setNodeChatConversationMemoryExcluded,
	setNodeChatMessageFeedback,
	setNodeChatSelectedPath,
	type XeLocalAiEngineClientEndpointsLocalChatV1NodeChatBranchConversationResponse,
} from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import {
	mapConversation,
	mapConversationSummary,
	mapMessageFeedback,
	mapMessageRevisions,
} from "@/features/chat/api/NodeChatMapper";
import { guardNodeChatStream } from "@/features/chat/api/NodeChatStreamGuard";
import type {
	ChatCompactionResult,
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageRevisions,
} from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto, NodeChatStreamRequestDto } from "@/features/chat/models/NodeChatStreamTypes";

/* eslint-disable react-doctor/async-await-in-loop -- The async-iterator bridge must await each SignalR event in wire order before reading the next one. */

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
	// Opt-in knowledge-base grounding for a plain-chat turn. Ignored by the server in agent mode.
	useKnowledgeBase?: boolean;
	reasoningEffort?: string;
	selectedPath?: Record<string, string>;
	// Agent to resolve for this turn. Absent → Default Assistant (today's built-in chat path). Only included when
	// agent mode is enabled, a valid agent is selected, and the agent still exists in the live list (stale/deleted
	// ids are dropped by Chat.tsx before the send).
	agentDefinitionId?: string;
	// Current (non-deleted) attachment file ids for the conversation, re-sent on every turn so the server can
	// ground plain chat (inline extracted text) and stage files into AgentHome for agent mode. Absent/empty → none.
	attachmentFileIds?: string[];
	// Developer-mode per-send sampling overrides. Omitted entirely when developer mode is off or all fields null.
	samplingOptions?: WireSamplingOptions;
}

export interface NodeChatAdapter {
	listConversations(options?: ListConversationsOptions): Promise<ChatConversationModel[]>;
	getConversation(conversationId: string, options?: RequestOptions): Promise<ChatConversationModel>;
	createConversation(request?: CreateConversationRequest, options?: RequestOptions): Promise<ChatConversationModel>;
	deleteConversation(conversationId: string, purgeImmediately?: boolean, options?: RequestOptions): Promise<void>;
	renameConversation(conversationId: string, title: string, options?: RequestOptions): Promise<ChatConversationModel>;
	compactConversation(conversationId: string, model?: string, options?: RequestOptions): Promise<ChatCompactionResult>;
	setConversationPinned(conversationId: string, isPinned: boolean, options?: RequestOptions): Promise<ChatConversationModel>;
	setConversationArchived(conversationId: string, archived: boolean, options?: RequestOptions): Promise<ChatConversationModel>;
	setConversationMemoryExcluded(
		conversationId: string,
		memoryExcluded: boolean,
		options?: RequestOptions,
	): Promise<ChatConversationModel>;
	branchConversation(
		conversationId: string,
		messageId: string,
		selectedRevisions: Record<string, string> | undefined,
		options?: RequestOptions,
	): Promise<XeLocalAiEngineClientEndpointsLocalChatV1NodeChatBranchConversationResponse>;
	listMessageRevisions(conversationId: string, messageId: string, options?: RequestOptions): Promise<ChatMessageRevisions | null>;
	setMessageFeedback(
		conversationId: string,
		messageId: string,
		rating: ChatFeedbackRating,
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
		useKnowledgeBase: boolean,
		selectedPath: Record<string, string> | undefined,
		signal: AbortSignal,
	): AsyncIterable<NodeChatStreamEventDto>;
	resumeConversation(conversationId: string, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto>;
}

// Exported for the wire-mapping test only. `sendMessage` hands the result straight to a lazy
// `signalRStream` closure, so the mapped payload is unobservable from outside without standing up a
// SignalR stub — and the omit-when-absent rules below (attachments, sampling) are exactly what needs
// asserting. Kept out of NodeChatAdapter's public interface; only the module and its test use it.
export function toStreamRequest(request: SendMessageRequest): NodeChatStreamRequestDto {
	return {
		conversationId: request.conversationId,
		content: request.content,
		userMessageId: request.userMessageId,
		messageId: request.messageId,
		requestId: request.requestId,
		model: request.model,
		useLocalTools: request.useLocalTools,
		useKnowledgeBase: request.useKnowledgeBase,
		reasoningEffort: request.reasoningEffort,
		selectedPath: request.selectedPath,
		agentDefinitionId: request.agentDefinitionId,
		// Forward the conversation's current attachment ids only when present (omit the empty array to keep the
		// wire payload byte-identical to the no-attachment path).
		attachmentFileIds:
			request.attachmentFileIds && request.attachmentFileIds.length > 0 ? request.attachmentFileIds : undefined,
		// Forward sampling overrides only when present; omitted when developer mode is off or nothing set.
		samplingOptions: request.samplingOptions,
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
	method: "SendMessage" | "RegenerateMessage" | "ResumeMessage" | "ResumeConversation";
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

			// The highest sequence forwarded downstream. A resumed stream restarts its numbering at zero on the
			// server (the resume registry cannot see the original stream's counter), so its events are rebased
			// past this mark before they reach the sequence-deduping guard — otherwise the guard drops every
			// resumed event (terminal included) as a stale duplicate and the message sticks with no error.
			let lastPushedSequence = Number.NEGATIVE_INFINITY;

			const pushEvent = (event: NodeChatStreamEventDto): void => {
				// Latch ids from the first event when not known up front, so a reconnect can resume the run.
				invocationId ??= event.requestId || undefined;
				assistantMessageId ??= event.messageId || undefined;
				if (isTerminalStreamEvent(event)) {
					reachedTerminal = true;
				}
				lastPushedSequence = Math.max(lastPushedSequence, event.sequence);
				values.push(event);
				notify();
			};

			const subscribe = (
				methodName: "SendMessage" | "RegenerateMessage" | "ResumeMessage" | "ResumeConversation",
				args: unknown[],
				isResume: boolean,
			): void => {
				const connection = nodeChatConnection.current();
				if (!connection) {
					return;
				}

				// Captured once per subscription: resumed events 0,1,2,… map to base, base+1, base+2,… directly
				// after the last sequence the original stream delivered. SignalR delivers in order within one
				// subscription, so the rebased stream stays contiguous for the guard. On a direct resume opening
				// (nothing pushed yet) the base is 0 and the rebase is the identity.
				const resumeSequenceBase = isResume && Number.isFinite(lastPushedSequence) ? lastPushedSequence + 1 : 0;

				activeSubscription = connection.stream<NodeChatStreamEventDto>(methodName, ...args).subscribe({
					next: (value) => {
						if (!isResume) {
							pushEvent(value);
							return;
						}
						// Resume events stamp the invocation id as the message id; remap to the assistant id
						// so the caller updates the same message instead of spawning a new one.
						pushEvent({
							...value,
							sequence: resumeSequenceBase + value.sequence,
							messageId: assistantMessageId ?? value.messageId,
						});
					},
					error: (error) => {
						activeSubscription = undefined;
						// Only a transport interruption is recoverable via resume: the run keeps going server-side
						// and onReconnected will re-subscribe. This applies to a resumed stream too — a second drop
						// resumes again (each resume rebases its restarted sequences past the new high-water mark).
						const recoverable =
							!signal.aborted &&
							!reachedTerminal &&
							!!invocationId &&
							(nodeChatConnection.status === "reconnecting" || nodeChatConnection.status === "connecting");
						if (recoverable) {
							notify();
							return;
						}
						// A ResumeMessage stream that errors while the connection is stable means the invocation is
						// unknown/terminal — the response already finished server-side. Complete cleanly so the caller
						// refetches the persisted conversation instead of showing a spurious failure.
						if (isResume) {
							completed = true;
							notify();
							return;
						}
						// A subscription error while the connection is stably connected is a genuine hub/application
						// failure (the invocation threw during turn setup before any terminal event) — fail fast so
						// the caller surfaces it instead of waiting for a resume that will never come.
						failure = error;
						completed = true;
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
			// The await above can resolve after an abort (or a close) already fired: re-check before subscribing so we
			// never open a subscription that abort()/the finally will not tear down, which would leak the subscription
			// and start a server run the caller has already given up on. Dispose any stray subscription and stop.
			if (signal.aborted || completed) {
				activeSubscription?.dispose();
				activeSubscription = undefined;
				completed = true;
			} else {
				subscribe(opening.method, opening.args, opening.isResumeOpening ?? false);
			}

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
		const { data } = await callWithResponseValidation(
			listNodeChatConversations({
				query: { includeArchived: options?.includeArchived ?? false },
				signal: options?.signal,
				throwOnError: true,
			}),
		);
		return (data.items ?? []).map(mapConversationSummary);
	},
	async getConversation(conversationId, options) {
		const { data } = await callWithResponseValidation(
			getNodeChatConversation({ path: { conversationId }, signal: options?.signal, throwOnError: true }),
		);
		return mapConversation(data);
	},
	async createConversation(request, options) {
		const { data } = await callWithResponseValidation(
			createNodeChatConversation({ body: request ?? {}, signal: options?.signal, throwOnError: true }),
		);
		return mapConversation(data);
	},
	async deleteConversation(conversationId, purgeImmediately, options) {
		await callWithResponseValidation(
			deleteNodeChatConversation({
				path: { conversationId },
				body: { purgeImmediately: purgeImmediately ?? false },
				signal: options?.signal,
				throwOnError: true,
			}),
		);
	},
	async renameConversation(conversationId, title, options) {
		const { data } = await callWithResponseValidation(
			renameNodeChatConversation({ path: { conversationId }, body: { title }, signal: options?.signal, throwOnError: true }),
		);
		return mapConversation(data);
	},
	async compactConversation(conversationId, model, options) {
		const { data } = await callWithResponseValidation(
			compactNodeChatConversation({ path: { conversationId }, body: { model: model ?? null }, signal: options?.signal, throwOnError: true }),
		);
		return {
			outcome: data.outcome,
			summary: data.summary ?? undefined,
			coversToSequence: data.coversToSequence ?? undefined,
			messagesFolded: data.messagesFolded ?? 0,
			updatedAtUtc: data.updatedAtUtc ?? undefined,
			modelUsed: data.modelUsed ?? undefined,
			usedFallbackModel: data.usedFallbackModel ?? false,
		};
	},
	async setConversationPinned(conversationId, isPinned, options) {
		const { data } = await callWithResponseValidation(
			pinNodeChatConversation({ path: { conversationId }, body: { isPinned }, signal: options?.signal, throwOnError: true }),
		);
		return mapConversation(data);
	},
	async setConversationArchived(conversationId, archived, options) {
		const { data } = await callWithResponseValidation(
			archiveNodeChatConversation({ path: { conversationId }, body: { archived }, signal: options?.signal, throwOnError: true }),
		);
		return mapConversation(data);
	},
	async setConversationMemoryExcluded(conversationId, memoryExcluded, options) {
		const { data } = await callWithResponseValidation(
			setNodeChatConversationMemoryExcluded({
				path: { conversationId },
				body: { memoryExcluded },
				signal: options?.signal,
				throwOnError: true,
			}),
		);
		return mapConversation(data);
	},
	async branchConversation(conversationId, messageId, selectedRevisions, options) {
		// The ids bind from the route; the body carries the optional selected-revision map so the branched thread
		// matches the revisions the user was viewing (variantGroupId -> selectedMessageId). Omitting it (undefined)
		// serializes to `{}`, which keeps the server's newest-per-group default. A non-empty body also documents a
		// request content-type, so FastEndpoints no longer 415s the (formerly body-less) POST at model-binding.
		const { data } = await callWithResponseValidation(
			branchNodeChatConversation({
				path: { conversationId, messageId },
				body: { selectedRevisions },
				signal: options?.signal,
				throwOnError: true,
			}),
		);
		return data;
	},
	async listMessageRevisions(conversationId, messageId, options) {
		try {
			const { data } = await callWithResponseValidation(
				listNodeChatMessageRevisions({ path: { conversationId, messageId }, signal: options?.signal, throwOnError: true }),
			);
			return mapMessageRevisions(data);
		} catch (error) {
			// A message with no variants returns 404 — surface null (no revisions) rather than an error.
			if (error instanceof ApiError && error.statusCode === 404) {
				return null;
			}
			throw error;
		}
	},
	async setMessageFeedback(conversationId, messageId, rating, comment, options) {
		const { data } = await callWithResponseValidation(
			setNodeChatMessageFeedback({
				path: { conversationId, messageId },
				body: { rating, comment },
				signal: options?.signal,
				throwOnError: true,
			}),
		);
		return mapMessageFeedback(data);
	},
	async cancelMessage(request, options) {
		await callWithResponseValidation(cancelNodeChatMessage({ body: request, signal: options?.signal, throwOnError: true }));
	},
	async persistSelectedPath(conversationId, selectedPath, options) {
		const { data } = await callWithResponseValidation(
			setNodeChatSelectedPath({ path: { conversationId }, body: { selectedPath }, signal: options?.signal, throwOnError: true }),
		);
		return data.selectedPath ?? {};
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
	regenerateMessage(conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath, signal) {
		// Server mints the sibling variant + drives the run (assistant revision flow); the variant messageId + requestId arrive
		// on the stream events and are latched for reconnect/resume. Streams exactly like a send, and honors the
		// current reasoning + local-tools + knowledge-base selection plus the active conversation-tree path via the hub
		// args (RegenerateMessage(conversationId, messageId, effort, useLocalTools, useKnowledgeBase, selectedPath)).
		return guardNodeChatStream(
			signalRStream(
				{
					method: "RegenerateMessage",
					args: [conversationId, originalMessageId, reasoningEffort, useLocalTools, useKnowledgeBase, selectedPath ?? null],
				},
				signal,
			),
		);
	},
	resumeConversation(conversationId, signal) {
		// Cold-load re-attach. `regenerateMessage`/`sendMessage` own a run they started, and the reconnect path
		// re-attaches with an invocation id it still holds in memory — but a client that has just RELOADED holds
		// nothing, so the server resolves the live invocation from the conversation instead.
		//
		// This is what keeps a reload from stranding an in-flight ask_user question or tool approval: the prompt is
		// transient live state that is deliberately never written into the conversation's persisted parts, so
		// re-fetching the conversation cannot bring it back and the run would sit parked until it timed out.
		//
		// Opened as a RESUME so an "unknown/terminal invocation" error completes cleanly rather than surfacing a
		// failure, and so the server's restarted sequence numbering is rebased before the dedupe guard sees it.
		// The hub returns an empty stream when nothing is live, so calling this on every open is safe.
		return guardNodeChatStream(
			signalRStream({ method: "ResumeConversation", args: [conversationId], isResumeOpening: true }, signal),
		);
	},
};
