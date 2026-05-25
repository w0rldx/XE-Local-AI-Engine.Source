import { Alert, Box, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useRef, useState } from "react";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { appendOptimisticNodeChatSend, applyNodeChatStreamEvent, markNodeChatStreamTerminated } from "@/features/chat/api/NodeChatStreamState";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatConversationModel, ChatStreamingState, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue, toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

const modelOptions: ModelOption[] = [
	{
		value: localDefaultModelValue,
		label: "Local default",
		displayName: "Local runtime default",
		isReasoningModel: false,
		isAvailable: true,
		statusLabel: "Runtime-selected model",
	},
];

const chatUiCapabilities = buildChatUiCapabilities(nodeCapabilities.chat);
const hiddenNodeSurfaces = hiddenChatSurfaceLabels(chatUiCapabilities).join(", ");
const emptyConversations: ChatConversationModel[] = [];

interface ActiveChatStream {
	conversationId: string;
	messageId: string;
	requestId: string;
	abortController: AbortController;
}

function mergeSelectedConversation(
	conversations: ChatConversationModel[],
	selectedConversation?: ChatConversationModel,
): ChatConversationModel[] {
	if (!selectedConversation) {
		return conversations;
	}

	const hasSelectedConversation = conversations.some((conversation) => conversation.id === selectedConversation.id);
	if (!hasSelectedConversation) {
		return [selectedConversation, ...conversations];
	}

	return conversations.map((conversation) => (conversation.id === selectedConversation.id ? selectedConversation : conversation));
}

function errorMessage(error: unknown): string {
	return error instanceof Error ? error.message : "Unknown error";
}

function createId(): string {
	return crypto.randomUUID();
}

function titleFromContent(content: string): string {
	const normalized = content.replace(/\s+/g, " ").trim();
	return normalized.length > 48 ? `${normalized.slice(0, 45)}…` : normalized || "New conversation";
}

