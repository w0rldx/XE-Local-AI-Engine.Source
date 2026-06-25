import { Alert, Anchor, Avatar, Badge, Box, Group, Paper, Stack, Text } from "@mantine/core";
import { IconAlertTriangle, IconSparkles } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { AnimatePresence, m, useReducedMotion } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import { ChatMessageActions, type ChatMessageActionCapabilities, type ChatMessageRevisionNav } from "@/features/chat/components/ChatMessageActions";
import { CHAT_ACCENT, CHAT_ASSISTANT_BACKGROUND, CHAT_ASSISTANT_BORDER } from "@/features/chat/components/ChatVisualTokens";
import { MessageParts } from "@/features/chat/components/MessageParts";
import type {
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatMessagePart,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

const EMPTY_PARTS: ChatMessagePart[] = [];

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

interface ChatMessageProps {
	message: ChatMessageModel;
	placeholder?: string;
	footer?: ReactNode;
	isStreaming?: boolean;
	// Ordered interleave parts for the in-flight turn (from the stream reducer). When absent the component falls
	// back to the persisted `message.parts`, then to a synthesized Thoughts segment from `message.reasoning`.
	streamingParts?: ChatMessagePart[];
	streamingReasoningOverflowBytes?: number;
	onRegenerate?: (messageId: string) => void;
	revisionNav?: ChatMessageRevisionNav;
	onBranch?: (messageId: string) => void;
	showFeedbackControls?: boolean;
	feedback?: ChatMessageFeedback;
	feedbackPending?: boolean;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	// The active composer reasoning effort, used to flag reasoning emitted while "none" is selected.
	reasoningEffort?: ReasoningEffort;
	// Live failure classification (e.g. "inter-chunk-stall") from the stream state. Only present for the
	// transient streaming turn; persisted failed turns carry just `message.error`. Folded into the error block.
	failureCategory?: string;
}

function hasText(value?: string): boolean {
	return typeof value === "string" && value.trim().length > 0;
}

function timeText(iso?: string): string {
	if (!iso) {
		return "";
	}

	const date = new Date(iso);
	return Number.isNaN(date.getTime()) ? "" : date.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

function roleLabel(role: ChatMessageModel["role"]): string {
	return role === "assistant" ? "Assistant" : role === "user" ? "You" : role;
}

export function ChatMessage({
	message,
	placeholder,
	footer,
	isStreaming = false,
	streamingParts,
	streamingReasoningOverflowBytes = 0,
	onRegenerate,
	revisionNav,
	onBranch,
	showFeedbackControls = false,
	feedback,
	feedbackPending = false,
	onSubmitFeedback,
	reasoningEffort,
	failureCategory,
}: ChatMessageProps) {
	const { t } = useTranslation();
	const reducedMotion = useReducedMotion();
	const showTokensPerSecond = useNodeChatPreferencesStore((state) => state.showTokensPerSecond);
	const setShowTokensPerSecond = useNodeChatPreferencesStore((state) => state.actions.setShowTokensPerSecond);
	const label = roleLabel(message.role);
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
	const canCopy = hasContentStarted && !isStreaming;
	const canRegenerate = assistantMessage && !isStreaming && Boolean(onRegenerate);
	const canBranch = assistantMessage && !isStreaming && Boolean(onBranch);
	const showRevisionNav = assistantMessage && !isStreaming && Boolean(revisionNav) && (revisionNav?.total ?? 0) > 1;
	const showFeedback = assistantMessage && !isStreaming && showFeedbackControls && Boolean(onSubmitFeedback) && hasContentStarted;
	// The ⋮ options menu shows on every completed assistant turn (not while streaming), independent of the other
	// actions, so the menu is always reachable. Including it in hasActions guarantees the actions row renders.
	const showMenu = assistantMessage && !isStreaming;
	const hasActions = canCopy || canRegenerate || canBranch || showRevisionNav || showFeedback || showMenu;

	const actionCapabilities: ChatMessageActionCapabilities = {
		copy: canCopy,
		regenerate: canRegenerate,
		branch: canBranch,
		revisionNav: showRevisionNav,
		feedback: showFeedback,
		menu: showMenu,
		showTokensPerSecond,
	};

	const actions = hasActions ? (
		<ChatMessageActions
			message={message}
			capabilities={actionCapabilities}
			revisionNav={revisionNav}
			onRegenerate={onRegenerate}
			onBranch={onBranch}
			feedback={feedback}
			feedbackPending={feedbackPending}
			onSubmitFeedback={onSubmitFeedback}
			onToggleTokensPerSecond={() => setShowTokensPerSecond(!showTokensPerSecond)}
		/>
	) : null;

	if (userMessage) {
		return (
			<Group justify="flex-end" align="flex-end" wrap="nowrap" data-testid={`chat-message-${message.id}`}>
				<Stack gap={4} align="flex-end" style={{ maxWidth: "82%" }}>
					<Paper p="sm" style={{ background: "var(--mantine-primary-color-light)", borderRadius: "14px 14px 4px 14px" }}>
						{content ? <ChatMarkdown content={content} /> : null}
					</Paper>
					<Group gap={4} align="center">
						<span data-testid={`chat-message-role-${message.id}`} style={{ position: "absolute", left: "-10000px" }}>
							{label}
						</span>
						{time ? (
							<Text size="xs" c="dimmed">
								{time}
							</Text>
						) : null}
						{actions}
					</Group>
					{footer}
				</Stack>
			</Group>
		);
	}

	return (
		<Group align="flex-start" wrap="nowrap" gap="sm" data-testid={`chat-message-${message.id}`}>
			{assistantMessage ? (
				<Avatar color="primary" radius="md" size={30} variant="light">
					<IconSparkles size={14} />
				</Avatar>
			) : null}
			<Stack gap={4} style={{ flex: 1, minWidth: 0, maxWidth: assistantMessage ? "92%" : "100%" }}>
				<Group gap={6} align="center">
					<Text size="sm" fw={600} data-testid={`chat-message-role-${message.id}`}>
						{assistantMessage ? t("pages.chat.nodeReply", "Node reply") : label}
					</Text>
					{assistantMessage && isStreaming ? (
						<Badge
							variant="light"
							size="xs"
							leftSection={
								<m.span
									style={{ display: "inline-block", width: 6, height: 6, borderRadius: 999, background: CHAT_ACCENT }}
									animate={reducedMotion ? undefined : { opacity: [0.4, 1, 0.4] }}
									transition={reducedMotion ? undefined : { duration: 0.6, repeat: Number.POSITIVE_INFINITY }}
								/>
							}
						>
							{t("pages.chat.streaming", "streaming")}
						</Badge>
					) : null}
				</Group>
				{assistantMessage ? (
					<MessageParts
						parts={parts}
						isStreaming={isStreaming}
						streamingReasoningOverflowBytes={streamingReasoningOverflowBytes}
						hasContentStarted={hasContentStarted}
						reasoningBypassed={reasoningEffort === "none" && (message.reasoning?.trim().length ?? 0) > 0}
					/>
				) : null}
				<AnimatePresence initial={false}>
					{content ? (
						<m.div
							key="answer"
							initial={reducedMotion ? { opacity: 1, y: 0 } : { opacity: 0, y: -6 }}
							animate={{ opacity: 1, y: 0 }}
						>
							<Paper
								withBorder={true}
								p="sm"
								style={{
									background: assistantMessage ? CHAT_ASSISTANT_BACKGROUND : "var(--mantine-color-body)",
									borderColor: assistantMessage ? CHAT_ASSISTANT_BORDER : undefined,
									borderRadius: "4px 14px 14px 14px",
									fontSize: assistantMessage ? 13.5 : undefined,
									lineHeight: assistantMessage ? 1.6 : undefined,
								}}
							>
								<ChatMarkdown content={content} withCaret={assistantMessage && isStreaming} />
							</Paper>
						</m.div>
					) : null}
				</AnimatePresence>
				{(errorText || failureCategory === "ModelNotInstalled") ? (
					<Alert
						color="red"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.chat.error.title", "Response failed")}
						data-testid={`chat-message-error-${message.id}`}
						style={{ borderRadius: "4px 14px 14px 14px" }}
					>
						<Stack gap={6}>
							{/* A "Local runtime default" send with no installed GGUF chat model is surfaced as a friendly,
							    actionable message (with a Models CTA) rather than the raw backend error string. Every other
							    category keeps the backend-provided message. */}
							<Text size="sm">
								{failureCategory === "ModelNotInstalled"
									? t("pages.chat.error.modelNotInstalled", "No chat model installed. Pull a GGUF model to start chatting.")
									: errorText}
							</Text>
							{failureCategory === "ModelNotInstalled" ? (
								<Anchor
									component={Link}
									to="/models"
									size="sm"
									data-testid={`chat-message-error-models-link-${message.id}`}
								>
									{t("pages.chat.error.goToModels", "Go to Models")}
								</Anchor>
							) : null}
							{hasText(failureCategory) ? (
								<Badge color="red" size="sm" variant="light" data-testid={`chat-message-error-category-${message.id}`}>
									{failureCategory}
								</Badge>
							) : null}
						</Stack>
					</Alert>
				) : null}
				{assistantMessage && (agentDisplayName || time) ? (
					// Attribution row: left side holds action icons (real empty Box when null, so space-between pins
					// right side even during streaming when actions is null). Right side = agentName · Model: X ·
					// Reasoning: X · [NN tok/s ·] time. The tps segment sits before time (when the toggle is on) so the
					// clock stays right-most.
					<Group justify="space-between" align="center" wrap="nowrap" gap={4}>
						<Box>{actions}</Box>
						<Text size="xs" c="dimmed" data-testid={`chat-message-agent-${message.id}`} style={{ flexShrink: 0 }}>
							{[agentDisplayName, modelLabel, reasoningLabel, showTokensPerSecond ? tpsLabel : undefined, time]
								.filter(Boolean)
								.join(" · ")}
						</Text>
					</Group>
				) : (
					actions
				)}
				{footer}
			</Stack>
		</Group>
	);
}
