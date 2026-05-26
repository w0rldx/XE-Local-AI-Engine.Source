import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";

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
}

export interface NodeChatConversationResponseDto {
	conversationId: string;
	title?: string | null;
	userId?: string | null;
	createdAtUtc: number;
	lastSeenUtc: number;
	purged: boolean;
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
	model?: string | null;
	error?: string | null;
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
	reasoningTokens?: number | null;
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