export function Chat() {
	const queryClient = useQueryClient();
	const activeStream = useRef<ActiveChatStream | null>(null);
	const [requestedConversationId, setRequestedConversationId] = useState("");
	const [collapsed, setCollapsed] = useState(false);
	const [selectedModel, setSelectedModel] = useState(modelOptions[0]?.value ?? "");
	const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>("medium");
	const [streamingMessage, setStreamingMessage] = useState<ChatStreamingState | undefined>();
	const [streamError, setStreamError] = useState<string | undefined>();

	const conversationsQuery = useQuery({
		queryKey: nodeChatQueryKeys.conversations(),
		queryFn: ({ signal }) => nodeChatAdapter.listConversations({ signal }),
	});

	const createConversationMutation = useMutation({
		mutationFn: () => nodeChatAdapter.createConversation({ title: "New conversation" }),
		onSuccess: async (conversation) => {
			queryClient.setQueryData<ChatConversationModel[]>(nodeChatQueryKeys.conversations(), (current = emptyConversations) => [conversation, ...current]);
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			setRequestedConversationId(conversation.id);
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
		},
	});

	const conversations = conversationsQuery.data ?? emptyConversations;
	const requestedConversationExists = conversations.some((conversation) => conversation.id === requestedConversationId);
	const selectedConversationId = requestedConversationExists ? requestedConversationId : (conversations[0]?.id ?? "");

	const selectedConversationQuery = useQuery({
		queryKey: nodeChatQueryKeys.conversation(selectedConversationId),
		queryFn: ({ signal }) => nodeChatAdapter.getConversation(selectedConversationId, { signal }),
		enabled: selectedConversationId.length > 0,
	});

	const displayConversations = useMemo(
		() => mergeSelectedConversation(conversations, selectedConversationQuery.data),
		[conversations, selectedConversationQuery.data],
	);
	const isLoadingInitialConversations = conversationsQuery.isLoading && displayConversations.length === 0;
	const isCreatingConversation = createConversationMutation.isPending;
	const isSending = Boolean(streamingMessage?.isActive);

	const cacheConversation = useCallback(
		(conversation: ChatConversationModel): void => {
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			queryClient.setQueryData<ChatConversationModel[]>(nodeChatQueryKeys.conversations(), (current = emptyConversations) =>
				mergeSelectedConversation(current, conversation),
			);
		},
		[queryClient],
	);

	const resolveSendConversation = useCallback(
		async (content: string): Promise<ChatConversationModel> => {
			if (selectedConversationQuery.data) {
				return selectedConversationQuery.data;
			}

			const summaryConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
			if (summaryConversation) {
				const loaded = await nodeChatAdapter.getConversation(summaryConversation.id);
				cacheConversation(loaded);
				return loaded;
			}

			const created = await nodeChatAdapter.createConversation({ title: titleFromContent(content) });
			cacheConversation(created);
			setRequestedConversationId(created.id);
			return created;
		},
		[cacheConversation, displayConversations, selectedConversationId, selectedConversationQuery.data],
	);

	const refreshConversation = useCallback(
		async (conversationId: string): Promise<void> => {
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId) }),
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() }),
			]);
		},
		[queryClient],
	);

	const handleSend = useCallback(
		async (content: string, _effort: ReasoningEffort, model: string): Promise<void> => {
			if (activeStream.current) {
				return;
			}

			setStreamError(undefined);
			let conversation: ChatConversationModel;
			try {
				conversation = await resolveSendConversation(content);
			} catch (error) {
				setStreamError(errorMessage(error));
				return;
			}

			const ids = {
				userMessageId: createId(),
				assistantMessageId: createId(),
				requestId: createId(),
			};
			const requestModel = toNodeChatRequestModel(model);
			const startedAt = new Date().toISOString();
			const optimisticConversation = appendOptimisticNodeChatSend(conversation, ids, content, startedAt, requestModel);
			const abortController = new AbortController();

			cacheConversation(optimisticConversation);
			setStreamingMessage({
				conversationId: conversation.id,
				messageId: ids.assistantMessageId,
				content: "",
				isActive: true,
			});
			activeStream.current = {
				conversationId: conversation.id,
				messageId: ids.assistantMessageId,
				requestId: ids.requestId,
				abortController,
			};

			try {
				for await (const streamEvent of nodeChatAdapter.sendMessage(
					{
						conversationId: conversation.id,
						content,
						userMessageId: ids.userMessageId,
						messageId: ids.assistantMessageId,
						requestId: ids.requestId,
						model: requestModel,
					},
					abortController.signal,
				)) {
					const currentConversation = queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ?? optimisticConversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					cacheConversation(applied.conversation);
					setStreamingMessage(applied.streamingMessage);
				}
			} catch (error) {
				if (!abortController.signal.aborted) {
					const message = errorMessage(error);
					const currentConversation = queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ?? optimisticConversation;
					const failed = markNodeChatStreamTerminated(currentConversation, ids.assistantMessageId, "failed", message);
					cacheConversation(failed.conversation);
					setStreamingMessage(failed.streamingMessage);
					setStreamError(message);
				}
			} finally {
				activeStream.current = null;
				setStreamingMessage((current) => (current?.messageId === ids.assistantMessageId ? { ...current, isActive: false } : current));
				await refreshConversation(conversation.id);
			}
		},
		[cacheConversation, queryClient, refreshConversation, resolveSendConversation],
	);

	const handleCancel = useCallback(async (): Promise<void> => {
		const active = activeStream.current;
		if (!active) {
			return;
		}

		try {
			await nodeChatAdapter.cancelMessage({
				conversationId: active.conversationId,
				messageId: active.messageId,
				requestId: active.requestId,
			});
		} catch (error) {
			setStreamError(errorMessage(error));
		} finally {
			active.abortController.abort();
			const currentConversation = queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(active.conversationId));
			if (currentConversation) {
				const cancelled = markNodeChatStreamTerminated(currentConversation, active.messageId, "cancelled");
				cacheConversation(cancelled.conversation);
				setStreamingMessage(cancelled.streamingMessage);
			}
			await refreshConversation(active.conversationId);
		}
	}, [cacheConversation, queryClient, refreshConversation]);

	const notice = useMemo(
		() =>
			streamError ? (
				<Stack gap={2}>
					<Text fw={700}>Local chat stream failed.</Text>
					<Text size="sm">{streamError}</Text>
				</Stack>
			) : conversationsQuery.isError ? (
				<Stack gap={2}>
					<Text fw={700}>Unable to load local chat history.</Text>
					<Text size="sm">{errorMessage(conversationsQuery.error)}</Text>
				</Stack>
			) : (
				`Local conversation history is loaded from the node API. Send, stream, and cancel are wired to the local SignalR hub. Node mode hides ${hiddenNodeSurfaces}.`
			),
		[conversationsQuery.error, conversationsQuery.isError, streamError],
	);

	return (
		<Box py="lg" style={{ display: "flex", flexDirection: "column", flex: 1, minHeight: 0 }}>
			{isLoadingInitialConversations ? (
				<Alert color="blue" variant="light" icon={<Loader size={16} />}>
					Loading local chat history…
				</Alert>
			) : null}
			{createConversationMutation.isError ? (
				<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} mb="md">
					{errorMessage(createConversationMutation.error)}
				</Alert>
			) : null}
			<ChatDisplayShell
				conversations={displayConversations}
				selectedConversationId={selectedConversationId}
				modelOptions={modelOptions}
				selectedModel={selectedModel}
				reasoningEffort={reasoningEffort}
				availableReasoningEfforts={["none", "low", "medium", "high"]}
				capabilities={chatUiCapabilities}
				contextUsage={{
					isAuthoritative: true,
					modelLabel: "Local runtime default",
					nodeLabel: "Local node",
				}}
				streamingMessage={streamingMessage}
				disabledNotice={notice}
				inputStatus={{
					isSending,
					chatInputDisabled: isCreatingConversation,
					modelSelectorDisabled: true,
					sendDisabled: selectedConversationQuery.isLoading,
				}}
				conversationListCollapsed={collapsed}
				onSelectConversation={setRequestedConversationId}
				onCreateConversation={() => {
					if (!isCreatingConversation) {
						createConversationMutation.mutate();
					}
				}}
				onToggleConversationList={() => setCollapsed((value) => !value)}
				onModelChange={setSelectedModel}
				onReasoningEffortChange={setReasoningEffort}
				onSend={(content, effort, model) => {
					handleSend(content, effort, model).catch((error: unknown) => setStreamError(errorMessage(error)));
				}}
				onCancel={() => {
					handleCancel().catch((error: unknown) => setStreamError(errorMessage(error)));
				}}
			/>
		</Box>
	);
}
