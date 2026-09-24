import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { isNodeChatReadOnlyConflict, isNodeChatWorkflowRunLiveConflict } from "@/features/chat/api/NodeChatConflict";
import {
	clientWatchdogFailureCategory,
	StreamWatchdogError,
	streamWatchdogNotice,
} from "@/features/chat/api/NodeChatStreamGuard";
import {
	accumulateToolTimelineEntry,
	appendOptimisticNodeChatSend,
	applyNodeChatStreamEvent,
	markNodeChatStreamTerminated,
} from "@/features/chat/api/NodeChatStreamState";
import { useStreamCommitScheduler } from "@/features/chat/hooks/useStreamCommitScheduler";
import {
	inFlightAssistantMessageId,
	stampVariantGroup,
	titleFromContent,
} from "@/features/chat/models/ChatConversationDerivations";
import { errorMessage } from "@/features/chat/models/ChatErrorMessage";
import type {
	AgentOption,
	ChatConversationModel,
	ChatStreamingState,
	ChatTimelineEntry,
	ModelOption,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import type { ActiveChatStream, PendingStreamCommit } from "@/features/chat/models/ChatStreamState";
import { toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";

function createId(): string {
	return crypto.randomUUID();
}

export interface ChatStreamControllerInput {
	/** The page's single error slot. Every loop writes the banner through it; the page also writes it from CRUD. */
	setStreamError: (message: string | undefined) => void;
	/** Detail + list cache write. Shared with the page's conversation mutations, so the page owns it. */
	cacheConversation: (conversation: ChatConversationModel) => void;
	/** Detail-only cache write, used for per-frame streaming commits (see the page's own comment on it). */
	cacheConversationDetail: (conversation: ChatConversationModel) => void;
	/** Post-turn reconciliation. Shared with the page's conversation mutations, so the page owns it. */
	refreshConversation: (conversationId: string) => Promise<void>;
	/** Create-or-load the conversation a send targets. Shared with the attachment upload path, so the page owns it. */
	resolveSendConversation: (content: string) => Promise<ChatConversationModel>;
	selectedConversationId: string;
	displayConversations: ChatConversationModel[];
	modelOptions: ModelOption[];
	cloudModelOptions: ModelOption[];
	agentModeEnabled: boolean;
	selectedAgentId: string;
	agentOptions: AgentOption[];
	activeRevisionByGroup: Record<string, string>;
	attachmentFileIds: string[];
	toolsEnabled: boolean;
	knowledgeBaseEnabled: boolean;
	reasoningEffort: ReasoningEffort;
	/**
	 * The chat-stream voice tap (barge-in + answer progress). Instantiated by the page, not here: it holds per-turn
	 * buffers in refs so it must exist exactly once, and `src/features/voice` is another feature — importing it from
	 * a new chat module would add a cross-feature edge dependency-cruiser does not carry.
	 */
	onVoiceTurnStart: () => void;
	onVoiceAnswerProgress: (streaming: ChatStreamingState | undefined) => void;
	/** The conversation whose FULL payload has loaded; "" while none has. Arms the cold-load re-attach. */
	loadedSelectedConversationId: string;
	/** Bumped by a scope's owner when a NEW server-side turn starts on the same conversation; re-arms the re-attach. */
	resumeNonce?: number;
}

export interface ChatStreamController {
	streamingMessage: ChatStreamingState | undefined;
	timelineEntries: ChatTimelineEntry[];
	handleSend: (content: string, effort: ReasoningEffort, model: string) => Promise<void>;
	handleRegenerate: (assistantMessageId: string) => void;
	handleCancel: () => Promise<void>;
	/**
	 * Flag a conversation as deleted and abort its in-flight turn, if it owns the active stream. Both halves belong
	 * to the delete path but read the controller's private refs, so they are exposed as one call instead of the
	 * raw Set + AbortController.
	 */
	markConversationDeleted: (conversationId: string) => void;
	/** Roll the flag above back when the delete failed and the conversation survived (otherwise: an unchattable zombie). */
	unmarkConversationDeleted: (conversationId: string) => void;
	/** Suppress the first-send auto-title for a conversation (an explicit rename already named it). */
	markConversationTitled: (conversationId: string) => void;
	/** Drop a deleted conversation from the auto-title memo. */
	forgetConversationTitle: (conversationId: string) => void;
}

/**
 * The race-coupled send / regenerate / resume / cancel controller, and the mutable cluster those four share.
 *
 * `activeStream` is the mutual-exclusion token: all three streaming loops park an AbortController in it, and
 * `handleCancel` aborts whatever is there without knowing which loop is running. That invariant only holds while
 * every loop keeps a consistent shape in that ONE ref, so the loops, the ref, the deleted-conversation guard set
 * and the per-frame commit scheduler live together here rather than spread across the page.
 *
 * `streamingMessage` / `timelineEntries` are owned here too: `commitStreamState` writes both setters and is threaded
 * into `useStreamCommitScheduler`, which every loop closes over — the state and its writers cannot be separated.
 */
export function useChatStreamController({
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
	resumeNonce,
}: ChatStreamControllerInput): ChatStreamController {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	// Developer mode + per-send sampling overrides. Read directly from global stores; nothing outside the loops
	// below consumes either, so the subscriptions live here rather than on the page.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const samplingOptions = useChatSamplingPreferencesStore((state) => state.options);

	const activeStream = useRef<ActiveChatStream | null>(null);
	// Conversations deleted while a stream was in flight. The streaming loops consult this set so an aborted
	// turn cannot re-cache or refetch (404 / resurrect) a thread the operator just removed.
	// Lazy-init so the Set is built once instead of allocating a throwaway `new Set()` on every render;
	// the literal `useRef` (not a wrapper) keeps lint treating `.current` as a stable, non-reactive ref.
	const deletedConversationIds = useRef<Set<string>>(undefined as unknown as Set<string>);
	if (!deletedConversationIds.current) {
		deletedConversationIds.current = new Set<string>();
	}
	// Conversations whose first message has already promoted their title (avoids re-renaming on every send).
	// Lazy-init (see deletedConversationIds): build the Set once, not a throwaway per render.
	const titledConversations = useRef<Set<string>>(undefined as unknown as Set<string>);
	if (!titledConversations.current) {
		titledConversations.current = new Set<string>();
	}

	const [streamingMessage, setStreamingMessage] = useState<ChatStreamingState | undefined>();
	// Tool-call activity entries accumulated over the current streaming turn (keyed by tool call id). Reset per turn.
	const [timelineEntries, setTimelineEntries] = useState<ChatTimelineEntry[]>([]);

	// Per-frame stream commit: the streaming loops fold each SignalR event onto the previous state synchronously,
	// then hand the derived state here; the scheduler batches these setState/cache writes to one commit per
	// animation frame (terminal events flush immediately). Only the terminal frame refreshes the list cache.
	const commitStreamState = useCallback(
		(pending: PendingStreamCommit): void => {
			if (pending.writeConversationList) {
				cacheConversation(pending.conversation);
			} else {
				cacheConversationDetail(pending.conversation);
			}
			setStreamingMessage(pending.streamingMessage);
			if (pending.toolTimelineEntries.length > 0) {
				setTimelineEntries((current) => pending.toolTimelineEntries.reduce(accumulateToolTimelineEntry, current));
			}
			// Decoupled voice tap: mirror the reduced answer text into the TTS sentence buffer. Per-frame cadence is
			// fine for sentence detection; the loops fire a final isActive:false flush when the stream ends.
			onVoiceAnswerProgress(pending.streamingMessage);
		},
		[cacheConversation, cacheConversationDetail, onVoiceAnswerProgress],
	);

	// Fold two same-frame pending commits: the reducer already accumulated conversation + streamingMessage onto the
	// latest, so those take the newer value; tool timeline entries are rare but must all survive to the flush.
	const mergePendingStreamCommit = useCallback(
		(previous: PendingStreamCommit, next: PendingStreamCommit): PendingStreamCommit => ({
			conversation: next.conversation,
			writeConversationList: previous.writeConversationList || next.writeConversationList,
			streamingMessage: next.streamingMessage,
			toolTimelineEntries:
				previous.toolTimelineEntries.length > 0
					? [...previous.toolTimelineEntries, ...next.toolTimelineEntries]
					: next.toolTimelineEntries,
		}),
		[],
	);

	const streamScheduler = useStreamCommitScheduler(commitStreamState, mergePendingStreamCommit);

	const handleSend = useCallback(
		async (content: string, effort: ReasoningEffort, model: string): Promise<void> => {
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
			const hasPlaceholderTitle =
				conversation.origin !== "remote" &&
				(conversation.title.trim().length === 0 || conversation.title.trim() === "New conversation");
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
			// Only honor a concrete model id that is still an available option. A stale persisted selection — e.g. an
			// Ollama model id left in localStorage from before the node switched to the bundled llama.cpp runtime —
			// must NOT be sent: it would route to a model that isn't installed and fail with "model is not installed".
			// Falling back to undefined makes the node resolve its configured default (the provisioned GGUF). This
			// guards the fast first-send case before the reconciliation effect (which resets the store) has run.
			const requestedConcreteModel = toNodeChatRequestModel(model);
			const requestModel =
				requestedConcreteModel !== undefined &&
				(modelOptions.some((option) => option.value === requestedConcreteModel) ||
					cloudModelOptions.some((option) => option.value === requestedConcreteModel))
					? requestedConcreteModel
					: undefined;
			const startedAt = new Date().toISOString();
			// Compute effective agentDefinitionId: only stamp when mode is on, an agent is selected, AND the
			// selected agent still exists in the live list (stale/deleted ids fall back to Default Assistant).
			const effectiveAgentId =
				agentModeEnabled && selectedAgentId && agentOptions.some((a) => a.id === selectedAgentId) ? selectedAgentId : undefined;
			const effectiveAgentName = effectiveAgentId
				? (agentOptions.find((a) => a.id === effectiveAgentId)?.name ?? undefined)
				: undefined;
			const optimisticConversation = appendOptimisticNodeChatSend(
				conversation,
				ids,
				content,
				startedAt,
				requestModel,
				effectiveAgentName,
				effort,
			);
			const abortController = new AbortController();

			cacheConversation(optimisticConversation);
			setTimelineEntries([]);
			// Barge-in: a fresh send halts any voice playback still running from a previous turn.
			onVoiceTurnStart();
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

			let lastStreaming: ChatStreamingState | undefined;
			// Running reduced conversation for THIS turn: each event folds onto the previous one here so batched
			// per-frame commits don't have to round-trip the query cache (which the scheduler hasn't flushed yet).
			let latestConversation: ChatConversationModel | undefined;
			try {
				for await (const streamEvent of nodeChatAdapter.sendMessage(
					{
						conversationId: conversation.id,
						content,
						userMessageId: ids.userMessageId,
						messageId: ids.assistantMessageId,
						requestId: ids.requestId,
						model: requestModel,
						useLocalTools: toolsEnabled,
						// Opt-in knowledge-base grounding for plain chat. The server ignores it in agent mode.
						useKnowledgeBase: knowledgeBaseEnabled,
						reasoningEffort: effort,
						// Send the active conversation-tree path so the server assembles context from the selected
						// branch only. Omit when nothing was navigated this turn so the server keeps the stored map.
						selectedPath: Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined,
						agentDefinitionId: effectiveAgentId,
						// Re-send the conversation's CURRENT (non-deleted) attachment ids on every turn so the server can
						// ground plain chat (inline extracted text, capped) and stage the files into AgentHome for agent mode.
						attachmentFileIds: attachmentFileIds.length > 0 ? attachmentFileIds : undefined,
						// Include sampling overrides only when developer mode is on and at least one field is set.
						// toWireSamplingOptions returns undefined when all fields are null → omitted from wire payload
						// (byte-identical invariant: the OFF path is byte-identical to the default non-dev path).
						samplingOptions: developerMode ? toWireSamplingOptions(samplingOptions) : undefined,
					},
					abortController.signal,
				)) {
					// The conversation was deleted mid-stream: drop any batched commit and stop touching its cache so
					// the aborted turn can neither re-create the removed cache entry nor be refetched in the finally.
					if (deletedConversationIds.current.has(conversation.id)) {
						streamScheduler.cancel();
						break;
					}
					const currentConversation =
						latestConversation ??
						queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ??
						optimisticConversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent, lastStreaming);
					latestConversation = applied.conversation;
					lastStreaming = applied.streamingMessage;
					streamScheduler.schedule({
						conversation: applied.conversation,
						writeConversationList: applied.isTerminal,
						streamingMessage: applied.streamingMessage,
						toolTimelineEntries: applied.timelineEntry ? [applied.timelineEntry] : [],
					});
					// Commit terminal state now instead of waiting for the next frame so completion/failure lands
					// promptly and the list-cache refresh above isn't left pending.
					if (applied.isTerminal) {
						streamScheduler.flush();
					}
				}
				// Flush any trailing batched delta (a stream that ended without an explicit terminal event).
				streamScheduler.flush();
				// Fire the final voice flush once the stream completes (the terminal flush is idempotent).
				if (lastStreaming) {
					onVoiceAnswerProgress({ ...lastStreaming, isActive: false });
				}
			} catch (error) {
				// The turn errored: drop any batched delta and write the failed state synchronously below.
				streamScheduler.cancel();
				if (!abortController.signal.aborted && !deletedConversationIds.current.has(conversation.id)) {
					// A send against a remote-origin conversation is rejected by the server-side mutation guard; over
					// SignalR that arrives as a HubException leading with the ReadOnlyConversation token, not a 409.
					// The client watchdog is the ONE failure the browser itself raises, so it must say so: its raw
					// message ("Local chat stream timed out (inter-chunk-stall).") reads like a node timeout and is what
					// made a premature client-side give-up indistinguishable from the node's own ceiling. It gets a
					// translated sentence plus its own reason code; every other error keeps the backend's text, which
					// now names which node-side bound fired.
					const watchdogNotice = error instanceof StreamWatchdogError ? streamWatchdogNotice(error.category) : undefined;
					let message: string;
					if (isNodeChatReadOnlyConflict(error)) {
						message = t(
							"pages.chat.remoteViewOnly",
							"This conversation was started from a paired client and is view-only on this node.",
						);
					} else if (isNodeChatWorkflowRunLiveConflict(error)) {
						message = t(
							"pages.chat.workflowRunLive",
							"This conversation is running a workflow; answer through it or stop it first.",
						);
					} else if (watchdogNotice) {
						message = t(watchdogNotice.key, watchdogNotice.fallback);
					} else {
						message = errorMessage(error);
					}
					const failureCategory = watchdogNotice ? clientWatchdogFailureCategory : undefined;
					// Prefer the running reduced state (which includes any delta whose frame we just cancelled) over the
					// query cache so a failed turn keeps every received partial token, not the last flushed frame.
					const currentConversation =
						latestConversation ??
						queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ??
						optimisticConversation;
					const failed = markNodeChatStreamTerminated(
						currentConversation,
						ids.assistantMessageId,
						"failed",
						message,
						failureCategory,
					);
					cacheConversation(failed.conversation);
					setStreamingMessage(failed.streamingMessage);
					setStreamError(message);
				}
			} finally {
				activeStream.current = null;
				setStreamingMessage((current) =>
					current?.messageId === ids.assistantMessageId ? { ...current, isActive: false } : current,
				);
				if (!deletedConversationIds.current.has(conversation.id)) {
					await refreshConversation(conversation.id);
				}
			}
		},
		[
			activeRevisionByGroup,
			agentModeEnabled,
			agentOptions,
			attachmentFileIds,
			cacheConversation,
			cloudModelOptions,
			developerMode,
			modelOptions,
			queryClient,
			refreshConversation,
			resolveSendConversation,
			samplingOptions,
			selectedAgentId,
			setStreamError,
			streamScheduler,
			t,
			toolsEnabled,
			knowledgeBaseEnabled,
			onVoiceTurnStart,
			onVoiceAnswerProgress,
		],
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
			// Regenerate via the shared runner over the hub: the server mints a sibling variant and
			// drives + streams the run exactly like a send. The variant messageId + requestId arrive on the
			// events, so there is no client-known id up front; applyNodeChatStreamEvent appends the new variant.
			// The group id used to collapse the streaming variant onto the original in place (the server's real
			// id arrives on the post-stream refetch): the original's own group when it already has siblings,
			// otherwise a synthetic group keyed on the original message id.
			const originalMessage = conversation.messages.find((message) => message.id === assistantMessageId);
			const variantGroupId = originalMessage?.variantGroupId ?? assistantMessageId;
			const abortController = new AbortController();
			activeStream.current = { conversationId: conversation.id, messageId: "", requestId: "", abortController };
			setTimelineEntries([]);
			// Barge-in: regenerate halts any voice playback still running.
			onVoiceTurnStart();
			setStreamingMessage({ conversationId: conversation.id, messageId: "", content: "", isActive: true });

			let lastStreaming: ChatStreamingState | undefined;
			// Running reduced conversation for THIS regenerate turn (see handleSend): each event folds onto the
			// previous grouped conversation so batched frames don't read a not-yet-flushed query cache.
			let latestConversation: ChatConversationModel | undefined;
			try {
				for await (const streamEvent of nodeChatAdapter.regenerateMessage(
					conversation.id,
					assistantMessageId,
					reasoningEffort,
					toolsEnabled,
					// Honor the same opt-in knowledge-base grounding the send path uses, so a regenerated turn keeps
					// (or drops) KB grounding + its sources strip consistently with the original send (30c).
					knowledgeBaseEnabled,
					// Send the active conversation-tree path so the regenerated turn's context follows the selected
					// branch only. Omit when nothing was navigated so the server keeps the stored map.
					Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined,
					// Same developer-mode gate the send path applies: overrides ride only when developer mode is on and
					// at least one field is set, so a plain regenerate stays byte-identical to today.
					developerMode ? toWireSamplingOptions(samplingOptions) : undefined,
					abortController.signal,
				)) {
					// The conversation was deleted mid-stream: drop any batched commit and stop touching its cache so
					// the aborted turn can neither re-create the removed cache entry nor be refetched in the finally.
					if (deletedConversationIds.current.has(conversation.id)) {
						streamScheduler.cancel();
						break;
					}
					if (activeStream.current) {
						activeStream.current = {
							...activeStream.current,
							messageId: streamEvent.messageId,
							requestId: streamEvent.requestId,
						};
					}
					const currentConversation =
						latestConversation ??
						queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ??
						conversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent, lastStreaming);
					// Collapse the streaming variant onto the original in place (applyNodeChatStreamEvent rebuilds the
					// variant row per event without a group id, so re-stamp every iteration). Only once the server id
					// is latched and differs from the original — i.e. a genuine sibling, not an in-place re-render.
					const grouped =
						streamEvent.messageId && streamEvent.messageId !== assistantMessageId
							? stampVariantGroup(applied.conversation, assistantMessageId, streamEvent.messageId, variantGroupId)
							: applied.conversation;
					latestConversation = grouped;
					lastStreaming = applied.streamingMessage;
					streamScheduler.schedule({
						conversation: grouped,
						writeConversationList: applied.isTerminal,
						streamingMessage: applied.streamingMessage,
						toolTimelineEntries: applied.timelineEntry ? [applied.timelineEntry] : [],
					});
					if (applied.isTerminal) {
						streamScheduler.flush();
					}
				}
				// Flush any trailing batched delta the loop left pending.
				streamScheduler.flush();
				// Fire the final voice flush once the regenerated stream completes (terminal flush is idempotent).
				if (lastStreaming) {
					onVoiceAnswerProgress({ ...lastStreaming, isActive: false });
				}
				// The stream events don't carry variant_group_id; the post-stream refetch loads it from persistence
				// and groupMessageRevisions surfaces the newest sibling by default, so no explicit selection here.
			} catch (error) {
				// The turn errored: drop any batched delta before surfacing the error.
				streamScheduler.cancel();
				if (!abortController.signal.aborted && !deletedConversationIds.current.has(conversation.id)) {
					if (isNodeChatReadOnlyConflict(error)) {
						setStreamError(
							t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node."),
						);
					} else {
						setStreamError(errorMessage(error));
					}
				}
			} finally {
				const finishedMessageId = activeStream.current?.messageId;
				activeStream.current = null;
				setStreamingMessage((current) => (current?.messageId === finishedMessageId ? { ...current, isActive: false } : current));
				if (!deletedConversationIds.current.has(conversation.id)) {
					await refreshConversation(conversation.id);
				}
			}
		},
		[
			activeRevisionByGroup,
			developerMode,
			displayConversations,
			knowledgeBaseEnabled,
			queryClient,
			reasoningEffort,
			refreshConversation,
			samplingOptions,
			selectedConversationId,
			setStreamError,
			streamScheduler,
			t,
			toolsEnabled,
			onVoiceTurnStart,
			onVoiceAnswerProgress,
		],
	);

	const handleRegenerate = useCallback(
		(assistantMessageId: string): void => {
			regenerate(assistantMessageId).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[regenerate, setStreamError],
	);

	// Cold-load re-attach to a turn that is still running. The adapter's `onReconnected` path covers a SignalR drop
	// inside a LIVING page, where the invocation id is still in memory; a page that has RELOADED holds nothing, so
	// nothing re-attaches — and an in-flight `ask_user` question (transient live state, deliberately never written
	// into the conversation's persisted parts) is then lost for good while the run stays parked until it times out.
	// Opening a conversation therefore asks the server whether it still has a live turn for it; an idle conversation
	// answers with an empty stream and this whole loop is a no-op.
	const resumeActiveTurn = useCallback(
		async (conversationId: string, abortController: AbortController): Promise<void> => {
			// A send/regenerate — or an earlier resume — already owns the turn; re-attaching would double-subscribe.
			if (activeStream.current) {
				return;
			}

			// Latched on the FIRST event, never before: until one arrives the conversation is most likely idle, and
			// claiming stream ownership there would spin the composer into the in-flight state (and block sends) for a
			// thread with nothing running.
			let attachedMessageId: string | undefined;
			let lastStreaming: ChatStreamingState | undefined;
			// Running reduced conversation for THIS re-attach (see handleSend): each event folds onto the previous one
			// so batched frames never read a query cache the scheduler hasn't flushed yet.
			let latestConversation: ChatConversationModel | undefined;
			try {
				for await (const streamEvent of nodeChatAdapter.resumeConversation(conversationId, abortController.signal)) {
					// The conversation was deleted mid-stream: drop any batched commit and stop touching its cache.
					if (deletedConversationIds.current.has(conversationId)) {
						streamScheduler.cancel();
						break;
					}
					const currentConversation =
						latestConversation ?? queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversationId));
					if (!currentConversation) {
						break;
					}
					if (!attachedMessageId) {
						// A send/regenerate that started while this stream was opening now owns the turn — leave it alone.
						if (activeStream.current) {
							break;
						}
						attachedMessageId = inFlightAssistantMessageId(currentConversation) ?? streamEvent.messageId;
						setTimelineEntries([]);
						activeStream.current = {
							conversationId,
							messageId: attachedMessageId,
							// The resume's request id IS the invocation id, which is what Stop cancels.
							requestId: streamEvent.requestId,
							abortController,
						};
					}
					// Remap onto the persisted in-flight row (see inFlightAssistantMessageId).
					const applied = applyNodeChatStreamEvent(
						currentConversation,
						{ ...streamEvent, messageId: attachedMessageId },
						lastStreaming,
					);
					latestConversation = applied.conversation;
					lastStreaming = applied.streamingMessage;
					streamScheduler.schedule({
						conversation: applied.conversation,
						writeConversationList: applied.isTerminal,
						streamingMessage: applied.streamingMessage,
						toolTimelineEntries: applied.timelineEntry ? [applied.timelineEntry] : [],
					});
					if (applied.isTerminal) {
						streamScheduler.flush();
					}
				}
				// Flush any trailing batched delta. A no-op for the idle case, which schedules nothing at all.
				streamScheduler.flush();
				if (lastStreaming) {
					onVoiceAnswerProgress({ ...lastStreaming, isActive: false });
				}
			} catch (error) {
				streamScheduler.cancel();
				// Nothing attached ⇒ nothing was resumed, so there is no turn to fail: opening an idle conversation must
				// never raise a banner. A failure AFTER attaching interrupted a turn the operator can see, so surface it.
				if (attachedMessageId && !abortController.signal.aborted && !deletedConversationIds.current.has(conversationId)) {
					setStreamError(errorMessage(error));
				}
			} finally {
				// Only the attached path owns state to release; the idle path must leave everything untouched.
				if (attachedMessageId) {
					// Re-bound as a const so the updater below keeps the narrowing (a `let` loses it inside a closure).
					const resumedMessageId = attachedMessageId;
					activeStream.current = null;
					setStreamingMessage((current) => (current?.messageId === resumedMessageId ? { ...current, isActive: false } : current));
					if (!deletedConversationIds.current.has(conversationId)) {
						await refreshConversation(conversationId);
					}
				}
			}
		},
		[onVoiceAnswerProgress, queryClient, refreshConversation, setStreamError, streamScheduler],
	);

	// Read through a ref so the effect below can key on the conversation alone (mirrors useStreamCommitScheduler's
	// commitRef): re-running the effect on an unrelated dependency change would abort a live re-attach in its
	// cleanup, losing the very question card this exists to restore.
	const resumeActiveTurnRef = useRef(resumeActiveTurn);
	useLayoutEffect(() => {
		resumeActiveTurnRef.current = resumeActiveTurn;
	}, [resumeActiveTurn]);

	// biome-ignore lint/correctness/useExhaustiveDependencies: resumeNonce is a re-arm signal, not an input the body reads.
	useEffect(() => {
		if (!loadedSelectedConversationId) {
			return;
		}

		// Keyed on the conversation id alone, so one open attaches at most once: neither a re-render nor a background
		// refetch of the same thread can re-fire it. Switching away aborts; returning later attaches again.
		const abortController = new AbortController();
		// The loop surfaces the only failure worth showing (see its catch); a rejection escaping here is the
		// post-turn refresh, which must not raise a banner on conversation open.
		resumeActiveTurnRef.current(loadedSelectedConversationId, abortController).catch(() => undefined);
		return () => abortController.abort();
		// resumeNonce re-arms the re-attach when the OWNER starts a new server-side turn on the same conversation
		// (a work-session step), which the conversation id alone cannot observe.
	}, [loadedSelectedConversationId, resumeNonce]);

	const handleCancel = useCallback(async (): Promise<void> => {
		const active = activeStream.current;
		if (!active) {
			return;
		}

		// Abort and dispose the local stream FIRST so the UI stops immediately and the SignalR subscription is torn down
		// before we round-trip to the server. The awaited server cancel must never gate the local stop: if it is slow or
		// fails, the user's stop has still taken effect.
		active.abortController.abort();
		// Barge-in: cancelling generation halts voice playback immediately (acceptance: stop = halt playback).
		onVoiceTurnStart();
		const currentConversation = queryClient.getQueryData<ChatConversationModel>(
			nodeChatQueryKeys.conversation(active.conversationId),
		);
		if (currentConversation) {
			const cancelled = markNodeChatStreamTerminated(currentConversation, active.messageId, "cancelled");
			cacheConversation(cancelled.conversation);
			setStreamingMessage(cancelled.streamingMessage);
		}

		try {
			// Best-effort server cancel: the local stream is already stopped, so a failure here only affects server-side
			// reconciliation, surfaced as a non-blocking error.
			await nodeChatAdapter.cancelMessage({
				conversationId: active.conversationId,
				messageId: active.messageId,
				requestId: active.requestId,
			});
		} catch (error) {
			setStreamError(errorMessage(error));
		} finally {
			// Reconcile from the server's authoritative terminal state.
			await refreshConversation(active.conversationId);
		}
	}, [cacheConversation, queryClient, refreshConversation, setStreamError, onVoiceTurnStart]);

	const markConversationDeleted = useCallback((conversationId: string): void => {
		// Abort an in-flight stream for this thread before deleting, and flag it so the streaming loop stops
		// re-caching/refetching the just-removed conversation (otherwise the abort resurrects it as a 404).
		deletedConversationIds.current.add(conversationId);
		if (activeStream.current?.conversationId === conversationId) {
			activeStream.current.abortController.abort();
		}
	}, []);

	const unmarkConversationDeleted = useCallback((conversationId: string): void => {
		deletedConversationIds.current.delete(conversationId);
	}, []);

	const markConversationTitled = useCallback((conversationId: string): void => {
		titledConversations.current.add(conversationId);
	}, []);

	const forgetConversationTitle = useCallback((conversationId: string): void => {
		titledConversations.current.delete(conversationId);
	}, []);

	return {
		streamingMessage,
		timelineEntries,
		handleSend,
		handleRegenerate,
		handleCancel,
		markConversationDeleted,
		unmarkConversationDeleted,
		markConversationTitled,
		forgetConversationTitle,
	};
}
