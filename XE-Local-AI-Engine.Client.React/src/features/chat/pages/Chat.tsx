import { Alert, Box, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { isNodeChatReadOnlyConflict } from "@/features/chat/api/NodeChatApi";
import { StreamWatchdogError } from "@/features/chat/api/NodeChatStreamGuard";
import { appendOptimisticNodeChatSend, applyNodeChatStreamEvent, markNodeChatStreamTerminated } from "@/features/chat/api/NodeChatStreamState";
import { buildChatUiCapabilities, hiddenChatSurfaceLabels } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatConversationModel, ChatFeedbackRating, ChatMessageFeedback, ChatStreamingState, ModelOption, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { deriveUsedContextTokens } from "@/features/chat/models/ContextUsageDerivation";
import { localDefaultModelValue, toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { getLocalModelDetails, listLocalModels } from "@/features/models/api/LocalModelsApi";
import type { LocalModelDto } from "@/features/models/api/LocalModelsApi";
import { localModelsQueryKeys } from "@/features/models/queries/LocalModelsQueryKeys";

const localDefaultModelOption: ModelOption = {
	value: localDefaultModelValue,
	label: "Local default",
	displayName: "Local runtime default",
	isReasoningModel: false,
	isAvailable: true,
	statusLabel: "Runtime-selected model",
};

function toModelOption(model: LocalModelDto, nodeAvailable: boolean): ModelOption {
	const statusLabel = [model.isSelected ? "Node default" : undefined, model.parameterSize ?? undefined, model.quantizationLevel ?? undefined]
		.filter((part): part is string => Boolean(part))
		.join(" · ");

	return {
		value: model.modelName,
		label: model.modelName,
		isReasoningModel: false,
		isAvailable: nodeAvailable,
		statusLabel: statusLabel.length > 0 ? statusLabel : undefined,
	};
}

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
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const activeStream = useRef<ActiveChatStream | null>(null);
	const [requestedConversationId, setRequestedConversationId] = useState("");
	const [collapsed, setCollapsed] = useState(false);
	const [selectedModel, setSelectedModel] = useState(localDefaultModelValue);
	const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>("medium");
	const [streamingMessage, setStreamingMessage] = useState<ChatStreamingState | undefined>();
	const [streamError, setStreamError] = useState<string | undefined>();
	const [conversationSearchQuery, setConversationSearchQuery] = useState("");
	const [showArchivedConversations, setShowArchivedConversations] = useState(false);
	const [mutatingConversationId, setMutatingConversationId] = useState<string | undefined>();
	// Operator's chosen revision per variant group (variantGroupId → active messageId). Unset groups default to newest.
	const [activeRevisionByGroup, setActiveRevisionByGroup] = useState<Record<string, string>>({});
	const [pendingFeedbackMessageId, setPendingFeedbackMessageId] = useState<string | undefined>();
	// Conversations whose first message has already promoted their title (avoids re-renaming on every send).
	const titledConversations = useRef<Set<string>>(new Set());
	const feedbackControlsEnabled = chatUiCapabilities.showConversationFeedbackControls;

	const conversationsQuery = useQuery({
		queryKey: nodeChatQueryKeys.conversationList(showArchivedConversations),
		queryFn: ({ signal }) => nodeChatAdapter.listConversations({ includeArchived: showArchivedConversations, signal }),
	});

	const localModelsQuery = useQuery({
		queryKey: localModelsQueryKeys.list(),
		queryFn: ({ signal }) => listLocalModels({ signal }),
	});

	const modelOptions = useMemo<ModelOption[]>(() => {
		const response = localModelsQuery.data;
		if (!response) {
			return [localDefaultModelOption];
		}

		return [localDefaultModelOption, ...response.items.map((model) => toModelOption(model, response.isAvailable))];
	}, [localModelsQuery.data]);
	const selectedModelOption = useMemo(
		() => modelOptions.find((option) => option.value === selectedModel),
		[modelOptions, selectedModel],
	);
	const selectedConcreteModelName = useMemo(() => {
		const requestModel = toNodeChatRequestModel(selectedModel);
		return requestModel ?? localModelsQuery.data?.selectedModelName ?? localModelsQuery.data?.configuredDefaultModelName ?? "";
	}, [localModelsQuery.data?.configuredDefaultModelName, localModelsQuery.data?.selectedModelName, selectedModel]);

	const selectedModelDetailsQuery = useQuery({
		queryKey: localModelsQueryKeys.details(selectedConcreteModelName),
		queryFn: ({ signal }) => getLocalModelDetails(selectedConcreteModelName, { signal }),
		enabled: selectedConcreteModelName.length > 0,
	});

	const createConversationMutation = useMutation({
		mutationFn: () => nodeChatAdapter.createConversation({ title: "New conversation" }),
		onSuccess: async (conversation) => {
			queryClient.setQueryData<ChatConversationModel[]>(nodeChatQueryKeys.conversationList(showArchivedConversations), (current = emptyConversations) => [
				conversation,
				...current,
			]);
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
	const activeConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
	// Remote conversations are view-only on this node (server enforces the guard; this is the cosmetic UI hide).
	const isRemoteConversation = activeConversation?.origin === "remote";

	const assistantMessageIds = useMemo(
		() =>
			(activeConversation?.messages ?? [])
				.filter((message) => message.role === "assistant" && message.content.trim().length > 0)
				.map((message) => message.id),
		[activeConversation?.messages],
	);
	// Node-local feedback for every assistant turn in the open conversation (Phase 5.3). GET 404 → no feedback yet.
	const feedbackQuery = useQuery({
		queryKey: nodeChatQueryKeys.feedback(selectedConversationId),
		queryFn: async ({ signal }) => {
			const entries = await Promise.all(
				assistantMessageIds.map(async (messageId) => nodeChatAdapter.getMessageFeedback(selectedConversationId, messageId, { signal })),
			);
			const byMessageId: Record<string, ChatMessageFeedback> = {};
			for (const entry of entries) {
				if (entry) {
					byMessageId[entry.messageId] = entry;
				}
			}
			return byMessageId;
		},
		enabled: feedbackControlsEnabled && !isRemoteConversation && selectedConversationId.length > 0 && assistantMessageIds.length > 0,
	});
	const feedbackByMessageId = feedbackQuery.data;
	const usedContextTokens = useMemo(() => deriveUsedContextTokens(activeConversation?.messages ?? []), [activeConversation?.messages]);
	const effectiveMaxContextTokens = selectedModelDetailsQuery.data?.maxContextTokens ?? undefined;
	const contextModelLabel = selectedConcreteModelName || selectedModelOption?.displayName || selectedModelOption?.label || "Local runtime default";
	const isLoadingInitialConversations = conversationsQuery.isLoading && displayConversations.length === 0;
	const isCreatingConversation = createConversationMutation.isPending;
	const isSending = Boolean(streamingMessage?.isActive);

	const cacheConversation = useCallback(
		(conversation: ChatConversationModel): void => {
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			queryClient.setQueryData<ChatConversationModel[]>(nodeChatQueryKeys.conversationList(showArchivedConversations), (current = emptyConversations) =>
				mergeSelectedConversation(current, conversation),
			);
		},
		[queryClient, showArchivedConversations],
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

			// Promote a placeholder-titled local conversation to a content-derived title on its first send,
			// using the rename endpoint (no in-place client title hack). Best-effort and silent — a failure
			// here must not block the send.
			const hasPriorUserMessage = conversation.messages.some((message) => message.role === "user");
			const hasPlaceholderTitle = conversation.origin !== "remote" && (conversation.title.trim().length === 0 || conversation.title.trim() === "New conversation");
			if (!hasPriorUserMessage && hasPlaceholderTitle && !titledConversations.current.has(conversation.id)) {
				titledConversations.current.add(conversation.id);
				try {
					conversation = await nodeChatAdapter.renameConversation(conversation.id, titleFromContent(content));
					cacheConversation(conversation);
				} catch {
					// Leave the placeholder title; the send proceeds regardless.
				}
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
					const failureCategory = error instanceof StreamWatchdogError ? error.category : undefined;
					const currentConversation = queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ?? optimisticConversation;
					const failed = markNodeChatStreamTerminated(currentConversation, ids.assistantMessageId, "failed", message, failureCategory);
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

	const regenerate = useCallback(
		async (assistantMessageId: string): Promise<void> => {
			if (activeStream.current) {
				return;
			}

			const conversation =
				queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(selectedConversationId)) ??
				displayConversations.find((item) => item.id === selectedConversationId);
			if (!conversation || conversation.origin === "remote") {
				setStreamError(t("pages.chat.actions.regenerateUnavailable", "Unable to regenerate this message."));
				return;
			}

			setStreamError(undefined);
			// Regenerate via the shared runner over the hub (Phase 5.2): the server mints a sibling variant and
			// drives + streams the run exactly like a send. The variant messageId + requestId arrive on the
			// events, so there is no client-known id up front; applyNodeChatStreamEvent appends the new variant,
			// and groupMessageRevisions later collapses it into the variant_group with prev/next nav.
			const abortController = new AbortController();
			activeStream.current = { conversationId: conversation.id, messageId: "", requestId: "", abortController };
			setStreamingMessage({ conversationId: conversation.id, messageId: "", content: "", isActive: true });

			try {
				for await (const streamEvent of nodeChatAdapter.regenerateMessage(conversation.id, assistantMessageId, abortController.signal)) {
					if (activeStream.current) {
						activeStream.current = { ...activeStream.current, messageId: streamEvent.messageId, requestId: streamEvent.requestId };
					}
					const currentConversation = queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ?? conversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					cacheConversation(applied.conversation);
					setStreamingMessage(applied.streamingMessage);
				}
				// The stream events don't carry variant_group_id; the post-stream refetch loads it from persistence
				// and groupMessageRevisions surfaces the newest sibling by default, so no explicit selection here.
			} catch (error) {
				if (!abortController.signal.aborted) {
					if (isNodeChatReadOnlyConflict(error)) {
						setStreamError(t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."));
					} else {
						setStreamError(errorMessage(error));
					}
				}
			} finally {
				const finishedMessageId = activeStream.current?.messageId;
				activeStream.current = null;
				setStreamingMessage((current) => (current?.messageId === finishedMessageId ? { ...current, isActive: false } : current));
				await refreshConversation(conversation.id);
			}
		},
		[cacheConversation, displayConversations, queryClient, refreshConversation, selectedConversationId, t],
	);

	const handleRegenerate = useCallback(
		(assistantMessageId: string): void => {
			regenerate(assistantMessageId).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[regenerate],
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

	const runConversationMutation = useCallback(
		async (conversationId: string, mutate: () => Promise<ChatConversationModel>): Promise<void> => {
			setStreamError(undefined);
			setMutatingConversationId(conversationId);
			try {
				const updated = await mutate();
				cacheConversation(updated);
				await refreshConversation(conversationId);
			} catch (error) {
				// Remote-origin conversations are view-only; the node rejects writes with a 409. Surface a clear
				// notice and refresh so the UI re-reflects authoritative (unchanged) server state.
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."));
					await refreshConversation(conversationId);
					return;
				}
				setStreamError(errorMessage(error));
			} finally {
				setMutatingConversationId(undefined);
			}
		},
		[cacheConversation, refreshConversation, t],
	);

	const handleRenameConversation = useCallback(
		(conversationId: string, title: string): void => {
			titledConversations.current.add(conversationId);
			runConversationMutation(conversationId, () => nodeChatAdapter.renameConversation(conversationId, title)).catch((error: unknown) =>
				setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
	);

	const handleToggleConversationPinned = useCallback(
		(conversationId: string, isPinned: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationPinned(conversationId, isPinned)).catch((error: unknown) =>
				setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
	);

	const handleToggleConversationArchived = useCallback(
		(conversationId: string, archived: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationArchived(conversationId, archived)).catch((error: unknown) =>
				setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
	);

	const handleSelectRevision = useCallback((variantGroupId: string, messageId: string): void => {
		setActiveRevisionByGroup((current) => ({ ...current, [variantGroupId]: messageId }));
	}, []);

	const branchConversation = useCallback(
		async (messageId: string): Promise<void> => {
			setStreamError(undefined);
			try {
				const result = await nodeChatAdapter.branchConversation(selectedConversationId, messageId);
				// Surface the branched Origin=Local conversation: refresh history and open it.
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
				setRequestedConversationId(result.branchedConversationId);
			} catch (error) {
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."));
					return;
				}
				setStreamError(errorMessage(error));
			}
		},
		[queryClient, selectedConversationId, t],
	);

	const handleBranch = useCallback(
		(messageId: string): void => {
			if (!selectedConversationId) {
				return;
			}

			branchConversation(messageId).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[branchConversation, selectedConversationId],
	);

	const submitFeedback = useCallback(
		async (messageId: string, rating: ChatFeedbackRating, comment: string | undefined): Promise<void> => {
			setStreamError(undefined);
			setPendingFeedbackMessageId(messageId);
			try {
				await nodeChatAdapter.setMessageFeedback(selectedConversationId, messageId, rating, comment);
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.feedback(selectedConversationId) });
			} catch (error) {
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."));
					return;
				}
				setStreamError(errorMessage(error));
			} finally {
				setPendingFeedbackMessageId(undefined);
			}
		},
		[queryClient, selectedConversationId, t],
	);

	const handleSubmitFeedback = useCallback(
		(messageId: string, rating: ChatFeedbackRating, comment: string | undefined): void => {
			if (!selectedConversationId) {
				return;
			}

			submitFeedback(messageId, rating, comment).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[selectedConversationId, submitFeedback],
	);

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
			) : isRemoteConversation ? (
				<Stack gap={2}>
					<Text fw={700}>{t("pages.chat.remoteViewOnlyTitle", "Remote conversation")}</Text>
					<Text size="sm">{t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node.")}</Text>
				</Stack>
			) : (
				`Local conversation history is loaded from the node API. Send, stream, and cancel are wired to the local SignalR hub. Node mode hides ${hiddenNodeSurfaces}.`
			),
		[conversationsQuery.error, conversationsQuery.isError, isRemoteConversation, streamError, t],
	);

	return (
		<Box py="lg" style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
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
					usedTokens: usedContextTokens,
					maxTokens: effectiveMaxContextTokens,
					isAuthoritative: usedContextTokens !== undefined,
					modelLabel: contextModelLabel,
					nodeLabel: "Local node",
				}}
				streamingMessage={streamingMessage}
				disabledNotice={notice}
				inputStatus={{
					isSending,
					chatInputDisabled: isCreatingConversation || isRemoteConversation,
					modelSelectorDisabled: isRemoteConversation,
					sendDisabled: selectedConversationQuery.isLoading || isRemoteConversation,
				}}
				conversationSearchQuery={conversationSearchQuery}
				showArchivedConversations={showArchivedConversations}
				mutatingConversationId={mutatingConversationId}
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
				onRegenerate={isRemoteConversation ? undefined : handleRegenerate}
				onConversationSearchChange={setConversationSearchQuery}
				onToggleShowArchivedConversations={setShowArchivedConversations}
				onRenameConversation={handleRenameConversation}
				onToggleConversationPinned={handleToggleConversationPinned}
				onToggleConversationArchived={handleToggleConversationArchived}
				onBranchFromMessage={isRemoteConversation ? undefined : handleBranch}
				activeRevisionByGroup={activeRevisionByGroup}
				onSelectRevision={handleSelectRevision}
				feedbackByMessageId={feedbackByMessageId}
				pendingFeedbackMessageId={pendingFeedbackMessageId}
				onSubmitFeedback={isRemoteConversation ? undefined : handleSubmitFeedback}
			/>
		</Box>
	);
}
