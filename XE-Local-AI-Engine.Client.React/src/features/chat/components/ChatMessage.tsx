import { Avatar, Badge, Group, Paper, Stack, Text } from "@mantine/core";
import { IconChecks, IconSparkles } from "@tabler/icons-react";
import { AnimatePresence, m, useReducedMotion } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import { CHAT_ACCENT, CHAT_ASSISTANT_BACKGROUND, CHAT_ASSISTANT_BORDER } from "@/features/chat/components/ChatVisualTokens";
import { ChatActivityTimeline, ToolCallDisplay } from "@/features/chat/components/ToolCallDisplay";
import { ThoughtsSection } from "@/features/chat/components/ThoughtsSection";
import type { ChatMessageModel, ChatTimelineEntry, ChatToolCall } from "@/features/chat/models/ChatModels";

interface ChatMessageProps {
	message: ChatMessageModel;
	placeholder?: string;
	footer?: ReactNode;
	isStreaming?: boolean;
	streamingReasoning?: string;
	streamingReasoningOverflowBytes?: number;
	entries?: ChatTimelineEntry[];
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

function calls(entries: ChatTimelineEntry[]): ChatToolCall[] {
	return entries
		.filter((entry) => entry.toolName)
		.map((entry) => ({
			id: entry.id,
			name: entry.toolName ?? "tool",
			state: entry.state ?? (entry.type === "ToolResult" ? "received" : "waiting"),
			args: entry.toolArgs,
			result: entry.toolResult,
		}));
}

export function ChatMessage({
	message,
	placeholder,
	footer,
	isStreaming = false,
	streamingReasoning,
	streamingReasoningOverflowBytes = 0,
	entries = [],
}: ChatMessageProps) {
	const { t } = useTranslation();
	const reducedMotion = useReducedMotion();
	const label = roleLabel(message.role);
	const userMessage = message.role === "user";
	const assistantMessage = message.role === "assistant";
	const content = message.content.trim().length > 0 ? message.content : placeholder;
	const time = timeText(message.updatedAt ?? message.createdAt);
	const hasContentStarted = message.content.trim().length > 0;
	const toolCalls = assistantMessage ? calls(entries) : [];

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
						<IconChecks size={12} color="var(--mantine-color-teal-6)" />
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
					{time ? (
						<Text size="xs" c="dimmed">
							· {time}
						</Text>
					) : null}
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
					<ThoughtsSection
						messageId={message.id}
						reasoning={message.reasoning}
						streamingContent={streamingReasoning}
						streamingOverflowBytes={streamingReasoningOverflowBytes}
						isStreaming={isStreaming}
						hasContentStarted={hasContentStarted}
					/>
				) : null}
				{assistantMessage && isStreaming ? <ToolCallDisplay calls={toolCalls} /> : null}
				{assistantMessage && !isStreaming ? <ChatActivityTimeline entries={entries} /> : null}
				<AnimatePresence initial={false}>
					{content ? (
						<m.div key="answer" initial={reducedMotion ? { opacity: 1, y: 0 } : { opacity: 0, y: -6 }} animate={{ opacity: 1, y: 0 }}>
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
				{footer}
			</Stack>
		</Group>
	);
}
