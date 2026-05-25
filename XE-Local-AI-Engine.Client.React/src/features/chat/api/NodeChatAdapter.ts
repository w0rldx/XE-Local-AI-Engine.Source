import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import {
	cancelMessage,
	createConversation,
	deleteConversation,
	getConversation,
	listConversations,
	type NodeChatStreamEventDto,
	type NodeChatStreamRequestDto,
} from "@/features/chat/api/NodeChatApi";
import { mapConversation, mapConversationSummary } from "@/features/chat/api/NodeChatMapper";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

interface RequestOptions {
	signal?: AbortSignal;
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
}

export interface NodeChatAdapter {
	listConversations(options?: RequestOptions): Promise<ChatConversationModel[]>;
	getConversation(conversationId: string, options?: RequestOptions): Promise<ChatConversationModel>;
	createConversation(request?: CreateConversationRequest, options?: RequestOptions): Promise<ChatConversationModel>;
	deleteConversation(conversationId: string, purgeImmediately?: boolean, options?: RequestOptions): Promise<void>;
	cancelMessage(request: CancelMessageRequest, options?: RequestOptions): Promise<void>;
	sendMessage(request: SendMessageRequest, signal: AbortSignal): AsyncIterable<NodeChatStreamEventDto>;
}

function toStreamRequest(request: SendMessageRequest): NodeChatStreamRequestDto {
	return {
		conversationId: request.conversationId,
		content: request.content,
		userMessageId: request.userMessageId,
		messageId: request.messageId,
		requestId: request.requestId,
		model: request.model,
	};
}

function signalRStream<T>(request: NodeChatStreamRequestDto, signal: AbortSignal): AsyncIterable<T> {
	return {
		async *[Symbol.asyncIterator](): AsyncIterator<T> {
			const connection = new HubConnectionBuilder()
				.withUrl(buildLocalApiUrl("chat/hub"), {
					transport: HttpTransportType.LongPolling,
					accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
				})
				.configureLogging(LogLevel.Warning)
				.build();
			const values: T[] = [];
			let completed = false;
			let failure: unknown;
			let wake: (() => void) | undefined;

			const notify = (): void => {
				wake?.();
				wake = undefined;
			};

			await connection.start();
			const subscription = connection.stream<T>("SendMessage", request).subscribe({
				next: (value) => {
					values.push(value);
					notify();
				},
				error: (error) => {
					failure = error;
					completed = true;
					notify();
				},
				complete: () => {
					completed = true;
					notify();
				},
			});

			const abort = (): void => {
				subscription.dispose();
				completed = true;
				notify();
			};

			signal.addEventListener("abort", abort, { once: true });

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
				signal.removeEventListener("abort", abort);
				subscription.dispose();
				await connection.stop();
			}
		},
	};
}

export const nodeChatAdapter: NodeChatAdapter = {
	async listConversations(options) {
		const response = await listConversations({ includeArchived: false }, { signal: options?.signal });
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
	async cancelMessage(request, options) {
		await cancelMessage(request, { signal: options?.signal });
	},
	sendMessage(request, signal) {
		return signalRStream<NodeChatStreamEventDto>(toStreamRequest(request), signal);
	},
};
