import { Anchor, Avatar, Badge, Box, Group, Paper, Stack, Text } from "@mantine/core";
import { IconPlayerStop, IconPlayerTrackNext, IconSparkles } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { AnimatePresence, m, useReducedMotion } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import { ChatSourcesStrip } from "@/features/chat/components/ChatSourcesStrip";
import { CHAT_ACCENT, CHAT_ASSISTANT_BACKGROUND, CHAT_ASSISTANT_BORDER } from "@/features/chat/components/ChatVisualTokens";
import { MessageParts } from "@/features/chat/components/MessageParts";
import type { ChatMessageDisplay } from "@/features/chat/models/ChatMessageDisplay";
import { hasText } from "@/features/chat/models/ChatMessageDisplay";
import type { ChatMessageModel, ReasoningEffort } from "@/features/chat/models/ChatModels";
import { useVoiceRuntime } from "@/features/voice/VoiceRuntimeContext";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";

interface AssistantMessageBubbleProps {
	message: ChatMessageModel;
	display: ChatMessageDisplay;
	actions: ReactNode;
	footer?: ReactNode;
	isStreaming: boolean;
	streamingReasoningOverflowBytes: number;
	reasoningEffort?: ReasoningEffort;
	failureCategory?: string;
	showTokensPerSecond: boolean;
}

// Every non-user turn: the assistant bubble proper, and the rare system/tool row that shares its layout.
export function AssistantMessageBubble({
	message,
	display,
	actions,
	footer,
	isStreaming,
	streamingReasoningOverflowBytes,
	reasoningEffort,
	failureCategory,
	showTokensPerSecond,
}: AssistantMessageBubbleProps) {
	const { t } = useTranslation();
	const reducedMotion = useReducedMotion();
	// When this turn's answer is being read aloud, silence the screen reader on the answer block to avoid a
	// double-read against the active TTS. Inert when no voice provider is mounted.
	const { playingMessageId } = useVoiceRuntime();
	const isBeingSpoken = playingMessageId === message.id;
	const { assistantMessage, content } = display;

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
						{assistantMessage ? t("pages.chat.nodeReply", "Node reply") : display.label}
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
						parts={display.parts}
						isStreaming={isStreaming}
						streamingReasoningOverflowBytes={streamingReasoningOverflowBytes}
						hasContentStarted={display.hasContentStarted}
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
								aria-live={assistantMessage && isBeingSpoken ? "off" : undefined}
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
				{assistantMessage && message.sources && message.sources.length > 0 ? (
					<ChatSourcesStrip sources={message.sources} />
				) : null}
				{display.isCancelled ? (
					<Group gap={6} align="center" data-testid={`chat-message-stopped-${message.id}`} role="status">
						<IconPlayerStop size={14} color="var(--mantine-color-dimmed)" />
						<Text size="sm" c="dimmed">
							{t("pages.chat.stopped", "Generation stopped")}
						</Text>
					</Group>
				) : null}
				{display.isStepBudgetNotice ? (
					<Group gap={6} align="center" data-testid={`chat-message-step-budget-${message.id}`} role="status">
						<IconPlayerTrackNext size={14} color="var(--mantine-color-dimmed)" />
						<Text size="sm" c="dimmed">
							{t("pages.chat.stepBudgetReached", "Step ended — call budget reached, continuing.")}
						</Text>
					</Group>
				) : null}
				{display.showErrorAlert ? (
					<InlineErrorAlert
						variant="light"
						title={t("pages.chat.error.title", "Response failed")}
						data-testid={`chat-message-error-${message.id}`}
						style={{ borderRadius: "4px 14px 14px 14px" }}
						message={
							<Stack gap={6}>
								{/* A "Local runtime default" send with no installed GGUF chat model is surfaced as a friendly,
								    actionable message (with a Models CTA) rather than the raw backend error string. Every other
								    category keeps the backend-provided message. */}
								<Text size="sm">
									{failureCategory === "ModelNotInstalled"
										? t("pages.chat.error.modelNotInstalled", "No chat model installed. Pull a GGUF model to start chatting.")
										: display.errorText}
								</Text>
								{failureCategory === "ModelNotInstalled" ? (
									<Anchor component={Link} to="/models" size="sm" data-testid={`chat-message-error-models-link-${message.id}`}>
										{t("pages.chat.error.goToModels", "Go to Models")}
									</Anchor>
								) : null}
								{hasText(failureCategory) ? (
									<Badge color="red" size="sm" variant="light" data-testid={`chat-message-error-category-${message.id}`}>
										{failureCategory}
									</Badge>
								) : null}
							</Stack>
						}
					/>
				) : null}
				{assistantMessage && (display.agentDisplayName || display.time) ? (
					// Attribution row: left side holds action icons (real empty Box when null, so space-between pins
					// right side even during streaming when actions is null). Right side = agentName · Model: X ·
					// Reasoning: X · [NN tok/s ·] time. The tps segment sits before time (when the toggle is on) so the
					// clock stays right-most. The row wraps on narrow widths: the metadata text drops below the icons
					// (marginLeft auto keeps it right-pinned) instead of overflowing across them.
					<Group justify="space-between" align="center" wrap="wrap" gap={4}>
						<Box>{actions}</Box>
						<Text
							size="xs"
							c="dimmed"
							data-testid={`chat-message-agent-${message.id}`}
							style={{ minWidth: 0, marginLeft: "auto", textAlign: "right" }}
						>
							{[
								display.agentDisplayName,
								display.modelLabel,
								display.reasoningLabel,
								showTokensPerSecond ? display.tpsLabel : undefined,
								display.time,
							]
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
