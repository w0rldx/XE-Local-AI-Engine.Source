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
import { useStreamCommitScheduler } from "@/features/chat/hooks/useStreamCommitScheduler";
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
import { shouldFetchLocalModelDetails } from "@/features/chat/pages/ChatModelDetailsQuery";
import { resolveLocalDefaultModelCapabilities, toChatModelOptions } from "@/features/chat/pages/ChatModelOptions";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import { useCodexModelOptions } from "@/features/chat/queries/useCodexModelOptions";
import { useConversationAttachments } from "@/features/chat/queries/useConversationAttachments";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";
import {
	binaryReasoningEfforts,
	clampReasoningEffort,
	codexReasoningEfforts,
	reasoningEfforts,
	useNodeChatPreferencesStore,
} from "@/features/chat/stores/NodeChatPreferencesStore";
import { useVoicePlayback } from "@/features/voice/useVoicePlayback";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

/* eslint-disable react-doctor/no-giant-component, react-doctor/prefer-useReducer, react-doctor/js-combine-iterations */

// Base identity for the synthetic "Local default" composer option. Capabilities are filled in dynamically inside
// modelOptions (see below) from the concrete model the runtime will resolve, so picking "Local default" mirrors the
// reasoning/tool controls of picking that model directly. The false capabilities here are only the pre-load default
// (used until the local model list arrives).
const localDefaultModelOptionBase: ModelOption = {
	value: localDefaultModelValue,
	label: "Local default",
	displayName: "Local runtime default",
	isReasoningModel: false,
	isToolCapable: false,
	isAvailable: true,
	statusLabel: "Runtime-selected model",
};

const emptyConversations: ChatConversationModel[] = [];

interface ActiveChatStream {
	conversationId: string;
	messageId: string;
	requestId: string;
	abortController: AbortController;
}

