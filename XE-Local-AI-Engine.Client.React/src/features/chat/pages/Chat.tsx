import { Alert, Anchor, Button, Center, Loader, Stack, Text } from "@mantine/core";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { type ReactNode, useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { isNodeChatReadOnlyConflict } from "@/features/chat/api/NodeChatConflict";
import { useNodeChatConnectionReadiness } from "@/features/chat/api/useNodeChatConnectionReadiness";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { useChatAgentSelection } from "@/features/chat/hooks/useChatAgentSelection";
import { useChatConversationSelection } from "@/features/chat/hooks/useChatConversationSelection";
import { useChatModelSelection } from "@/features/chat/hooks/useChatModelSelection";
import { useChatRevisionSelection } from "@/features/chat/hooks/useChatRevisionSelection";
import { useChatStreamController } from "@/features/chat/hooks/useChatStreamController";
import { buildChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import { mergeSelectedConversation, titleFromContent } from "@/features/chat/models/ChatConversationDerivations";
import { errorMessage } from "@/features/chat/models/ChatErrorMessage";
import type {
	ChatConversationListModel,
	ChatConversationModel,
	ChatFeedbackRating,
	ChatScope,
} from "@/features/chat/models/ChatModels";
import { toChatCommandOption } from "@/features/chat/models/SlashCommandModels";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useConversationAttachments } from "@/features/chat/queries/useConversationAttachments";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { useCommands } from "@/features/commands/queries/useCommands";
import { useKnowledgeDocuments } from "@/features/knowledge/queries/useKnowledgeDocuments";
import { useVoicePlayback } from "@/features/voice/useVoicePlayback";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

/* eslint-disable react-doctor/no-giant-component -- This page is the chat orchestrator: conversation CRUD plus the prop assembly ChatDisplayShell needs in one place. The race-coupled send/regenerate/resume/cancel loops now live in useChatStreamController; transport, model, agent and revision-selection lifecycles live in their own modules. */

// Fallback for the cache updaters below when they run before the list query has landed: an empty list with no known
// message-size limit (which simply means the composer runs no size pre-check until the real fetch arrives).
const emptyConversationList: ChatConversationListModel = { conversations: [] };

export function Chat({ scope }: { scope?: ChatScope } = {}) {
	const { t } = useTranslation();
	// Owner-embedded mode (a work session pinning its own conversation). `/chat` passes no scope and every branch
	// below falls back to the exact behaviour it had before the prop existed.
	const isScoped = scope !== undefined;
	const { confirm } = useConfirm();
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
		agentModeEnabled,
		selectedAgentId,
		agentOptions,
		agentControlsAvailable,
		boundAgentMemoryEnabled,
		pinnedAgentModelProfile,
		scopedModelPending,
		handleSelectAgent,
	} = useChatAgentSelection(scope, chatUiCapabilities.showAgentControls);
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
	const [streamError, setStreamError] = useState<string | undefined>();
	const [conversationSearchQuery, setConversationSearchQuery] = useState("");
	const [mutatingConversationId, setMutatingConversationId] = useState<string | undefined>();
	const [pendingFeedbackMessageId, setPendingFeedbackMessageId] = useState<string | undefined>();
	const {
		conversationsIsLoading,
		conversationsIsError,
		conversationsError,
		maxMessageSizeKb,
		showArchivedConversations,
		setShowArchivedConversations,
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
	} = useChatConversationSelection({ scope, requestedConversationId });

	const createConversationMutation = useMutation({
		mutationFn: () => nodeChatAdapter.createConversation({ title: "New conversation" }),
		onSuccess: async (conversation) => {
			queryClient.setQueryData<ChatConversationListModel>(
				nodeChatQueryKeys.conversationList(showArchivedConversations),
				(current = emptyConversationList) => ({ ...current, conversations: [conversation, ...current.conversations] }),
			);
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			setRequestedConversationId(conversation.id);
			// A new conversation must not inherit the previous thread's pinned agent — clear the
			// selection so the composer starts clean. Model stays sticky (intentional UX).
			clearSelectedAgent();
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

	// Write only the selected-conversation detail cache. Used for per-frame streaming commits: the sidebar has
	// nothing to show for an in-flight turn, so folding the growing conversation into the whole list every frame
	// (mergeSelectedConversation is O(list)) is wasted work. The list is refreshed on terminal events instead.
	const cacheConversationDetail = useCallback(
		(conversation: ChatConversationModel): void => {
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
		},
		[queryClient],
	);

	const cacheConversation = useCallback(
		(conversation: ChatConversationModel): void => {
			cacheConversationDetail(conversation);
			queryClient.setQueryData<ChatConversationListModel>(
				nodeChatQueryKeys.conversationList(showArchivedConversations),
				(current = emptyConversationList) => ({
					...current,
					conversations: mergeSelectedConversation(current.conversations, conversation),
				}),
			);
		},
		[cacheConversationDetail, queryClient, showArchivedConversations],
	);

	const resolveSendConversation = useCallback(
		async (content: string): Promise<ChatConversationModel> => {
			// Only trust the cached selected-conversation payload when it actually belongs to the CURRENT
			// selection AND is not a keepPreviousData placeholder from the thread we just switched away from.
			// A fast switch to an uncached conversation followed by Enter would otherwise resolve
			// the PREVIOUS conversation's payload here and send the turn to the wrong id. When it's stale/
			// placeholder, fall through to the load-by-id path, which fetches the currently-selected thread.
			if (
				selectedConversationData &&
				selectedConversationData.id === selectedConversationId &&
				!selectedConversationIsPlaceholderData
			) {
				return selectedConversationData;
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
		[
			cacheConversation,
			displayConversations,
			selectedConversationId,
			selectedConversationData,
			selectedConversationIsPlaceholderData,
			setRequestedConversationId,
		],
	);

	// Resolve the conversation a file should attach to. When a conversation is already on screen — including a
	// freshly-created empty one from "New plain chat" — attach to IT directly. Going through the create-or-load
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
			setStreamError(errorMessage(error));
			return "";
		}
	}, [selectedConversationId, resolveSendConversation]);

	const {
		attachments,
		attachmentFileIds,
		pendingUploads,
		uploadFiles: handleUploadAttachments,
		removeAttachment: handleRemoveAttachment,
	} = useConversationAttachments({ conversationId: selectedConversationId, ensureConversationId });

	const refreshConversation = useCallback(
		async (conversationId: string): Promise<void> => {
			// Scoped tightly — this runs after EVERY turn. `exact` keeps the conversation's `files` child out (a turn
			// never changes the attachment list; upload/delete invalidate their own key), and `conversationLists`
			// refreshes the sidebar without the broad `conversations()` prefix that would re-invalidate this same
			// detail mid-refetch (the observed duplicate round per turn) plus every other cached conversation.
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId), exact: true }),
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() }),
				// The local runtime only fills EffectiveContextTokens once the model is WARM, but the
				// model-details query fires pre-warm — so the context-usage meter would stay pinned to the model's
				// train ceiling (e.g. 262k) even though the server launched with a far smaller `-c` (e.g. 16k). Once
				// a turn reaches a terminal state the model is warm, so invalidate the details query to re-read the
				// real window. Partial-object match on the hey-api single-element key so it invalidates every model
				// path (never `.slice()` a hey-api key).
				// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
				queryClient.invalidateQueries({ queryKey: [{ _id: "getLocalModelDetails" }] }),
			]);
		},
		[queryClient],
	);

	// The conversation whose FULL payload has loaded — id-matched, so never a keepPreviousData placeholder from the
	// thread we just switched away from. The re-attach waits for it because the resumed events are folded onto the
	// persisted in-flight assistant row.
	const loadedSelectedConversationId = selectedConversationData?.id === selectedConversationId ? selectedConversationId : "";

	// Send, regenerate, cold-load resume and cancel, plus the mutable cluster those four share: the `activeStream`
	// mutual-exclusion token every loop parks its AbortController in, the deleted-conversation guard set, and the
	// per-frame commit scheduler. Declared exactly where `handleSend` used to be — everything between here and the old
	// cancel callback WAS that cluster, so the hook's layout effect and re-attach effect keep their position relative
	// to every other hook on this page.
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
		agentModeEnabled,
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

	const isSending = Boolean(streamingMessage?.isActive);

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
					setStreamError(
						t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."),
					);
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
			markConversationTitled(conversationId);
			runConversationMutation(conversationId, () => nodeChatAdapter.renameConversation(conversationId, title)).catch(
				(error: unknown) => setStreamError(errorMessage(error)),
			);
		},
		[markConversationTitled, runConversationMutation],
	);

	const handleToggleConversationPinned = useCallback(
		(conversationId: string, isPinned: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationPinned(conversationId, isPinned)).catch(
				(error: unknown) => setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
	);

	const handleToggleConversationArchived = useCallback(
		(conversationId: string, archived: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationArchived(conversationId, archived)).catch(
				(error: unknown) => setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
	);

	// Toggle a conversation "temporary" (memory-excluded). A temporary conversation still USES existing memory; it
	// just won't teach the agent new memory from this thread. PATCHes the conversation via the same mutation path as
	// pin/archive so the cache + selected-conversation query refresh from authoritative server state.
	const handleToggleConversationMemoryExcluded = useCallback(
		(conversationId: string, memoryExcluded: boolean): void => {
			runConversationMutation(conversationId, () =>
				nodeChatAdapter.setConversationMemoryExcluded(conversationId, memoryExcluded),
			).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[runConversationMutation],
	);

	const deleteConversationMutation = useMutation({
		mutationFn: (conversationId: string) => nodeChatAdapter.deleteConversation(conversationId),
		onSuccess: async (_result, conversationId) => {
			// Drop the deleted thread from caches and, when it was the open one, clear the selection so the
			// list falls back to the newest remaining conversation.
			queryClient.removeQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId) });
			if (requestedConversationId === conversationId) {
				setRequestedConversationId("");
			}
			forgetConversationTitle(conversationId);
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
		},
	});

	const deleteConversation = useCallback(
		async (conversationId: string, skipConfirm: boolean): Promise<void> => {
			// Shift-click skips the confirm and deletes immediately, mirroring the platform client.
			if (!skipConfirm) {
				const confirmed = await confirm({
					title: t("pages.chat.conversationList.delete", "Delete"),
					description: t("pages.chat.conversationList.deleteConfirm", "Delete this conversation? This cannot be undone."),
					confirmationText: t("pages.chat.conversationList.delete", "Delete"),
					cancellationText: t("common.cancel", "Cancel"),
				});
				if (!confirmed) {
					return;
				}
			}

			markConversationDeleted(conversationId);

			setStreamError(undefined);
			setMutatingConversationId(conversationId);
			try {
				await deleteConversationMutation.mutateAsync(conversationId);
			} catch (error) {
				// The delete failed, so the conversation still exists and stays visible/selectable. Roll back the
				// pre-emptive "deleted" marker set above — otherwise the streaming loop's has(id) guard
				// treats the surviving thread as removed and silently refuses to stream to it: a permanently
				// un-chattable zombie until reload.
				unmarkConversationDeleted(conversationId);
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(
						t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."),
					);
					await refreshConversation(conversationId);
					return;
				}
				setStreamError(errorMessage(error));
			} finally {
				setMutatingConversationId(undefined);
			}
		},
		[confirm, deleteConversationMutation, markConversationDeleted, refreshConversation, t, unmarkConversationDeleted],
	);

	const handleDeleteConversation = useCallback(
		(conversationId: string, skipConfirm: boolean): void => {
			deleteConversation(conversationId, skipConfirm).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[deleteConversation],
	);

	const handleSelectRevision = useCallback(
		(variantGroupId: string, messageId: string): void => {
			const nextSelection = selectRevision(variantGroupId, messageId);
			// Persist the navigated selection so a reload restores it even without sending a message. Fire-and-forget,
			// consistent with other adapter calls; a failure surfaces in the error banner but never blocks the UI.
			if (selectedConversationId) {
				nodeChatAdapter
					.persistSelectedPath(selectedConversationId, nextSelection)
					.catch((error: unknown) => setStreamError(errorMessage(error)));
			}
		},
		[selectRevision, selectedConversationId],
	);

	const branchConversation = useCallback(
		async (messageId: string): Promise<void> => {
			setStreamError(undefined);
			try {
				// Send the visible active-revision selection so the branched thread copies the revisions the user was
				// viewing upstream, not always the newest sibling. Empty ⇒ server keeps its newest-per-group default.
				const selectedRevisions = Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined;
				const result = await nodeChatAdapter.branchConversation(selectedConversationId, messageId, selectedRevisions);
				// Surface the branched Origin=Local conversation: refresh history and open it.
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
				setRequestedConversationId(result.branchedConversationId ?? "");
			} catch (error) {
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(
						t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."),
					);
					return;
				}
				setStreamError(errorMessage(error));
			}
		},
		[activeRevisionByGroup, queryClient, selectedConversationId, setRequestedConversationId, t],
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
				// Feedback is read from the conversation's messages now (not a per-message GET); refetch the
				// conversation so the just-saved rating/comment re-renders.
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(selectedConversationId) });
			} catch (error) {
				if (isNodeChatReadOnlyConflict(error)) {
					setStreamError(
						t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."),
					);
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

	// Only an actual error / remote-view-only condition surfaces a notice; the always-on informational banner
	// was dropped. When undefined, ChatDisplayShell renders no alert.
	const notice = streamError ? (
		<Stack gap={2}>
			<Text fw={700}>{t("pages.chat.streamFailedTitle", "Local chat stream failed.")}</Text>
			<Text size="sm">{streamError}</Text>
		</Stack>
	) : conversationsIsError ? (
		<Stack gap={2}>
			<Text fw={700}>{t("pages.chat.historyLoadFailedTitle", "Unable to load local chat history.")}</Text>
			<Text size="sm">{errorMessage(conversationsError)}</Text>
		</Stack>
	) : isRemoteConversation ? (
		<Stack gap={2}>
			<Text fw={700}>{t("pages.chat.remoteViewOnlyTitle", "Remote conversation")}</Text>
			<Text size="sm">
				{t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node.")}
			</Text>
		</Stack>
	) : showNoModelGuidance ? (
		// Advisory, not blocking — a Codex/Azure sign-in later still routes around this via the picker's
		// cloud sections, and a send attempted anyway still falls through to ChatMessage's ModelNotInstalled Alert.
		<Stack gap={2}>
			<Text fw={700}>{t("pages.chat.noModelGuidance.title", "No chat model installed yet")}</Text>
			<Text size="sm">{t("pages.chat.noModelGuidance.body", "Install a GGUF model to start chatting locally.")}</Text>
			<Anchor component={Link} to="/models" size="sm" data-testid="chat-no-model-guidance-models-link">
				{t("pages.chat.noModelGuidance.goToModels", "Go to Models")}
			</Anchor>
		</Stack>
	) : undefined;

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
				<InlineErrorAlert variant="light" mb="md" message={errorMessage(createConversationMutation.error)} />
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
				}}
				maxMessageSizeKb={maxMessageSizeKb}
				streamingMessage={streamingMessage}
				timelineEntries={timelineEntries}
				disabledNotice={notice}
				isLoadingMessages={isLoadingSelectedConversation}
				messagesLoadFailed={selectedConversationLoadFailed}
				messagesLoadErrorText={selectedConversationLoadFailed ? errorMessage(selectedConversationError) : undefined}
				onRetryLoadMessages={handleRetryLoadMessages}
				inputStatus={{
					isSending,
					chatInputDisabled: isCreatingConversation || isRemoteConversation || scope?.composerDisabled === true,
					// A scoped session pins the agent, and the agent pins the model — both selectors read-only.
					modelSelectorDisabled: isRemoteConversation || isScoped,
					agentSelectorDisabled: isScoped,
					sendDisabled: selectedConversationIsLoading || isRemoteConversation || scope?.composerDisabled === true,
				}}
				conversationSearchQuery={conversationSearchQuery}
				showArchivedConversations={showArchivedConversations}
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
					handleSend(content, effort, model).catch((error: unknown) => setStreamError(errorMessage(error)));
					return undefined;
				}}
				onCancel={() => {
					if (scope?.onStopOverride) {
						scope.onStopOverride();
						return;
					}
					handleCancel().catch((error: unknown) => setStreamError(errorMessage(error)));
				}}
				onRegenerate={isScoped || isRemoteConversation ? undefined : handleRegenerate}
				onConversationSearchChange={setConversationSearchQuery}
				onToggleShowArchivedConversations={setShowArchivedConversations}
				onRenameConversation={isScoped ? undefined : handleRenameConversation}
				onToggleConversationPinned={isScoped ? undefined : handleToggleConversationPinned}
				onToggleConversationArchived={isScoped ? undefined : handleToggleConversationArchived}
				boundAgentMemoryEnabled={boundAgentMemoryEnabled}
				onToggleConversationMemoryExcluded={handleToggleConversationMemoryExcluded}
				onDeleteConversation={isScoped ? undefined : handleDeleteConversation}
				onBranchFromMessage={isScoped || isRemoteConversation ? undefined : handleBranch}
				activeRevisionByGroup={activeRevisionByGroup}
				onSelectRevision={handleSelectRevision}
				feedbackByMessageId={feedbackByMessageId}
				pendingFeedbackMessageId={pendingFeedbackMessageId}
				onSubmitFeedback={isScoped || isRemoteConversation ? undefined : handleSubmitFeedback}
			/>
		</ChatFrame>
	);
}

// The chat page normally claims the Layout scroll container's full height; an embedded scope's parent already owns
// that frame (and its own padding), so `Chat` renders bare inside it.
function ChatFrame({ embedded, children }: { embedded: boolean; children: ReactNode }) {
	if (embedded) {
		return <>{children}</>;
	}
	return <FullHeightPage data-tour="chat-overview">{children}</FullHeightPage>;
}
