import type { TFunction } from "i18next";

import { formatTime } from "@/core/formatting/TimeFormatting";
import type {
	ChatFeedbackRating,
	ChatMessageModel,
	ChatMessagePart,
	ChatMessageRevisionNav,
} from "@/features/chat/models/ChatModels";

const EMPTY_PARTS: ChatMessagePart[] = [];

// Verbatim copy of ProviderCallBudget.StepCallCapReachedMessage (XE-Local-AI-Engine.AI.Agent/Invocation/
// ProviderCallBudget.cs), the fixed message a work-session step carries when it ends because it spent its
// per-step provider-call cap. The backend cannot un-fail that row (a terminalized chat message is immutable) and
// the stream event carries no failure category, so the text IS the signal. Pinned server-side by
// ProviderCallBudgetTests.RegisterProviderRound_WhenTheStepCapTrips_ReportsTheStepMessage — change both together.
const STEP_CALL_CAP_MESSAGE =
	"This step reached its provider-call cap; the work session continues from its saved state on the next step.";

/**
 * Resolves the ordered parts to render for an assistant turn. Prefers the streaming/persisted `parts`; otherwise
 * synthesizes a single Thoughts segment from the flat `reasoning` blob (legacy turns + direct renders), keyed on
 * the message id so the reasoning controls keep their stable testids.
 */
function resolveParts(message: ChatMessageModel, streamingParts: ChatMessagePart[] | undefined): ChatMessagePart[] {
	if (streamingParts && streamingParts.length > 0) {
		return streamingParts;
	}

	if (message.parts && message.parts.length > 0) {
		return message.parts;
	}

	if (message.reasoning && message.reasoning.trim().length > 0) {
		return [{ kind: "reasoning", id: message.id, sequence: 0, text: message.reasoning }];
	}

	return EMPTY_PARTS;
}

export function hasText(value?: string): boolean {
	return typeof value === "string" && value.trim().length > 0;
}

function roleLabel(role: ChatMessageModel["role"], t: ChatMessageDisplayInput["t"]): string {
	if (role === "assistant") {
		return t("pages.chat.roles.assistant", "Assistant");
	}
	return role === "user" ? t("pages.chat.roles.you", "You") : role;
}

function timeText(iso?: string): string {
	if (!iso) {
		return "";
	}

	// Empty, not the helper's dash: an unusable stamp leaves the bubble's corner blank rather than printing a
	// placeholder into the middle of a conversation.
	if (Number.isNaN(new Date(iso).getTime())) {
		return "";
	}
	return formatTime(iso, { hour: "2-digit", minute: "2-digit" });
}

export interface ChatMessageDisplayInput {
	message: ChatMessageModel;
	placeholder?: string;
	isStreaming: boolean;
	streamingParts?: ChatMessagePart[];
	onRegenerate?: (messageId: string) => void;
	onBranch?: (messageId: string) => void;
	revisionNav?: ChatMessageRevisionNav;
	showFeedbackControls: boolean;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	// Live failure classification (e.g. "inter-chunk-stall") from the stream state.
	failureCategory?: string;
	isWorkSessionConversation: boolean;
	t: TFunction;
}

export interface ChatMessageDisplay {
	label: string;
	userMessage: boolean;
	assistantMessage: boolean;
	content?: string;
	time: string;
	agentDisplayName?: string;
	modelLabel?: string;
	reasoningLabel?: string;
	tpsLabel?: string;
	hasContentStarted: boolean;
	parts: ChatMessagePart[];
	errorText?: string;
	isCancelled: boolean;
	isStepBudgetNotice: boolean;
	showErrorAlert: boolean;
	canCopy: boolean;
	canRegenerate: boolean;
	canBranch: boolean;
	showRevisionNav: boolean;
	showFeedback: boolean;
	showMenu: boolean;
	hasActions: boolean;
}