// One rAF-batched streaming commit (see commitStreamState / useStreamCommitScheduler).
interface PendingStreamCommit {
	conversation: ChatConversationModel;
	// Terminal frames also refresh the conversation-LIST cache; per-token frames update only the detail cache.
	writeConversationList: boolean;
	streamingMessage: ChatStreamingState;
	// Tool-lifecycle entries seen since the last flush; empty on plain token deltas.
	toolTimelineEntries: ChatTimelineEntry[];
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
	// Lazy-init so the Set is built once instead of allocating a throwaway `new Set()` on every render;
	// the literal `useRef` (not a wrapper) keeps lint treating `.current` as a stable, non-reactive ref.
	const deletedConversationIds = useRef<Set<string>>(undefined as unknown as Set<string>);
	if (!deletedConversationIds.current) {
		deletedConversationIds.current = new Set<string>();
	}
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
		clearSelectedAgent,
	} = useNodeChatPreferencesStore((state) => state.actions);
	// Developer mode + per-send sampling overrides. Read directly from global stores.
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const samplingOptions = useChatSamplingPreferencesStore((state) => state.options);
	// Voice runtime: the manifest-derived gate drives showVoiceControls; the playback tap mirrors the stream into TTS.
	const voiceRuntime = useVoiceRuntime();
	const { onTurnStart: onVoiceTurnStart, onAnswerProgress: onVoiceAnswerProgress } = useVoicePlayback();
	const chatUiCapabilities = useMemo(
		() => buildChatUiCapabilities(nodeCapabilities.chat, voiceRuntime.manifest?.enabled ?? false),
		[voiceRuntime.manifest?.enabled],
	);
	const { readiness: connectionReadiness, error: connectionError, retry: retryConnection } = useNodeChatConnectionReadiness();
	const [streamingMessage, setStreamingMessage] = useState<ChatStreamingState | undefined>();
	// Tool-call activity entries accumulated over the current streaming turn (keyed by tool call id). Reset per turn.
	const [timelineEntries, setTimelineEntries] = useState<ChatTimelineEntry[]>([]);
	const [streamError, setStreamError] = useState<string | undefined>();
	const [conversationSearchQuery, setConversationSearchQuery] = useState("");
	const [showArchivedConversations, setShowArchivedConversations] = useState(false);
	const [mutatingConversationId, setMutatingConversationId] = useState<string | undefined>();
	// Operator's in-session revision picks (variantGroupId → active messageId), scoped to the conversation they were
	// made in. Layered over the conversation's persisted selected-path baseline to derive the effective map below, so
	// the active selection is computed during render rather than copied into state through an effect (no extra render).
	const [revisionOverrides, setRevisionOverrides] = useState<{ conversationId: string; overrides: Record<string, string> }>({
		conversationId: "",
		overrides: {},
	});
	const [pendingFeedbackMessageId, setPendingFeedbackMessageId] = useState<string | undefined>();
	// Conversations whose first message has already promoted their title (avoids re-renaming on every send).
	// Lazy-init (see deletedConversationIds): build the Set once, not a throwaway per render.
	const titledConversations = useRef<Set<string>>(undefined as unknown as Set<string>);
	if (!titledConversations.current) {
		titledConversations.current = new Set<string>();
	}
	// The persisted selected-path baseline latched the first time each conversation's full payload loads. Latching
	// once (keyed by conversation id) means a later background refetch never clobbers an in-session selection the
	// operator just navigated — the override layer always wins over this frozen baseline.
	const revisionBaseline = useRef<{ conversationId: string; selectedPath: Record<string, string> }>({
		conversationId: "",
		selectedPath: {},
	});

	const {
		data: conversationsData,
		isLoading: conversationsIsLoading,
		isError: conversationsIsError,
		error: conversationsError,
	} = useQuery({
		queryKey: nodeChatQueryKeys.conversationList(showArchivedConversations),
		queryFn: ({ signal }) => nodeChatAdapter.listConversations({ includeArchived: showArchivedConversations, signal }),
	});

	const { data: localModelsData } = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Keep the prior model list while a refetch is in flight so a transient response that momentarily omits
		// the selected model can't trip the reconcile effect and reset selectedModel to the default (which would
		// undercut the persisted model selection restored from localStorage).
		placeholderData: keepPreviousData,
	});

	const modelOptions = useMemo<ModelOption[]>(() => {
		const response = localModelsData;
		if (!response) {
			return [localDefaultModelOptionBase];
		}

		// Mirror the resolved concrete model's capabilities onto the Local-default option so its reasoning/tool
		// controls match picking that model directly (see resolveLocalDefaultModelCapabilities).
		const items = response.items ?? [];
		const localDefaultModelOption: ModelOption = {
			...localDefaultModelOptionBase,
			...resolveLocalDefaultModelCapabilities(items),
		};
		return [localDefaultModelOption, ...toChatModelOptions(items, response.isAvailable ?? false)];
	}, [localModelsData]);
	// Cloud (Codex) model options — empty array when signed out; non-empty only when Codex session active.
	const cloudModelOptions = useCodexModelOptions();
	const selectedModelOption = useMemo(
		() =>
			modelOptions.find((option) => option.value === selectedModel) ??
			cloudModelOptions.find((option) => option.value === selectedModel),
		[cloudModelOptions, modelOptions, selectedModel],
	);
	// Per-model capability gating: only offer the reasoning-effort menu when the active model
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
				playbookEnabled: agent.playbookEnabled,
			}))
			.sort((a, b) => a.name.localeCompare(b.name));
	}, [agentDefinitionsQuery.data]);
	// agentControlsAvailable: capability gate AND at least one agent in the live list.
	const agentControlsAvailable = chatUiCapabilities.showAgentControls && agentOptions.length > 0;
	// Whether the currently bound agent (agent mode on + a selected agent that still exists) has adaptive memory
	// enabled. Gates the temporary-chat toggle in the chat header — there is nothing to suppress unless the agent
	// learns memory at all. Default Assistant / mode-off => no bound agent => false.
	const boundAgentMemoryEnabled = useMemo(() => {
		if (!agentModeEnabled || !selectedAgentId) {
			return false;
		}
		return agentOptions.find((agent) => agent.id === selectedAgentId)?.playbookEnabled ?? false;
	}, [agentModeEnabled, agentOptions, selectedAgentId]);
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
		if (!localModelsData) {
			return;
		}

		const isCloudSelection = cloudModelOptions.some((option) => option.value === selectedModel);
		if (!isCloudSelection && !modelOptions.some((option) => option.value === selectedModel)) {
			setSelectedModel(localDefaultModelValue);
		}
	}, [cloudModelOptions, localModelsData, modelOptions, selectedModel, setSelectedModel]);
	// Keep the selected reasoning effort valid for the active model's reasoning mode so the composer never SENDS an
	// effort the model can't honor. Graded models accept none/low/medium/high; binary models accept on/none; Codex
	// models add minimal/xhigh. When the current effort isn't in the active model's set (a Codex "xhigh" carried onto
	// a graded model, or a binary "on" carried onto a graded model) clampReasoningEffort maps to the nearest valid
	// level that PRESERVES reasoning intent — xhigh→high, minimal→low, graded→"on" for binary — instead of collapsing
	// reasoning OFF. Only "none" maps to "none". Runs on every model switch and on first load.
	useEffect(() => {
		const clamped = clampReasoningEffort(reasoningEffort, availableReasoningEfforts);
		if (clamped !== reasoningEffort) {
			setReasoningEffort(clamped);
		}
	}, [availableReasoningEfforts, reasoningEffort, setReasoningEffort]);
	const selectedConcreteModelName = useMemo(() => {
		const requestModel = toNodeChatRequestModel(selectedModel);
		return requestModel ?? localModelsData?.selectedModelName ?? localModelsData?.configuredDefaultModelName ?? "";
	}, [localModelsData?.configuredDefaultModelName, localModelsData?.selectedModelName, selectedModel]);

	// Only poll model-details when the selection can actually return them: a non-empty local (non-cloud) name whose
	// list option, if known, is available AND whose concrete name is actually installed. Cloud (Codex) ids have no
	// LOCAL details (the endpoint 404s for them), an unavailable model just retries a guaranteed failure, and a
	// configured-but-not-installed default (its GGUF never downloaded) 404s forever until the install lands. GGUF
	// (llamacpp) selections that are installed ARE polled — CL-4 serves their details as a 200 carrying
	// maxContextTokens, which the context meter needs.
	const concreteModelInstalled = useMemo(
		() =>
			selectedConcreteModelName.length > 0 &&
			modelOptions.some((option) => option.value === selectedConcreteModelName),
		[modelOptions, selectedConcreteModelName],
	);
	const selectedModelDetailsEnabled = shouldFetchLocalModelDetails(
		selectedConcreteModelName,
		selectedModelOption,
		selectedModelIsCloud,
		concreteModelInstalled,
	);
	const { data: selectedModelDetails } = useQuery({
		...withResponseValidation(getLocalModelDetailsOptions({ path: { modelName: selectedConcreteModelName } })),
		enabled: selectedModelDetailsEnabled,
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
			// A new conversation must not inherit the previous thread's pinned agent — clear the
			// selection so the composer starts clean. Model stays sticky (intentional UX).
			clearSelectedAgent();
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
		},
	});

	const conversations = conversationsData ?? emptyConversations;
	const requestedConversationExists = conversations.some((conversation) => conversation.id === requestedConversationId);
	const selectedConversationId = requestedConversationExists ? requestedConversationId : (conversations[0]?.id ?? "");

	const {
		data: selectedConversationData,
		isLoading: selectedConversationIsLoading,
		isFetching: selectedConversationIsFetching,
		isPlaceholderData: selectedConversationIsPlaceholderData,
		isError: selectedConversationIsError,
		error: selectedConversationError,
		refetch: refetchSelectedConversation,
	} = useQuery({
		queryKey: nodeChatQueryKeys.conversation(selectedConversationId),
		queryFn: ({ signal }) => nodeChatAdapter.getConversation(selectedConversationId, { signal }),
		enabled: selectedConversationId.length > 0,
		// Keep the prior conversation's full payload mounted while the newly selected one loads so the message
		// list never collapses to the summary entry (no messages) and flashes the empty-state mid-switch.
		placeholderData: keepPreviousData,
	});
	// The selected conversation's full payload failed to load AND we don't already hold its payload (a background
	// refetch failing over good data must NOT blow away the thread — that stays showing the cached messages). The
	// query is keyed by selectedConversationId, so `isError` reflects the CURRENT selection; switching threads
	// re-keys the query and clears this. Drives the inline error+retry state in the message list. Without it, a
	// permanently-failing getConversation left the loading term below true forever (spinner deadlock, no error).
	const selectedConversationLoadFailed =
		selectedConversationId.length > 0 &&
		selectedConversationIsError &&
		selectedConversationData?.id !== selectedConversationId;
	// The full payload (with messages) hasn't settled for the currently selected conversation yet: either the
	// first load, a switch where keepPreviousData is still showing the prior thread (isPlaceholderData), or a
	// background refetch over a cached message-less entry. isFetching is the key signal — isLoading alone is
	// false whenever ANY cached/placeholder data exists for the id, which let the empty-state flash mid-fetch.
	// The failure state takes precedence: once the load has errored we surface the error+retry, not a spinner
	// that would otherwise spin forever (the id-mismatch term below never clears on a permanent failure).
	const isLoadingSelectedConversation =
		!selectedConversationLoadFailed &&
		selectedConversationId.length > 0 &&
		(selectedConversationIsLoading ||
			selectedConversationIsFetching ||
			selectedConversationIsPlaceholderData ||
			selectedConversationData?.id !== selectedConversationId);
	const handleRetryLoadMessages = useCallback(() => {
		// Fire-and-forget refetch: any failure re-lands in the query's own isError state (which drives this same
		// error surface), so there is nothing extra to handle here — mirror the adapter fire-and-forget convention.
		refetchSelectedConversation().catch(() => undefined);
	}, [refetchSelectedConversation]);

	// Gate the selected conversation against the current selection before merging it into the displayed list.
	// When the last conversation is deleted, selectedConversationId becomes "" and the query disables — but its
	// `.data` stays STALE (it still holds the just-deleted conversation, keepPreviousData never clears it).
	// Injecting that stale payload would render a ghost row, so only feed the merge the selected conversation when
	// the selection is live (non-empty) AND the cached payload actually matches it.
	const selectedConversationForMerge =
		selectedConversationId.length > 0 && selectedConversationData?.id === selectedConversationId
			? selectedConversationData
			: undefined;
	const displayConversations = useMemo(
		() => mergeSelectedConversation(conversations, selectedConversationForMerge),
		[conversations, selectedConversationForMerge],
	);
	const activeConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
	// Remote conversations are view-only on this node (server enforces the guard; this is the cosmetic UI hide).
	const isRemoteConversation = activeConversation?.origin === "remote";

	// Latch the persisted selected-path baseline for the current conversation the first time its full payload loads,
	// so navigating < N/N > variants survives a reload. Latched once per conversation (tracked by ref) — a later
	// background refetch of the same conversation must not overwrite a selection the operator just made. Computed
	// during render (no effect → no extra render); writing a ref here is render-safe because it only memoizes and
	// never schedules a re-render.
	const loadedConversation = selectedConversationData;
	if (
		loadedConversation &&
		loadedConversation.id === selectedConversationId &&
		revisionBaseline.current.conversationId !== selectedConversationId
	) {
		revisionBaseline.current = { conversationId: selectedConversationId, selectedPath: loadedConversation.selectedPath ?? {} };
	}

	// Effective active-revision map: the frozen server baseline for the current conversation overlaid with the
	// operator's in-session picks (only those made in THIS conversation apply). Derived during render instead of
	// stored in state. Reads loadedConversation so the map recomputes when the full payload loads: the ref-latch block
	// above sets revisionBaseline.current on that same render, and the fallback below also reads the freshly-loaded
	// payload directly, so the baseline is correct whether or not the ref has latched yet.
	const activeRevisionByGroup = useMemo<Record<string, string>>(() => {
		const isCurrentLoaded = loadedConversation?.id === selectedConversationId;
		const baseline =
			revisionBaseline.current.conversationId === selectedConversationId
				? revisionBaseline.current.selectedPath
				: isCurrentLoaded
					? (loadedConversation?.selectedPath ?? {})
					: {};
		const overrides = revisionOverrides.conversationId === selectedConversationId ? revisionOverrides.overrides : {};
		return { ...baseline, ...overrides };
	}, [revisionOverrides, selectedConversationId, loadedConversation]);

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
	const effectiveMaxContextTokens = selectedModelDetails?.maxContextTokens ?? undefined;
	const contextModelLabel =
		selectedConcreteModelName || selectedModelOption?.displayName || selectedModelOption?.label || "Local runtime default";
	const isLoadingInitialConversations = conversationsIsLoading && displayConversations.length === 0;
	const isCreatingConversation = createConversationMutation.isPending;
	const isSending = Boolean(streamingMessage?.isActive);

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
			queryClient.setQueryData<ChatConversationModel[]>(
				nodeChatQueryKeys.conversationList(showArchivedConversations),
				(current = emptyConversations) => mergeSelectedConversation(current, conversation),
			);
		},
		[cacheConversationDetail, queryClient, showArchivedConversations],
	);

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

	const resolveSendConversation = useCallback(
		async (content: string): Promise<ChatConversationModel> => {
			if (selectedConversationData) {
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
		[cacheConversation, displayConversations, selectedConversationId, selectedConversationData, setRequestedConversationId],
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

			let lastVoiceStreaming: ChatStreamingState | undefined;
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
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					latestConversation = applied.conversation;
					lastVoiceStreaming = applied.streamingMessage;
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
				if (lastVoiceStreaming) {
					onVoiceAnswerProgress({ ...lastVoiceStreaming, isActive: false });
				}
			} catch (error) {
				// The turn errored: drop any batched delta and write the failed state synchronously below.
				streamScheduler.cancel();
				if (!abortController.signal.aborted && !deletedConversationIds.current.has(conversation.id)) {
					const message = errorMessage(error);
					const failureCategory = error instanceof StreamWatchdogError ? error.category : undefined;
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
			streamScheduler,
			toolsEnabled,
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

			let lastVoiceStreaming: ChatStreamingState | undefined;
			// Running reduced conversation for THIS regenerate turn (see handleSend): each event folds onto the
			// previous grouped conversation so batched frames don't read a not-yet-flushed query cache.
			let latestConversation: ChatConversationModel | undefined;
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
					const applied = applyNodeChatStreamEvent(currentConversation, streamEvent);
					// Collapse the streaming variant onto the original in place (applyNodeChatStreamEvent rebuilds the
					// variant row per event without a group id, so re-stamp every iteration). Only once the server id
					// is latched and differs from the original — i.e. a genuine sibling, not an in-place re-render.
					const grouped =
						streamEvent.messageId && streamEvent.messageId !== assistantMessageId
							? stampVariantGroup(applied.conversation, assistantMessageId, streamEvent.messageId, variantGroupId)
							: applied.conversation;
					latestConversation = grouped;
					lastVoiceStreaming = applied.streamingMessage;
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
				if (lastVoiceStreaming) {
					onVoiceAnswerProgress({ ...lastVoiceStreaming, isActive: false });
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
			displayConversations,
			queryClient,
			reasoningEffort,
			refreshConversation,
			selectedConversationId,
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
		[regenerate],
	);

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
	}, [cacheConversation, queryClient, refreshConversation, onVoiceTurnStart]);

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
			// Record the pick as an in-session override scoped to the current conversation; the effective map is
			// derived from baseline + overrides during render. A conversation switch carries a fresh overrides bucket
			// (keyed by conversationId), so picks never leak across threads.
			setRevisionOverrides((current) => {
				const base = current.conversationId === selectedConversationId ? current.overrides : {};
				return { conversationId: selectedConversationId, overrides: { ...base, [variantGroupId]: messageId } };
			});
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
	const notice = useMemo(
		() =>
			streamError ? (
				<Stack gap={2}>
					<Text fw={700}>Local chat stream failed.</Text>
					<Text size="sm">{streamError}</Text>
				</Stack>
			) : conversationsIsError ? (
				<Stack gap={2}>
					<Text fw={700}>Unable to load local chat history.</Text>
					<Text size="sm">{errorMessage(conversationsError)}</Text>
				</Stack>
			) : isRemoteConversation ? (
				<Stack gap={2}>
					<Text fw={700}>{t("pages.chat.remoteViewOnlyTitle", "Remote conversation")}</Text>
					<Text size="sm">
						{t("pages.chat.remoteViewOnly", "This conversation was started from a paired client and is view-only on this node.")}
					</Text>
				</Stack>
			) : undefined,
		[conversationsError, conversationsIsError, isRemoteConversation, streamError, t],
	);

	// Module-readiness gate (platform parity): block the chat behind a connecting/error state until the
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
				messagesLoadFailed={selectedConversationLoadFailed}
				messagesLoadErrorText={selectedConversationLoadFailed ? errorMessage(selectedConversationError) : undefined}
				onRetryLoadMessages={handleRetryLoadMessages}
				inputStatus={{
					isSending,
					chatInputDisabled: isCreatingConversation || isRemoteConversation,
					modelSelectorDisabled: isRemoteConversation,
					sendDisabled: selectedConversationIsLoading || isRemoteConversation,
				}}
				conversationSearchQuery={conversationSearchQuery}
				showArchivedConversations={showArchivedConversations}
				mutatingConversationId={mutatingConversationId}
				conversationListCollapsed={collapsed}
				onSelectConversation={setRequestedConversationId}
				onCreateConversation={handleCreateConversation}
				onToggleConversationList={toggleSidebar}
				onModelChange={setSelectedModel}
				onReasoningEffortChange={setReasoningEffort}
				onToggleTools={toggleTools}
				agentControlsAvailable={agentControlsAvailable}
				agentModeEnabled={agentModeEnabled}
				selectedAgentId={selectedAgentId}
				agentOptions={agentOptions}
				onSelectAgent={handleSelectAgent}
				attachments={attachments}
				pendingUploads={pendingUploads}
				onUploadFiles={handleUploadAttachments}
				onRemoveAttachment={handleRemoveAttachment}
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
				boundAgentMemoryEnabled={boundAgentMemoryEnabled}
				onToggleConversationMemoryExcluded={handleToggleConversationMemoryExcluded}
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
