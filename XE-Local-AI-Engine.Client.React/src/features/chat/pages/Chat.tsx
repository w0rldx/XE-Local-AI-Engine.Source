import { Alert, Box, Button, Center, Loader, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { getLocalModelDetailsOptions, listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useAgentDefinitions } from "@/features/agents/queries/useAgentDefinitions";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { isNodeChatReadOnlyConflict } from "@/features/chat/api/NodeChatConflict";
import { StreamWatchdogError } from "@/features/chat/api/NodeChatStreamGuard";
import {
	accumulateToolTimelineEntry,
	appendOptimisticNodeChatSend,
	applyNodeChatStreamEvent,
	markNodeChatStreamTerminated,
} from "@/features/chat/api/NodeChatStreamState";
import { useNodeChatConnectionReadiness } from "@/features/chat/api/useNodeChatConnectionReadiness";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { buildChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type {
	AgentOption,
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatStreamingState,
	ChatTimelineEntry,
	ModelOption,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { DEFAULT_ASSISTANT_NAME } from "@/features/chat/models/ChatModels";
import { toWireSamplingOptions } from "@/features/chat/models/ChatSamplingOptions";
import { deriveUsedContextTokens } from "@/features/chat/models/ContextUsageDerivation";
import { localDefaultModelValue, toNodeChatRequestModel } from "@/features/chat/models/NodeChatModelSelection";
import { toChatModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useCodexModelOptions } from "@/features/chat/queries/useCodexModelOptions";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";
import {
	binaryReasoningEfforts,
	codexReasoningEfforts,
	reasoningEfforts,
	useNodeChatPreferencesStore,
} from "@/features/chat/stores/NodeChatPreferencesStore";

/* eslint-disable react-doctor/no-giant-component, react-doctor/prefer-useReducer, react-doctor/js-combine-iterations */

const localDefaultModelOption: ModelOption = {
	value: localDefaultModelValue,
	label: "Local default",
	displayName: "Local runtime default",
	// The runtime resolves the concrete model later, so its capabilities are unknown here. Treat as not
	// reasoning/tool capable (safe default): offer neither the reasoning menu nor the tool controls until
	// a concrete, capability-known model is selected.
	isReasoningModel: false,
	isToolCapable: false,
	isAvailable: true,
	statusLabel: "Runtime-selected model",
};

const chatUiCapabilities = buildChatUiCapabilities(nodeCapabilities.chat);
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

// During a regenerate the server-minted variant streams in WITHOUT a variant_group_id (the stream events
// don't carry it — it only arrives on the post-stream refetch). Left ungrouped, the new turn renders as a
// second assistant message stacked below the original until the refetch collapses them. Stamping both the
// original and the streaming variant into a shared group up front collapses them immediately, so the new turn
// streams in place as the active revision with the prev/next selector. The id is the original's existing group
// when it already has siblings, otherwise a synthetic group keyed on the original message id; either way the
// refetch later replaces it with the authoritative server group id.
function stampVariantGroup(
	conversation: ChatConversationModel,
	originalMessageId: string,
	variantMessageId: string,
	variantGroupId: string,
): ChatConversationModel {
	return {
		...conversation,
		messages: conversation.messages.map((message) => {
			if (message.id === variantMessageId) {
				return { ...message, variantGroupId };
			}
			// Only seed the original when it isn't already part of a group — never clobber a real group id.
			if (message.id === originalMessageId && !message.variantGroupId) {
				return { ...message, variantGroupId };
			}
			return message;
		}),
	};
}

export function Chat() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const queryClient = useQueryClient();
	const activeStream = useRef<ActiveChatStream | null>(null);
	// Conversations deleted while a stream was in flight. The streaming loops consult this set so an aborted
	// turn cannot re-cache or refetch (404 / resurrect) a thread the operator just removed.
	const deletedConversationIds = useRef<Set<string>>(new Set());
	// Composer selections, the last-selected conversation, and the sidebar collapsed state all persist across
	// reloads via localStorage (NodeChatPreferencesStore), mirroring the platform ToolCallingStore. Persisted
	// values are validated below: the model against the live model list / effort set, and the last-selected
	// conversation against the loaded list (a stale id falls back to the first conversation).
	const selectedModel = useNodeChatPreferencesStore((state) => state.selectedModel);
	const reasoningEffort = useNodeChatPreferencesStore((state) => state.reasoningEffort);
	const toolsEnabled = useNodeChatPreferencesStore((state) => state.toolsEnabled);
	const requestedConversationId = useNodeChatPreferencesStore((state) => state.selectedConversationId);
	const collapsed = useNodeChatPreferencesStore((state) => state.sidebarCollapsed);
	const agentModeEnabled = useNodeChatPreferencesStore((state) => state.agentModeEnabled);
	const selectedAgentId = useNodeChatPreferencesStore((state) => state.selectedAgentId);
	const {
		setSelectedModel,
		setReasoningEffort,
		toggleTools,
		setSelectedConversationId: setRequestedConversationId,
		toggleSidebar,
		setAgentModeEnabled,
		setSelectedAgentId,
	} = useNodeChatPreferencesStore((state) => state.actions);
	// Developer mode + per-send sampling overrides (§7.9 of plan). Read directly from global stores.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const samplingOptions = useChatSamplingPreferencesStore((state) => state.options);
	const { readiness: connectionReadiness, error: connectionError, retry: retryConnection } = useNodeChatConnectionReadiness();
	const [streamingMessage, setStreamingMessage] = useState<ChatStreamingState | undefined>();
	// Tool-call activity entries accumulated over the current streaming turn (keyed by tool call id). Reset per turn.
	const [timelineEntries, setTimelineEntries] = useState<ChatTimelineEntry[]>([]);
	const [streamError, setStreamError] = useState<string | undefined>();
	const [conversationSearchQuery, setConversationSearchQuery] = useState("");
	const [showArchivedConversations, setShowArchivedConversations] = useState(false);
	const [mutatingConversationId, setMutatingConversationId] = useState<string | undefined>();
	// Operator's chosen revision per variant group (variantGroupId → active messageId). Unset groups default to newest.
	const [activeRevisionByGroup, setActiveRevisionByGroup] = useState<Record<string, string>>({});
	const [pendingFeedbackMessageId, setPendingFeedbackMessageId] = useState<string | undefined>();
	// Conversations whose first message has already promoted their title (avoids re-renaming on every send).
	const titledConversations = useRef<Set<string>>(new Set());
	// The conversation whose persisted selected-path has already seeded activeRevisionByGroup, so a background
	// refetch of the same conversation never clobbers an in-session selection the operator just navigated.
	const seededSelectionConversationId = useRef<string | undefined>(undefined);

	const conversationsQuery = useQuery({
		queryKey: nodeChatQueryKeys.conversationList(showArchivedConversations),
		queryFn: ({ signal }) => nodeChatAdapter.listConversations({ includeArchived: showArchivedConversations, signal }),
	});

	const localModelsQuery = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Keep the prior model list while a refetch is in flight so a transient response that momentarily omits
		// the selected model can't trip the reconcile effect and reset selectedModel to the default (which would
		// undercut the persisted selection from #4).
		placeholderData: keepPreviousData,
	});

	const modelOptions = useMemo<ModelOption[]>(() => {
		const response = localModelsQuery.data;
		if (!response) {
			return [localDefaultModelOption];
		}

		return [localDefaultModelOption, ...toChatModelOptions(response.items ?? [], response.isAvailable ?? false)];
	}, [localModelsQuery.data]);
	// Cloud (Codex) model options — empty array when signed out; non-empty only when Codex session active.
	const cloudModelOptions = useCodexModelOptions();
	const selectedModelOption = useMemo(
		() =>
			modelOptions.find((option) => option.value === selectedModel) ??
			cloudModelOptions.find((option) => option.value === selectedModel),
		[cloudModelOptions, modelOptions, selectedModel],
	);
	// Per-model capability gating (plan §6.6): only offer the reasoning-effort menu when the active model
	// advertises the Ollama `thinking` capability — otherwise collapse to ["none"] so the composer disables the
	// menu (it disables at length <= 1) and a non-reasoning model can never send a stale effort. Tool controls
	// gate on the model's `tools` capability (combined with the node-wide gate inside ChatInputArea).
	const activeModelReasoningCapable = selectedModelOption?.isReasoningModel ?? false;
	const activeModelToolCapable = selectedModelOption?.isToolCapable ?? false;
	// Pick the right reasoning-effort set based on the active model's provider:
	// - Cloud (Codex) models get the full OpenAI Responses vocabulary: none/minimal/low/medium/high/xhigh.
	//   "minimal" and "xhigh" are Codex-only and must NEVER be offered for Ollama models.
	// - Ollama models that advertise the `thinking` capability get the graded Ollama set: none/low/medium/high.
	// - Every other model (non-thinking Ollama, local default) gets binary On/Off: on/none.
	//   On omits think so a model that reasons by default runs its built-in reasoning; Off sends think:false.
	const selectedModelIsCloud = selectedModelOption?.isCloud === true;
	const availableReasoningEfforts = useMemo<ReasoningEffort[]>(() => {
		if (selectedModelIsCloud) {
			return [...codexReasoningEfforts];
		}
		if (activeModelReasoningCapable) {
			return [...reasoningEfforts];
		}
		return [...binaryReasoningEfforts];
	}, [activeModelReasoningCapable, selectedModelIsCloud]);

	const agentDefinitionsQuery = useAgentDefinitions();
	// Build the live agent option list (sorted, excluding the Default Assistant by shared constant).
	// Single derivation site — AgentSelectorCard receives this as a prop (no internal query call).
	// Chat.tsx uses this list to gate send: a stale/deleted selectedAgentId that no longer appears here is
	// silently dropped → the send falls back to Default Assistant (mode-off behavior).
	const agentOptions = useMemo<AgentOption[]>(() => {
		const definitions = agentDefinitionsQuery.data ?? [];
		return definitions
			.filter((agent) => agent.name.toLowerCase() !== DEFAULT_ASSISTANT_NAME.toLowerCase())
			.map((agent) => ({
				id: agent.id,
				name: agent.name,
				description: agent.description,
				kind: agent.kind,
				modelProfile: agent.modelProfile,
			}))
			.sort((a, b) => a.name.localeCompare(b.name));
	}, [agentDefinitionsQuery.data]);
	// agentControlsAvailable: capability gate AND at least one agent in the live list.
	const agentControlsAvailable = chatUiCapabilities.showAgentControls && agentOptions.length > 0;
	// Single merged agent control wiring: picking an agent enables agent mode and stamps it; picking the Default
	// Assistant row (empty id) disables agent mode and clears the selection. Replaces the old separate toggle.
	const handleSelectAgent = useCallback(
		(agentId: string) => {
			if (agentId) {
				setSelectedAgentId(agentId);
				setAgentModeEnabled(true);
			} else {
				setAgentModeEnabled(false);
				setSelectedAgentId("");
			}
		},
		[setAgentModeEnabled, setSelectedAgentId],
	);
	// Reconcile a persisted model selection against the live list: once the models query has resolved, a
	// stored model that no longer exists (renamed/removed on the node) falls back to the local default so the
	// composer never points at a phantom model. Guarded on loaded data so the initial default-only list
	// (before the query settles) does not clobber a still-valid persisted concrete model.
	// Also exempt cloud model selections — they are not in the local list and must not be evicted.
	useEffect(() => {
		if (!localModelsQuery.data) {
			return;
		}

		const isCloudSelection = cloudModelOptions.some((option) => option.value === selectedModel);
		if (!isCloudSelection && !modelOptions.some((option) => option.value === selectedModel)) {
			setSelectedModel(localDefaultModelValue);
		}
	}, [cloudModelOptions, localModelsQuery.data, modelOptions, selectedModel, setSelectedModel]);
	// Keep the selected reasoning effort valid for the active model's reasoning mode so the composer never SENDS an
	// effort the model can't honor. Graded models accept none/low/medium/high; binary models accept on/none. When
	// the current effort isn't in the active model's set (a graded "medium" carried onto a binary model, or a binary
	// "on" carried onto a graded model) fall back to its first available effort — "on" for binary (reason by default,
	// switch off to suppress), "none" for graded. Runs on every model switch and on first load.
	useEffect(() => {
		if (!availableReasoningEfforts.includes(reasoningEffort)) {
			setReasoningEffort(availableReasoningEfforts[0] ?? "none");
		}
	}, [availableReasoningEfforts, reasoningEffort, setReasoningEffort]);
	const selectedConcreteModelName = useMemo(() => {
		const requestModel = toNodeChatRequestModel(selectedModel);
		return requestModel ?? localModelsQuery.data?.selectedModelName ?? localModelsQuery.data?.configuredDefaultModelName ?? "";
	}, [localModelsQuery.data?.configuredDefaultModelName, localModelsQuery.data?.selectedModelName, selectedModel]);

	// getLocalModelDetails is Ollama-only — cloud (CodexOAuth) model ids are not in Ollama
	// and the endpoint 500s for them. Guard with !isCloud so we never fire the request for
	// a cloud selection. worker-be also short-circuits it server-side, but belt-and-suspenders.
	const selectedModelDetailsQuery = useQuery({
		...withResponseValidation(getLocalModelDetailsOptions({ path: { modelName: selectedConcreteModelName } })),
		enabled: selectedConcreteModelName.length > 0 && !selectedModelIsCloud,
	});

	const createConversationMutation = useMutation({
		mutationFn: () => nodeChatAdapter.createConversation({ title: "New conversation" }),
		onSuccess: async (conversation) => {
			queryClient.setQueryData<ChatConversationModel[]>(
				nodeChatQueryKeys.conversationList(showArchivedConversations),
				(current = emptyConversations) => [conversation, ...current],
			);
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
		// Keep the prior conversation's full payload mounted while the newly selected one loads so the message
		// list never collapses to the summary entry (no messages) and flashes the empty-state mid-switch.
		placeholderData: keepPreviousData,
	});
	// The full payload (with messages) hasn't settled for the currently selected conversation yet: either the
	// first load, a switch where keepPreviousData is still showing the prior thread (isPlaceholderData), or a
	// background refetch over a cached message-less entry. isFetching is the key signal — isLoading alone is
	// false whenever ANY cached/placeholder data exists for the id, which let the empty-state flash mid-fetch.
	const isLoadingSelectedConversation =
		selectedConversationId.length > 0 &&
		(selectedConversationQuery.isLoading ||
			selectedConversationQuery.isFetching ||
			selectedConversationQuery.isPlaceholderData ||
			selectedConversationQuery.data?.id !== selectedConversationId);

	const displayConversations = useMemo(
		() => mergeSelectedConversation(conversations, selectedConversationQuery.data),
		[conversations, selectedConversationQuery.data],
	);
	const activeConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
	// Remote conversations are view-only on this node (server enforces the guard; this is the cosmetic UI hide).
	const isRemoteConversation = activeConversation?.origin === "remote";

	// Hydrate the active-revision selection from the conversation's persisted selected-path once its full payload
	// loads, so navigating < N/N > variants survives a reload. Seeds once per conversation (tracked by ref) — a
	// later background refetch of the same conversation must not overwrite a selection the operator just made.
	const loadedConversation = selectedConversationQuery.data;
	useEffect(() => {
		if (!loadedConversation || loadedConversation.id !== selectedConversationId) {
			return;
		}

		if (seededSelectionConversationId.current === selectedConversationId) {
			return;
		}

		seededSelectionConversationId.current = selectedConversationId;
		setActiveRevisionByGroup(loadedConversation.selectedPath ?? {});
	}, [loadedConversation, selectedConversationId]);

	// Node-local feedback travels on each message in the loaded conversation:
	// derive the by-message map from the conversation read instead of firing a GET per assistant turn (which
	// 404'd and triggered a react-query retry storm before any feedback existed).
	const feedbackByMessageId = useMemo<Record<string, ChatMessageFeedback>>(() => {
		const byMessageId: Record<string, ChatMessageFeedback> = {};
		for (const message of activeConversation?.messages ?? []) {
			if (message.feedbackRating) {
				byMessageId[message.id] = {
					messageId: message.id,
					conversationId: message.conversationId,
					rating: message.feedbackRating,
					comment: message.feedbackComment,
					createdAt: message.createdAt,
					updatedAt: message.updatedAt ?? message.createdAt,
				};
			}
		}
		return byMessageId;
	}, [activeConversation?.messages]);
	const usedContextTokens = useMemo(
		() => deriveUsedContextTokens(activeConversation?.messages ?? []),
		[activeConversation?.messages],
	);
	const effectiveMaxContextTokens = selectedModelDetailsQuery.data?.maxContextTokens ?? undefined;
	const contextModelLabel =
		selectedConcreteModelName || selectedModelOption?.displayName || selectedModelOption?.label || "Local runtime default";
	const isLoadingInitialConversations = conversationsQuery.isLoading && displayConversations.length === 0;
	const isCreatingConversation = createConversationMutation.isPending;
	const isSending = Boolean(streamingMessage?.isActive);

	const cacheConversation = useCallback(
		(conversation: ChatConversationModel): void => {
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
			queryClient.setQueryData<ChatConversationModel[]>(
				nodeChatQueryKeys.conversationList(showArchivedConversations),
				(current = emptyConversations) => mergeSelectedConversation(current, conversation),
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
		[cacheConversation, displayConversations, selectedConversationId, selectedConversationQuery.data, setRequestedConversationId],
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
			const requestModel = toNodeChatRequestModel(model);
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
						useLocalTools: toolsEnabled,
						reasoningEffort: effort,
						// Send the active conversation-tree path so the server assembles context from the selected
						// branch only. Omit when nothing was navigated this turn so the server keeps the stored map.
						selectedPath: Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined,
						agentDefinitionId: effectiveAgentId,
						// Include sampling overrides only when developer mode is on and at least one field is set.
						// toWireSamplingOptions returns undefined when all fields are null → omitted from wire payload
						// (§3 byte-identical invariant: OFF path is byte-identical to today).
						samplingOptions: developerMode ? toWireSamplingOptions(samplingOptions) : undefined,
					},
					abortController.signal,
				)) {
					// The conversation was deleted mid-stream: stop touching its cache so the aborted turn can
					// neither re-create the removed cache entry nor be refetched in the finally below.
					if (deletedConversationIds.current.has(conversation.id)) {
						break;
					}
					const currentConversation =
						queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ??
						optimisticConversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					cacheConversation(applied.conversation);
					setStreamingMessage(applied.streamingMessage);
					const toolEntry = applied.timelineEntry;
					if (toolEntry) {
						setTimelineEntries((current) => accumulateToolTimelineEntry(current, toolEntry));
					}
				}
			} catch (error) {
				if (!abortController.signal.aborted && !deletedConversationIds.current.has(conversation.id)) {
					const message = errorMessage(error);
					const failureCategory = error instanceof StreamWatchdogError ? error.category : undefined;
					const currentConversation =
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
			cacheConversation,
			developerMode,
			queryClient,
			refreshConversation,
			resolveSendConversation,
			samplingOptions,
			selectedAgentId,
			toolsEnabled,
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
			setStreamingMessage({ conversationId: conversation.id, messageId: "", content: "", isActive: true });

			try {
				for await (const streamEvent of nodeChatAdapter.regenerateMessage(
					conversation.id,
					assistantMessageId,
					reasoningEffort,
					toolsEnabled,
					// Send the active conversation-tree path so the regenerated turn's context follows the selected
					// branch only. Omit when nothing was navigated so the server keeps the stored map.
					Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined,
					abortController.signal,
				)) {
					// The conversation was deleted mid-stream: stop touching its cache so the aborted turn can
					// neither re-create the removed cache entry nor be refetched in the finally below.
					if (deletedConversationIds.current.has(conversation.id)) {
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
						queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(conversation.id)) ?? conversation;
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					// Collapse the streaming variant onto the original in place (applyNodeChatStreamEvent rebuilds the
					// variant row per event without a group id, so re-stamp every iteration). Only once the server id
					// is latched and differs from the original — i.e. a genuine sibling, not an in-place re-render.
					const grouped =
						streamEvent.messageId && streamEvent.messageId !== assistantMessageId
							? stampVariantGroup(applied.conversation, assistantMessageId, streamEvent.messageId, variantGroupId)
							: applied.conversation;
					cacheConversation(grouped);
					setStreamingMessage(applied.streamingMessage);
					const toolEntry = applied.timelineEntry;
					if (toolEntry) {
						setTimelineEntries((current) => accumulateToolTimelineEntry(current, toolEntry));
					}
				}
				// The stream events don't carry variant_group_id; the post-stream refetch loads it from persistence
				// and groupMessageRevisions surfaces the newest sibling by default, so no explicit selection here.
			} catch (error) {
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
			cacheConversation,
			displayConversations,
			queryClient,
			reasoningEffort,
			refreshConversation,
			selectedConversationId,
			t,
			toolsEnabled,
		],
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
			const currentConversation = queryClient.getQueryData<ChatConversationModel>(
				nodeChatQueryKeys.conversation(active.conversationId),
			);
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
			titledConversations.current.add(conversationId);
			runConversationMutation(conversationId, () => nodeChatAdapter.renameConversation(conversationId, title)).catch(
				(error: unknown) => setStreamError(errorMessage(error)),
			);
		},
		[runConversationMutation],
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

	const deleteConversationMutation = useMutation({
		mutationFn: (conversationId: string) => nodeChatAdapter.deleteConversation(conversationId),
		onSuccess: async (_result, conversationId) => {
			// Drop the deleted thread from caches and, when it was the open one, clear the selection so the
			// list falls back to the newest remaining conversation.
			queryClient.removeQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId) });
			if (requestedConversationId === conversationId) {
				setRequestedConversationId("");
			}
			titledConversations.current.delete(conversationId);
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

			// Abort an in-flight stream for this thread before deleting, and flag it so the streaming loop stops
			// re-caching/refetching the just-removed conversation (otherwise the abort resurrects it as a 404).
			deletedConversationIds.current.add(conversationId);
			if (activeStream.current?.conversationId === conversationId) {
				activeStream.current.abortController.abort();
			}

			setStreamError(undefined);
			setMutatingConversationId(conversationId);
			try {
				await deleteConversationMutation.mutateAsync(conversationId);
			} catch (error) {
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
		[confirm, deleteConversationMutation, refreshConversation, t],
	);

	const handleDeleteConversation = useCallback(
		(conversationId: string, skipConfirm: boolean): void => {
			deleteConversation(conversationId, skipConfirm).catch((error: unknown) => setStreamError(errorMessage(error)));
		},
		[deleteConversation],
	);

	const handleSelectRevision = useCallback(
		(variantGroupId: string, messageId: string): void => {
			const nextSelection: Record<string, string> = { ...activeRevisionByGroup, [variantGroupId]: messageId };
			setActiveRevisionByGroup(nextSelection);
			// Persist the navigated selection so a reload restores it even without sending a message. Fire-and-forget,
			// consistent with other adapter calls; a failure surfaces in the error banner but never blocks the UI.
			if (selectedConversationId) {
				nodeChatAdapter
					.persistSelectedPath(selectedConversationId, nextSelection)
					.catch((error: unknown) => setStreamError(errorMessage(error)));
			}
		},
		[activeRevisionByGroup, selectedConversationId],
	);

	const branchConversation = useCallback(
		async (messageId: string): Promise<void> => {
			setStreamError(undefined);
			try {
				const result = await nodeChatAdapter.branchConversation(selectedConversationId, messageId);
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
		[queryClient, selectedConversationId, setRequestedConversationId, t],
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
	// was dropped (RC #3). When undefined, ChatDisplayShell renders no alert.
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
					<Text size="sm">
						{t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node.")}
					</Text>
				</Stack>
			) : undefined,
		[conversationsQuery.error, conversationsQuery.isError, isRemoteConversation, streamError, t],
	);

	// A9 module-readiness gate (platform parity): block the chat behind a connecting/error state until the
	// shared hub is live. Once connected it latches `ready` and transient reconnects are handled in-band.
	if (connectionReadiness !== "ready") {
		return (
			<Box py="lg" style={{ display: "flex", flexDirection: "column", height: "100%", minHeight: 0 }}>
				<Center style={{ flex: 1 }}>
					{connectionReadiness === "connecting" ? (
						<Stack align="center" gap="sm">
							<Loader />
							<Text c="dimmed">{t("pages.chat.connecting", "Connecting to local chat…")}</Text>
						</Stack>
					) : (
						<Alert
							color="red"
							variant="light"
							icon={<IconAlertTriangle size={16} />}
							title={t("pages.chat.connectionFailedTitle", "Local chat unavailable")}
						>
							<Stack gap="sm" align="flex-start">
								<Text size="sm">
									{connectionError ?? t("pages.chat.connectionFailed", "Could not connect to the local chat hub.")}
								</Text>
								{/* Retry re-arms connect(): readiness flips error → connecting and this whole gate re-renders to
								    the centered Loader above. Accepted as-is — no in-button spinner; the connecting state IS
								    the feedback, and the gate swap is a clean replace, not a flicker. */}
								<Button size="xs" variant="light" onClick={retryConnection}>
									{t("pages.chat.retryConnection", "Retry")}
								</Button>
							</Stack>
						</Alert>
					)}
				</Center>
			</Box>
		);
	}

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
				cloudModelOptions={cloudModelOptions}
				selectedModel={selectedModel}
				reasoningEffort={reasoningEffort}
				availableReasoningEfforts={availableReasoningEfforts}
				activeModelToolCapable={activeModelToolCapable}
				toolsEnabled={toolsEnabled}
				capabilities={chatUiCapabilities}
				contextUsage={{
					usedTokens: usedContextTokens,
					maxTokens: effectiveMaxContextTokens,
					isAuthoritative: usedContextTokens !== undefined,
					modelLabel: contextModelLabel,
					nodeLabel: "Local node",
				}}
				streamingMessage={streamingMessage}
				timelineEntries={timelineEntries}
				disabledNotice={notice}
				isLoadingMessages={isLoadingSelectedConversation}
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
				onToggleConversationList={toggleSidebar}
				onModelChange={setSelectedModel}
				onReasoningEffortChange={setReasoningEffort}
				onToggleTools={toggleTools}
				agentControlsAvailable={agentControlsAvailable}
				agentModeEnabled={agentModeEnabled}
				selectedAgentId={selectedAgentId}
				agentOptions={agentOptions}
				onSelectAgent={handleSelectAgent}
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
				onDeleteConversation={handleDeleteConversation}
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
