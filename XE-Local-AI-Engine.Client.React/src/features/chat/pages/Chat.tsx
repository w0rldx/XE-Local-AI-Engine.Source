import { Alert, Button, Center, Loader, Stack, Text, VisuallyHidden } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useCallback, useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { buildChatUiCapabilities } from "@/core/chat/models/ChatCapabilityGates";
import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { useCommands } from "@/core/commands/queries/useCommands";
import type { ChatScope } from "@/core/models/ChatScope";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { useNodeChatConnectionReadiness } from "@/features/chat/api/useNodeChatConnectionReadiness";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { type ChatSetupDownload, chatNotice } from "@/features/chat/components/ChatNotice";
import { useChatAgentSelection } from "@/features/chat/hooks/useChatAgentSelection";
import { useChatConversationActions } from "@/features/chat/hooks/useChatConversationActions";
import { emptyConversationList, useChatConversationCache } from "@/features/chat/hooks/useChatConversationCache";
import { useChatConversationSelection } from "@/features/chat/hooks/useChatConversationSelection";
import { useChatMessageActions } from "@/features/chat/hooks/useChatMessageActions";
import { useChatModelSelection } from "@/features/chat/hooks/useChatModelSelection";
import { useChatRevisionSelection } from "@/features/chat/hooks/useChatRevisionSelection";
import { useChatStreamController } from "@/features/chat/hooks/useChatStreamController";
import { inFlightAssistantMessageId } from "@/features/chat/models/ChatConversationDerivations";
import { chatErrorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatConversationListModel } from "@/features/chat/models/ChatModels";
import { effectiveAgent } from "@/features/chat/models/ChatTurnResolution";
import { toChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { useConversationAttachments } from "@/features/chat/queries/useConversationAttachments";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { forgetWebContentAutoAccept } from "@/features/chat/stores/WebContentAutoAcceptStore";
import { useChatWorkflow } from "@/features/chat/workflow/useChatWorkflow";
import { useKnowledgeDocuments } from "@/features/knowledge/queries/useKnowledgeDocuments";
import { useVoicePlayback } from "@/features/voice/hooks/useVoicePlayback";
import { useVoiceRuntime } from "@/features/voice/hooks/VoiceRuntimeContext";

/* eslint-disable react-doctor/no-giant-component -- This page is the chat orchestrator: hook wiring whose order is load-bearing, plus the prop assembly ChatDisplayShell needs in one place. The race-coupled send/regenerate/resume/cancel loops live in useChatStreamController; the cache writers, conversation and message actions, notice, and transport, model, agent and revision-selection lifecycles live in their own modules. */

const orphanedLiveTurnPollIntervalMs = 3_000;

export function Chat({ scope, setupDownload }: { scope?: ChatScope; setupDownload?: ChatSetupDownload } = {}) {
	const { t } = useTranslation();
	// Owner-embedded mode (a work session pinning its own conversation). `/chat` passes no scope and every branch
	// below takes its unpinned path.
	const isScoped = scope !== undefined;
	const queryClient = useQueryClient();
	const commandsQuery = useCommands();
	const commandOptions = useMemo(() => (commandsQuery.data ?? []).map(toChatCommandOption), [commandsQuery.data]);
	// Composer selections, the last-selected conversation, and the sidebar collapsed state all persist across
	// reloads via localStorage (NodeChatPreferencesStore), mirroring the platform ToolCallingStore. Persisted
	// values are validated below: the model against the live model list / effort set (useChatModelSelection), and
	// the last-selected conversation against the loaded list (a stale id falls back to the first conversation).
	const toolsEnabled = useNodeChatPreferencesStore((state) => state.toolsEnabled);
	const knowledgeBaseEnabled = useNodeChatPreferencesStore((state) => state.knowledgeBaseEnabled);
	const requestedConversationId = useNodeChatPreferencesStore((state) => state.selectedConversationId);
	const collapsed = useNodeChatPreferencesStore((state) => state.sidebarCollapsed);
	const {
		toggleTools,
		toggleKnowledgeBase,
		setSelectedConversationId: setSelectedConversationIdPreference,
		toggleSidebar,
		clearSelectedAgent,
	} = useNodeChatPreferencesStore((state) => state.actions);
	// The preference store is GLOBAL: opening a scoped (owner-pinned) conversation must never rewrite the operator's
	// remembered `/chat` thread, so every selection write becomes a no-op under scope.
	const setRequestedConversationId = useCallback(
		(conversationId: string) => {
			if (isScoped) {
				return;
			}
			setSelectedConversationIdPreference(conversationId);
		},
		[isScoped, setSelectedConversationIdPreference],
	);
	// A deep link from another surface (an agent run naming the conversation it ran in) arrives through the core
	// hand-off store rather than the URL, and is consumed once — a remount must never re-select it over whatever the
	// operator has picked since. Consuming it under scope without acting is deliberate: an owner-pinned conversation
	// ignores a `/chat` deep link, and leaving the id pending would make it fire at some later, unrelated mount.
	const consumePendingConversationId = usePendingChatConversationStore((state) => state.actions.consume);
	useEffect(() => {
		const pendingConversationId = consumePendingConversationId();
		if (pendingConversationId.length > 0) {
			setRequestedConversationId(pendingConversationId);
		}
	}, [consumePendingConversationId, setRequestedConversationId]);
	// Voice runtime: the node setting drives showVoiceControls; the playback tap mirrors the stream into Web Speech.
	// Only the streaming loops call the tap, but it is instantiated here: `src/features/voice` is another feature and
	// this page is the sole chat module dependency-cruiser carries that edge for.
	const voiceRuntime = useVoiceRuntime();
	const { onTurnStart: onVoiceTurnStart, onAnswerProgress: onVoiceAnswerProgress } = useVoicePlayback();
	const chatUiCapabilities = useMemo(
		() => buildChatUiCapabilities(nodeCapabilities.chat, voiceRuntime.enabled),
		[voiceRuntime.enabled],
	);
	// Knowledge-base documents drive whether the composer's "Use Knowledge Base" toggle is enabled: grounding on an
	// empty corpus is a no-op, so the toggle stays visible but disabled until at least one document is INDEXED. The
	// list is fetched only when the KB surface is on (Chat is authed-mounted, so this gate matches the feature gate);
	// an indexed doc is one that is Ready to search — status Indexed, or any row still serving last-known-good chunks.
	// Plain-chat grounding currently targets the backwards-compatible DEFAULT collection. Scope availability to that
	// same namespace so documents in project collections never enable a toggle whose backend search would return none.
	const { data: knowledgeDocuments } = useKnowledgeDocuments(chatUiCapabilities.showKnowledgeBaseControls, "DEFAULT");
	const knowledgeBaseHasDocuments = useMemo(
		() => (knowledgeDocuments ?? []).some((document) => document.status === "Indexed" || document.chunkCount > 0),
		[knowledgeDocuments],
	);
	const { readiness: connectionReadiness, error: connectionError, retry: retryConnection } = useNodeChatConnectionReadiness();
	const {
		conversationsIsLoading,
		conversationsIsError,
		conversationsError,
		maxMessageSizeKb,
		maxUploadFileSizeMb,
		showArchivedConversations,
		setShowArchivedConversations,
		conversationListQueryKey,
		isConversationListTruncated,
		showAllConversations,
		selectedConversationId,
		selectedConversationData,
		selectedConversationIsLoading,
		selectedConversationIsPlaceholderData,
		selectedConversationError,
		selectedConversationLoadFailed,
		isLoadingSelectedConversation,
		handleRetryLoadMessages,
		displayConversations,
		isRemoteConversation,
		feedbackByMessageId,
		usedContextTokens,
		contextWindow,
	} = useChatConversationSelection({ scope, requestedConversationId });
	const {
		agentModeEnabled,
		conversationAgentBound,
		selectedAgentId,
		agentOptions,
		agentControlsAvailable,
		boundAgentMemoryEnabled,
		pinnedAgentModelProfile,
		scopedModelPending,
		handleSelectAgent,
	} = useChatAgentSelection(
		scope,
		chatUiCapabilities.showAgentControls,
		selectedConversationData?.id === selectedConversationId ? selectedConversationData?.agentDefinitionId : undefined,
	);
	const {
		modelOptions,
		cloudModelOptions,
		selectedModel,
		reasoningEffort,
		availableReasoningEfforts,
		activeModelToolCapable,
		activeModelMultimodal,
		showNoModelGuidance,
		effectiveMaxContextTokens,
		contextModelLabel,
		setSelectedModel,
		setReasoningEffort,
	} = useChatModelSelection({
		isScoped,
		pinnedAgentId: scope?.pinnedAgentId,
		pinnedAgentModelProfile,
		scopedModelPending,
	});
	// A plain conversation runs no agent: the send carries no agent pick, which the server would reject.
	const selectedConversationPlain =
		selectedConversationData?.id === selectedConversationId && selectedConversationData?.isPlain === true;
	const sendAgentModeEnabled = agentModeEnabled && !selectedConversationPlain;
	// The agent the next request resolves, by the send path's rule: agent mode on and the agent still listed.
	const contextAgentLabel = effectiveAgent(sendAgentModeEnabled, selectedAgentId, agentOptions)?.name;
	const [streamError, setStreamError] = useState<string | undefined>();
	const [conversationSearchQuery, setConversationSearchQuery] = useState("");

	const createConversationMutation = useMutation({
		mutationFn: () => nodeChatAdapter.createConversation({ title: "New conversation" }),
		onSuccess: async (conversation) => {
			queryClient.setQueryData<ChatConversationListModel>(conversationListQueryKey, (current = emptyConversationList) => ({
				...current,
				conversations: [conversation, ...current.conversations],
			}));
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			setRequestedConversationId(conversation.id);
			// A new conversation must not inherit the previous thread's pinned agent — clear the
			// selection so the composer starts clean. Model stays sticky (intentional UX).
			clearSelectedAgent();
			// Nor the draft's web content choice: that belonged to the draft the user was typing in, not to a clean thread.
			forgetWebContentAutoAccept("");
			// List reconciliation only: the created conversation's detail was just primed via setQueryData with the
			// authoritative create response — the broad `conversations()` prefix would immediately re-mark it (and
			// every other cached detail) stale for no new information.
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() });
		},
	});

	const loadedConversation = selectedConversationData;
	const { activeRevisionByGroup, selectRevision } = useChatRevisionSelection(selectedConversationId, loadedConversation);

	const isLoadingInitialConversations = conversationsIsLoading && displayConversations.length === 0;
	const isCreatingConversation = createConversationMutation.isPending;

	// Stable identity so the memoized ConversationList isn't re-rendered every streaming token by a fresh inline
	// arrow. `.mutate` is referentially stable across renders; the guard reads the current pending flag.
	const createConversationMutate = createConversationMutation.mutate;
	const handleCreateConversation = useCallback(() => {
		if (!isCreatingConversation) {
			createConversationMutate();
		}
	}, [isCreatingConversation, createConversationMutate]);
	// Home's "New chat" arrives through the same hand-off store and runs this page's own create path, so the new thread
	// gets the same title, cleared agent and cleared web choice as the sidebar button. Consumed under scope without
	// acting, like the pending id above.
	const consumeNewConversationRequest = usePendingChatConversationStore((state) => state.actions.consumeNewConversationRequest);
	useEffect(() => {
		if (consumeNewConversationRequest() && !isScoped) {
			createConversationMutate();
		}
	}, [consumeNewConversationRequest, createConversationMutate, isScoped]);

	const { cacheConversationDetail, cacheConversation, resolveSendConversation, refreshConversation } = useChatConversationCache({
		conversationListQueryKey,
		selectedConversationId,
		selectedConversationData,
		selectedConversationIsPlaceholderData,
		displayConversations,
		setRequestedConversationId,
	});

	// Resolve the conversation a file should attach to. When a conversation is already on screen — including a
	// freshly-created empty one from "New chat" — attach to IT directly. Going through the create-or-load
	// path here would race an in-flight conversation creation (the new thread's full payload hasn't settled yet)
	// and spawn a duplicate empty conversation. Only when nothing is selected (true empty state) do we lazily
	// create one, mirroring the send path.
	const ensureConversationId = useCallback(async (): Promise<string> => {
		if (selectedConversationId.length > 0) {
			return selectedConversationId;
		}
		try {
			const conversation = await resolveSendConversation("");
			return conversation.id;
		} catch (error) {
			setStreamError(chatErrorMessage(error));
			return "";
		}
	}, [selectedConversationId, resolveSendConversation]);

	const {
		attachments,
		attachmentFileIds,
		pendingUploads,
		uploadFiles: handleUploadAttachments,
		removeAttachment: handleRemoveAttachment,
	} = useConversationAttachments({ conversationId: selectedConversationId, ensureConversationId, maxUploadFileSizeMb });

	// The conversation whose FULL payload has loaded — id-matched, so never a keepPreviousData placeholder from the
	// thread we just switched away from. The re-attach waits for it because the resumed events are folded onto the
	// persisted in-flight assistant row.
	const loadedSelectedConversationId = selectedConversationData?.id === selectedConversationId ? selectedConversationId : "";

	// Send, regenerate, cold-load resume and cancel, plus the mutable cluster those four share: the `activeStream`
	// mutual-exclusion token every loop parks its AbortController in, the deleted-conversation guard set, and the
	// per-frame commit scheduler. Declared at this exact position so the hook's layout effect and re-attach effect keep
	// their order relative to every other hook on this page.
	const {
		streamingMessage,
		timelineEntries,
		handleSend,
		handleRegenerate,
		handleCancel,
		markConversationDeleted,
		unmarkConversationDeleted,
		markConversationTitled,
		forgetConversationTitle,
	} = useChatStreamController({
		setStreamError,
		cacheConversation,
		cacheConversationDetail,
		refreshConversation,
		resolveSendConversation,
		selectedConversationId,
		displayConversations,
		modelOptions,
		cloudModelOptions,
		agentModeEnabled: sendAgentModeEnabled,
		selectedAgentId,
		agentOptions,
		activeRevisionByGroup,
		attachmentFileIds,
		toolsEnabled,
		knowledgeBaseEnabled,
		reasoningEffort,
		onVoiceTurnStart,
		onVoiceAnswerProgress,
		loadedSelectedConversationId,
		resumeNonce: scope?.resumeNonce,
	});

	// A remount (or a resume that ended early) leaves no local stream while the server run goes on. The persisted in-flight
	// row with the request id Stop cancels by still counts as sending, so Stop stays reachable and the composer stays
	// blocked, as the server would refuse a send into the live turn anyway.
	const loadedSelectedConversation = loadedSelectedConversationId ? selectedConversationData : undefined;
	const inFlightMessageId = loadedSelectedConversation && inFlightAssistantMessageId(loadedSelectedConversation);
	const hasPersistedLiveTurn = Boolean(
		inFlightMessageId &&
			loadedSelectedConversation?.messages.some((message) => message.id === inFlightMessageId && message.requestId),
	);
	const isStreamingLocally = Boolean(streamingMessage?.isActive);
	const isSending = isStreamingLocally || hasPersistedLiveTurn;
	// Nothing else observes such a turn finishing: a stream that ended without a terminal event refreshes once, and the
	// detail query does not poll. Re-read the conversation until the row turns terminal; never while a local stream
	// owns the cache, as a refetch would fight its per-frame commits.
	useEffect(() => {
		if (!hasPersistedLiveTurn || isStreamingLocally) {
			return;
		}
		// A tick skips while the previous refresh is pending: invalidation cancels an in-flight fetch, so a read slower than
		// the interval would otherwise be restarted forever and never land.
		let refreshing = false;
		const timer = setInterval(() => {
			if (refreshing) {
				return;
			}
			refreshing = true;
			refreshConversation(loadedSelectedConversationId)
				.catch(() => undefined)
				.finally(() => {
					refreshing = false;
				});
		}, orphanedLiveTurnPollIntervalMs);
		return () => clearInterval(timer);
	}, [hasPersistedLiveTurn, isStreamingLocally, loadedSelectedConversationId, refreshConversation]);
	// Chat workflow mode. Off for a scoped (owner-pinned) conversation: its owner is the single writer of that thread.
	const workflow = useChatWorkflow({
		enabled: nodeCapabilities.graphWorkflows && !isScoped,
		conversationId: selectedConversationId,
		resolveSendConversation,
		cacheConversation,
		markConversationTitled,
		attachmentFileIds,
		isSending,
		plain: selectedConversationPlain,
	});

	const {
		mutatingConversationId,
		handleRenameConversation,
		handleToggleConversationPinned,
		handleToggleConversationArchived,
		handleToggleConversationMemoryExcluded,
		handleSetConversationPlain,
		handleDeleteConversation,
	} = useChatConversationActions({
		cacheConversation,
		refreshConversation,
		setStreamError,
		requestedConversationId,
		setRequestedConversationId,
		markConversationTitled,
		forgetConversationTitle,
		markConversationDeleted,
		unmarkConversationDeleted,
	});

	const { handleSelectRevision, handleBranch, handleSubmitFeedback, pendingFeedbackMessageId } = useChatMessageActions({
		selectedConversationId,
		activeRevisionByGroup,
		selectRevision,
		setRequestedConversationId,
		setStreamError,
	});

	// Only an actual error / remote-view-only condition surfaces a notice; the always-on informational banner
	// was dropped. When undefined, ChatDisplayShell renders no alert.
	const notice = chatNotice(
		{ streamError, conversationsIsError, conversationsError, isRemoteConversation, showNoModelGuidance, setupDownload },
		t,
	);

	// Module-readiness gate (platform parity): block the chat behind a connecting/error state until the
	// shared hub is live. Once connected it latches `ready` and transient reconnects are handled in-band.
	if (connectionReadiness !== "ready") {
		return (
			<ChatFrame embedded={scope?.embedded === true}>
				<Center style={{ flex: 1 }}>
					{connectionReadiness === "connecting" ? (
						<Stack align="center" gap="sm">
							<Loader />
							<Text c="dimmed">{t("pages.chat.connecting", "Connecting to local chat…")}</Text>
						</Stack>
					) : (
						<InlineErrorAlert
							variant="light"
							title={t("pages.chat.connectionFailedTitle", "Local chat unavailable")}
							message={connectionError ?? t("pages.chat.connectionFailed", "Could not connect to the local chat hub.")}
						>
							{/* Retry re-arms connect(): readiness flips error → connecting and this whole gate re-renders to
							    the centered Loader above. Accepted as-is — no in-button spinner; the connecting state IS
							    the feedback, and the gate swap is a clean replace, not a flicker. */}
							<Button size="xs" variant="light" onClick={retryConnection}>
								{t("pages.chat.retryConnection", "Retry")}
							</Button>
						</InlineErrorAlert>
					)}
				</Center>
			</ChatFrame>
		);
	}

	return (
		<ChatFrame embedded={scope?.embedded === true}>
			{isLoadingInitialConversations ? (
				<Alert color="blue" variant="light" icon={<Loader size={16} />}>
					{t("pages.chat.loadingHistory", "Loading local chat history…")}
				</Alert>
			) : null}
			{createConversationMutation.isError ? (
				<InlineErrorAlert variant="light" mb="md" message={chatErrorMessage(createConversationMutation.error)} />
			) : null}
			<ChatDisplayShell
				conversations={displayConversations}
				selectedConversationId={selectedConversationId}
				modelOptions={modelOptions}
				cloudModelOptions={cloudModelOptions}
				selectedModel={selectedModel}
				reasoningEffort={reasoningEffort}
				availableReasoningEfforts={availableReasoningEfforts}
				activeModelToolCapable={activeModelToolCapable}
				activeModelMultimodal={activeModelMultimodal}
				toolsEnabled={toolsEnabled}
				knowledgeBaseEnabled={knowledgeBaseEnabled}
				knowledgeBaseHasDocuments={knowledgeBaseHasDocuments}
				capabilities={chatUiCapabilities}
				contextUsage={{
					usedTokens: usedContextTokens,
					maxTokens: effectiveMaxContextTokens,
					isAuthoritative: usedContextTokens !== undefined,
					modelLabel: contextModelLabel,
					nodeLabel: t("pages.chat.contextUsage.localNode", "Local node"),
					agentLabel: contextAgentLabel,
					contextWindow,
				}}
				maxMessageSizeKb={maxMessageSizeKb}
				streamingMessage={streamingMessage}
				timelineEntries={timelineEntries}
				disabledNotice={notice}
				isLoadingMessages={isLoadingSelectedConversation}
				messagesLoadFailed={selectedConversationLoadFailed}
				messagesLoadErrorText={selectedConversationLoadFailed ? chatErrorMessage(selectedConversationError) : undefined}
				onRetryLoadMessages={handleRetryLoadMessages}
				inputStatus={{
					isSending,
					chatInputDisabled: isCreatingConversation || isRemoteConversation || scope?.composerDisabled === true,
					// A scoped session pins the agent, and the agent pins the model — both selectors read-only.
					// In workflow mode the workflow's nodes pick their own models and agents.
					modelSelectorDisabled: isRemoteConversation || isScoped || workflow.active,
					agentSelectorDisabled: isScoped || workflow.active,
					sendDisabled: selectedConversationIsLoading || isRemoteConversation || scope?.composerDisabled === true,
				}}
				conversationSearchQuery={conversationSearchQuery}
				showArchivedConversations={showArchivedConversations}
				conversationListTruncated={isConversationListTruncated}
				onShowAllConversations={showAllConversations}
				mutatingConversationId={mutatingConversationId}
				conversationListCollapsed={collapsed}
				hideConversationList={isScoped}
				isWorkSessionConversation={isScoped}
				onSelectConversation={setRequestedConversationId}
				onCreateConversation={handleCreateConversation}
				onToggleConversationList={toggleSidebar}
				onModelChange={setSelectedModel}
				onReasoningEffortChange={setReasoningEffort}
				onToggleTools={toggleTools}
				onToggleKnowledgeBase={toggleKnowledgeBase}
				agentControlsAvailable={agentControlsAvailable}
				agentModeEnabled={agentModeEnabled}
				conversationAgentBound={conversationAgentBound}
				selectedAgentId={selectedAgentId}
				agentOptions={agentOptions}
				commandOptions={commandOptions}
				onSelectAgent={handleSelectAgent}
				attachments={attachments}
				pendingUploads={pendingUploads}
				onUploadFiles={handleUploadAttachments}
				onRemoveAttachment={handleRemoveAttachment}
				onSend={(content, effort, model) => {
					// The owner's supervisor is the single writer of invocations on a scoped conversation, so the
					// composer posts through the override instead of starting a second, unsupervised turn. The
					// returned promise is what defers ChatInputArea's draft clear until the post is accepted.
					if (scope?.onSendOverride) {
						return scope.onSendOverride(content);
					}
					// Workflow mode posts to the bound workflow and hands back the promise, so a refused send keeps the draft.
					if (workflow.active) {
						return workflow.send(content);
					}
					const normalSend = (): void => {
						handleSend(content, effort, model).catch((error: unknown) => setStreamError(chatErrorMessage(error)));
					};
					// Not yet known whether this conversation is bound to a workflow: the send waits for that answer.
					if (workflow.bindingUnresolved) {
						return workflow.sendWhenResolved(content, normalSend);
					}
					normalSend();
					return undefined;
				}}
				onCancel={() => {
					if (scope?.onStopOverride) {
						scope.onStopOverride();
						return;
					}
					handleCancel().catch((error: unknown) => setStreamError(chatErrorMessage(error)));
				}}
				onRegenerate={isScoped || isRemoteConversation ? undefined : handleRegenerate}
				onConversationSearchChange={setConversationSearchQuery}
				onToggleShowArchivedConversations={setShowArchivedConversations}
				onRenameConversation={isScoped ? undefined : handleRenameConversation}
				onToggleConversationPinned={isScoped ? undefined : handleToggleConversationPinned}
				onToggleConversationArchived={isScoped ? undefined : handleToggleConversationArchived}
				boundAgentMemoryEnabled={boundAgentMemoryEnabled}
				onToggleConversationMemoryExcluded={handleToggleConversationMemoryExcluded}
				onSetConversationPlain={isScoped || isRemoteConversation ? undefined : handleSetConversationPlain}
				onDeleteConversation={isScoped ? undefined : handleDeleteConversation}
				onBranchFromMessage={isScoped || isRemoteConversation ? undefined : handleBranch}
				activeRevisionByGroup={activeRevisionByGroup}
				onSelectRevision={handleSelectRevision}
				feedbackByMessageId={feedbackByMessageId}
				pendingFeedbackMessageId={pendingFeedbackMessageId}
				onSubmitFeedback={isScoped || isRemoteConversation ? undefined : handleSubmitFeedback}
				workflow={workflow.slots}
			/>
		</ChatFrame>
	);
}

// The chat page normally claims the Layout scroll container's full height; an embedded scope's parent already owns
// that frame (and its own padding), so `Chat` renders bare inside it.
//
// The frame is also where the page's `<h1>` lives. Chat shows no title of its own — the conversation list is the
// subject — so the heading is visually hidden and carries the navigation label, first inside the container so the
// flex column is untouched. It belongs to the FRAME, not to `Chat`: an embedded scope renders inside a page that
// already has its own h1, and a second one would be worse than none.
function ChatFrame({ embedded, children }: { embedded: boolean; children: ReactNode }) {
	const { t } = useTranslation();
	if (embedded) {
		return <>{children}</>;
	}
	return (
		<FullHeightPage data-tour="chat-overview">
			<VisuallyHidden component="h1">{t("navigation.chat", "Chat")}</VisuallyHidden>
			{children}
		</FullHeightPage>
	);
}
