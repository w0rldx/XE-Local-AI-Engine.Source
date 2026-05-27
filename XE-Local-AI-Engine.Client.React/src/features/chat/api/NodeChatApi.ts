import { isAxiosError, type AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

/** Conflict code the node returns when a mutation targets a read-only (Origin=Remote) conversation (Phase 1.5). */
export const nodeChatReadOnlyConflictCode = "conversation-read-only";

interface NodeChatConflictResponseDto {
	code: string;
	reason: string;
}

/** True when the error is the node's 409 rejection of a write to a remote-origin (view-only) conversation. */
export function isNodeChatReadOnlyConflict(error: unknown): boolean {
	if (!isAxiosError(error) || error.response?.status !== 409) {
		return false;
	}

	const body = error.response.data as Partial<NodeChatConflictResponseDto> | undefined;
	return body?.code === nodeChatReadOnlyConflictCode;
}

export interface CreateNodeChatConversationRequestDto {
	title?: string;
	userId?: string;
}

export interface ListNodeChatConversationsRequestDto {
	includeArchived?: boolean;
	limit?: number;
}

export interface ListNodeChatConversationsResponseDto {
	items: NodeChatConversationSummaryResponseDto[];
}

export interface NodeChatConversationSummaryResponseDto {
	conversationId: string;
	title?: string | null;
	createdAtUtc: number;
	lastSeenUtc: number;
	lastMessagePreview?: string | null;
	lastMessageStatus?: string | null;
	purged: boolean;
	origin: string;
	isPinned: boolean;
	archived: boolean;
}

export interface NodeChatConversationResponseDto {
	conversationId: string;
	title?: string | null;
	userId?: string | null;
	createdAtUtc: number;
	lastSeenUtc: number;
	purged: boolean;
	origin: string;
	isPinned: boolean;
	archived: boolean;
	branchOfConversationId?: string | null;
	messages: NodeChatMessageResponseDto[];
}

export interface NodeChatMessageResponseDto {
	messageId: string;
	conversationId: string;
	requestId?: string | null;
	sequence: number;
	role: string;
	content: string;
	reasoning?: string | null;
	status: string;
	createdAtUtc: number;
	updatedAtUtc: number;
	origin: string;
	model?: string | null;
	error?: string | null;
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
	reasoningTokens?: number | null;
	parentMessageId?: string | null;
	variantGroupId?: string | null;
}

export interface CancelNodeChatMessageRequestDto {
	conversationId: string;
	messageId: string;
	requestId: string;
}

export interface NodeChatCancelMessageResponseDto {
	conversationId: string;
	messageId: string;
	requestId: string;
	status: string;
	cancelled: boolean;
}

export interface NodeChatDeleteConversationResponseDto {
	conversationId: string;
	cancelRequested: boolean;
	purged: boolean;
}

export interface NodeChatStreamRequestDto {
	conversationId: string;
	content: string;
	userMessageId?: string;
	messageId?: string;
	requestId?: string;
	model?: string;
}

export interface NodeChatStreamEventDto {
	type: string;
	conversationId: string;
	messageId: string;
	requestId: string;
	status: string;
	sequence: number;
	occurredAtUtc: number;
	delta?: string | null;
	reasoningDelta?: string | null;
	content?: string | null;
	reasoning?: string | null;
	error?: string | null;
	model?: string | null;
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
	reasoningTokens?: number | null;
}

export async function listConversations(
	request: ListNodeChatConversationsRequestDto = {},
	config?: AxiosRequestConfig,
): Promise<ListNodeChatConversationsResponseDto> {
	const { data } = await axiosInstance.get<ListNodeChatConversationsResponseDto>(buildLocalApiUrl("chat/conversations"), {
		...config,
		params: {
			includeArchived: request.includeArchived,
			limit: request.limit,
			...config?.params,
		},
	});

	return data;
}

export async function getConversation(conversationId: string, config?: AxiosRequestConfig): Promise<NodeChatConversationResponseDto> {
	const { data } = await axiosInstance.get<NodeChatConversationResponseDto>(buildLocalApiUrl(`chat/conversations/${conversationId}`), config);
	return data;
}

export async function createConversation(
	request: CreateNodeChatConversationRequestDto = {},
	config?: AxiosRequestConfig,
): Promise<NodeChatConversationResponseDto> {
	const { data } = await axiosInstance.post<NodeChatConversationResponseDto>(buildLocalApiUrl("chat/conversations"), request, config);
	return data;
}

export async function deleteConversation(
	conversationId: string,
	purgeImmediately = false,
	config?: AxiosRequestConfig,
): Promise<NodeChatDeleteConversationResponseDto> {
	const { data } = await axiosInstance.delete<NodeChatDeleteConversationResponseDto>(buildLocalApiUrl(`chat/conversations/${conversationId}`), {
		...config,
		params: {
			purgeImmediately,
			...config?.params,
		},
	});

	return data;
}

export async function cancelMessage(
	request: CancelNodeChatMessageRequestDto,
	config?: AxiosRequestConfig,
): Promise<NodeChatCancelMessageResponseDto> {
	const { data } = await axiosInstance.post<NodeChatCancelMessageResponseDto>(buildLocalApiUrl("chat/cancel"), request, config);
	return data;
}

export interface RenameNodeChatConversationRequestDto {
	title?: string;
}

export interface PinNodeChatConversationRequestDto {
	isPinned: boolean;
}

export interface ArchiveNodeChatConversationRequestDto {
	archived: boolean;
}

export async function renameConversation(
	conversationId: string,
	request: RenameNodeChatConversationRequestDto,
	config?: AxiosRequestConfig,
): Promise<NodeChatConversationResponseDto> {
	const { data } = await axiosInstance.patch<NodeChatConversationResponseDto>(
		buildLocalApiUrl(`chat/conversations/${conversationId}/rename`),
		request,
		config,
	);
	return data;
}

export async function setConversationPinned(
	conversationId: string,
	request: PinNodeChatConversationRequestDto,
	config?: AxiosRequestConfig,
): Promise<NodeChatConversationResponseDto> {
	const { data } = await axiosInstance.patch<NodeChatConversationResponseDto>(
		buildLocalApiUrl(`chat/conversations/${conversationId}/pin`),
		request,
		config,
	);
	return data;
}

export async function setConversationArchived(
	conversationId: string,
	request: ArchiveNodeChatConversationRequestDto,
	config?: AxiosRequestConfig,
): Promise<NodeChatConversationResponseDto> {
	const { data } = await axiosInstance.patch<NodeChatConversationResponseDto>(
		buildLocalApiUrl(`chat/conversations/${conversationId}/archive`),
		request,
		config,
	);
	return data;
}

export interface NodeChatBranchConversationResponseDto {
	sourceConversationId: string;
	branchedConversationId: string;
	copiedMessageCount: number;
}

export interface NodeChatMessageRevisionsResponseDto {
	messageId: string;
	variantGroupId?: string | null;
	variants: NodeChatMessageResponseDto[];
}

export type NodeChatFeedbackRating = "up" | "down";

export interface SetNodeChatMessageFeedbackRequestDto {
	rating: NodeChatFeedbackRating;
	comment?: string;
}

export interface NodeChatMessageFeedbackResponseDto {
	messageId: string;
	conversationId: string;
	rating: string;
	comment?: string | null;
	createdAtUtc: number;
	updatedAtUtc: number;
}

/** Clones the conversation up to and including the target message into a new Origin=Local conversation. */
export async function branchConversation(
	conversationId: string,
	messageId: string,
	config?: AxiosRequestConfig,
): Promise<NodeChatBranchConversationResponseDto> {
	// Ids bind from the route; the body MUST be an empty JSON object — FastEndpoints rejects an empty-body
	// POST with 415 at model-binding (before the guard runs), so `undefined` would never reach the handler.
	const { data } = await axiosInstance.post<NodeChatBranchConversationResponseDto>(
		buildLocalApiUrl(`chat/conversations/${conversationId}/branch/${messageId}`),
		{},
		config,
	);
	return data;
}

/** Lists every sibling variant (revision) of an assistant turn. Returns null when the message has no variants (404). */
export async function listMessageRevisions(
	conversationId: string,
	messageId: string,
	config?: AxiosRequestConfig,
): Promise<NodeChatMessageRevisionsResponseDto | null> {
	try {
		const { data } = await axiosInstance.get<NodeChatMessageRevisionsResponseDto>(
			buildLocalApiUrl(`chat/conversations/${conversationId}/messages/${messageId}/revisions`),
			config,
		);
		return data;
	} catch (error) {
		if (isAxiosError(error) && error.response?.status === 404) {
			return null;
		}
		throw error;
	}
}

/** Reads node-local feedback for a message. Returns null when no feedback has been recorded (404). */
export async function getMessageFeedback(
	conversationId: string,
	messageId: string,
	config?: AxiosRequestConfig,
): Promise<NodeChatMessageFeedbackResponseDto | null> {
	try {
		const { data } = await axiosInstance.get<NodeChatMessageFeedbackResponseDto>(
			buildLocalApiUrl(`chat/conversations/${conversationId}/messages/${messageId}/feedback`),
			config,
		);
		return data;
	} catch (error) {
		if (isAxiosError(error) && error.response?.status === 404) {
			return null;
		}
		throw error;
	}
}

/** Upserts node-local feedback (thumbs + optional comment) for a message. */
export async function setMessageFeedback(
	conversationId: string,
	messageId: string,
	request: SetNodeChatMessageFeedbackRequestDto,
	config?: AxiosRequestConfig,
): Promise<NodeChatMessageFeedbackResponseDto> {
	const { data } = await axiosInstance.put<NodeChatMessageFeedbackResponseDto>(
		buildLocalApiUrl(`chat/conversations/${conversationId}/messages/${messageId}/feedback`),
		request,
		config,
	);
	return data;
}