/** Everything a chat bubble renders that is derived rather than rendered — no hooks, no JSX. */
export function deriveChatMessageDisplay({
	message,
	placeholder,
	isStreaming,
	streamingParts,
	onRegenerate,
	onBranch,
	revisionNav,
	showFeedbackControls,
	onSubmitFeedback,
	failureCategory,
	isWorkSessionConversation,
	t,
}: ChatMessageDisplayInput): ChatMessageDisplay {
	const label = roleLabel(message.role, t);
	const userMessage = message.role === "user";
	const assistantMessage = message.role === "assistant";
	const content = message.content.trim().length > 0 ? message.content : placeholder;
	const time = timeText(message.updatedAt ?? message.createdAt);
	// Agent attribution: falls back to "Default Assistant" so every assistant turn shows a name.
	// During streaming, message.agentName is the locally-selected agent name stamped optimistically at send
	// time (see appendOptimisticNodeChatSend) and carried through every stream-state rebuild, so the correct
	// agent shows live. The fallback only covers legacy turns and turns sent with no agent selected.
	const agentDisplayName = assistantMessage
		? (message.agentName ?? t("pages.chat.defaultAgentName", "Default Assistant"))
		: undefined;
	// Model that produced the turn (ground truth from the persisted message — Ollama id or Codex/cloud id).
	// Shown on every assistant turn that carries a model so multiple-provider threads stay auditable. Absent for
	// legacy turns with no persisted model and for user turns → omitted.
	const modelLabel =
		assistantMessage && message.model != null && message.model.trim().length > 0
			? t("pages.chat.messageModelLabel", "Model: {{model}}", { model: message.model })
			: undefined;
	// Reasoning effort used at generation time (ground truth from persisted metadata_json). Shown on every
	// assistant turn where it is present, including "none" → "off". Absent for legacy/user turns → omitted.
	const reasoningLabel =
		assistantMessage && message.reasoningEffort != null
			? t("pages.chat.reasoning.label", "Reasoning: {{effort}}", {
					effort: t(`pages.chat.reasoning.effort.${message.reasoningEffort}`, message.reasoningEffort),
				})
			: undefined;
	// Overall tokens/sec for the turn = output tokens / wall-clock generation seconds. Both must be present and
	// the duration positive, and the result finite & > 0, or no figure is shown (legacy turns have no duration).
	const tps =
		assistantMessage &&
		message.generationDurationMs != null &&
		message.generationDurationMs > 0 &&
		message.outputTokens != null &&
		message.outputTokens > 0
			? Math.round(message.outputTokens / (message.generationDurationMs / 1000))
			: undefined;
	const tpsLabel =
		tps != null && Number.isFinite(tps) && tps > 0
			? t("pages.chat.tokensPerSecond", "{{value}} tok/s", { value: tps })
			: undefined;
	const hasContentStarted = message.content.trim().length > 0;
	const parts = assistantMessage ? resolveParts(message, streamingParts) : EMPTY_PARTS;
	// A failed assistant turn carries an `error`. Render it as a highlighted block inside the bubble —
	// exactly once, always, regardless of whether the turn also has partial content. This covers the case
	// where the model streamed some text before failing: both the partial content AND the error block show.
	// StreamingIndicator no longer renders errors (single render site).
	const errorText = assistantMessage && hasText(message.error) ? message.error?.trim() : undefined;
	// A user-cancelled turn is a neutral, expected outcome — not a failure. It renders as a subdued
	// "Generation stopped" line, never the red error Alert, even when the backend persisted an error string
	// alongside the cancelled status. Classification is driven purely by the terminal `status`, never by
	// string-matching the (localized) error text, so it holds for the live cancel, the cancelled stream event,
	// and the persisted reload alike.
	const isCancelled = assistantMessage && message.status === "cancelled";
	// A work-session step that ended on its own provider-call cap is a bound being spent, not a fault: the tools it
	// ran are persisted and the next step resumes from the saved state. The row is persisted `failed` (a chat message
	// cannot be un-failed once terminalized) and carries no category, so the fixed backend message is the only signal
	// — matched verbatim, and only inside a session, so ordinary chat is untouched.
	const isStepBudgetNotice =
		isWorkSessionConversation && assistantMessage && message.status === "failed" && errorText === STEP_CALL_CAP_MESSAGE;
	const showErrorAlert = !isCancelled && !isStepBudgetNotice && (Boolean(errorText) || failureCategory === "ModelNotInstalled");
	const canCopy = hasContentStarted && !isStreaming;
	const canRegenerate = assistantMessage && !isStreaming && Boolean(onRegenerate);
	const canBranch = assistantMessage && !isStreaming && Boolean(onBranch);
	const showRevisionNav = assistantMessage && !isStreaming && Boolean(revisionNav) && (revisionNav?.total ?? 0) > 1;
	const showFeedback = assistantMessage && !isStreaming && showFeedbackControls && Boolean(onSubmitFeedback) && hasContentStarted;
	// The ⋮ options menu shows on every completed assistant turn (not while streaming), independent of the other
	// actions, so the menu is always reachable. Including it in hasActions guarantees the actions row renders.
	const showMenu = assistantMessage && !isStreaming;

	return {
		label,
		userMessage,
		assistantMessage,
		content,
		time,
		agentDisplayName,
		modelLabel,
		reasoningLabel,
		tpsLabel,
		hasContentStarted,
		parts,
		errorText,
		isCancelled,
		isStepBudgetNotice,
		showErrorAlert,
		canCopy,
		canRegenerate,
		canBranch,
		showRevisionNav,
		showFeedback,
		showMenu,
		hasActions: canCopy || canRegenerate || canBranch || showRevisionNav || showFeedback || showMenu,
	};
}
