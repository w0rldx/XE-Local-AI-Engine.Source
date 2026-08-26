import { ApiError } from "@/core/api/errors/ApiError";
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
import {
	mapConversation,
	mapConversationSummary,
	mapMessageFeedback,
	mapMessageRevisions,
} from "@/features/chat/api/NodeChatMapper";
import { guardNodeChatStream } from "@/features/chat/api/NodeChatStreamGuard";
import { streamNodeChatEvents } from "@/features/chat/api/NodeChatStreamTransport";
import type {
	ChatCompactionResult,
	ChatConversationListModel,
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageRevisions,
} from "@/features/chat/models/ChatModels";
import type { WireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import type { NodeChatStreamEventDto, NodeChatStreamRequestDto } from "@/features/chat/models/NodeChatStreamTypes";

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
	listConversations(options?: ListConversationsOptions): Promise<ChatConversationListModel>;
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
		// Developer-mode per-turn sampling overrides, the same ones a send carries. Undefined when developer mode is
		// off or nothing is set — the hub then receives null and the rerun keeps the model defaults.
		samplingOptions: WireSamplingOptions | undefined,
		signal: AbortSignal,
	): AsyncIterable<NodeChatStreamEventDto>;
	resumeConversation(conversationId: string, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto>;
}

// Exported for the wire-mapping test only. `sendMessage` hands the result straight to a lazy
// stream transport closure, so the mapped payload is unobservable from outside without standing up a
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
		attachmentFileIds: request.attachmentFileIds && request.attachmentFileIds.length > 0 ? request.attachmentFileIds : undefined,
		// Forward sampling overrides only when present; omitted when developer mode is off or nothing set.
		samplingOptions: request.samplingOptions,
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
		return { conversations: (data.items ?? []).map(mapConversationSummary), maxMessageSizeKb: data.maxMessageSizeKb };
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
			compactNodeChatConversation({
				path: { conversationId },
				body: { model: model ?? null },
				signal: options?.signal,
				throwOnError: true,
			}),
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
			streamNodeChatEvents(
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
	regenerateMessage(
		conversationId,
		originalMessageId,
		reasoningEffort,
		useLocalTools,
		useKnowledgeBase,
		selectedPath,
		samplingOptions,
		signal,
	) {
		// Server mints the sibling variant + drives the run (assistant revision flow); the variant messageId + requestId arrive
		// on the stream events and are latched for reconnect/resume. Streams exactly like a send, and honors the
		// current reasoning + local-tools + knowledge-base selection, the active conversation-tree path, and the
		// developer-mode sampling overrides via the hub args (RegenerateMessage(conversationId, messageId, effort,
		// useLocalTools, useKnowledgeBase, selectedPath, samplingOptions)). Absent selection/overrides ride as null.
		return guardNodeChatStream(
			streamNodeChatEvents(
				{
					method: "RegenerateMessage",
					args: [
						conversationId,
						originalMessageId,
						reasoningEffort,
						useLocalTools,
						useKnowledgeBase,
						selectedPath ?? null,
						samplingOptions ?? null,
					],
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
			streamNodeChatEvents({ method: "ResumeConversation", args: [conversationId], isResumeOpening: true }, signal),
		);
	},
};
