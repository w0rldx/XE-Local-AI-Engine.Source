import { ActionIcon, Avatar, Badge, CopyButton, Group, Paper, Stack, Text, Tooltip } from "@mantine/core";
import {
	IconCheck,
	IconChecks,
	IconChevronLeft,
	IconChevronRight,
	IconCopy,
	IconGitBranch,
	IconRefresh,
	IconSparkles,
} from "@tabler/icons-react";
import { AnimatePresence, m, useReducedMotion } from "framer-motion";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { ChatMarkdown } from "@/features/chat/components/ChatMarkdown";
import { CHAT_ACCENT, CHAT_ASSISTANT_BACKGROUND, CHAT_ASSISTANT_BORDER } from "@/features/chat/components/ChatVisualTokens";
import { MessageFeedbackControl } from "@/features/chat/components/MessageFeedbackControl";
import { ThoughtsSection } from "@/features/chat/components/ThoughtsSection";
import { ChatActivityTimeline, ToolCallDisplay } from "@/features/chat/components/ToolCallDisplay";
import type {
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatTimelineEntry,
	ChatToolCall,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";

const EMPTY_ENTRIES: ChatTimelineEntry[] = [];

/** Prev/next navigation across the sibling revisions (variant group) of an assistant turn (Phase 5.2). */
export interface ChatMessageRevisionNav {
	activeIndex: number;
	total: number;
	onPrevious: () => void;
	onNext: () => void;
}

interface ChatMessageProps {
	message: ChatMessageModel;
	placeholder?: string;
	footer?: ReactNode;
	isStreaming?: boolean;
	streamingReasoning?: string;
	streamingReasoningOverflowBytes?: number;
	entries?: ChatTimelineEntry[];
	onRegenerate?: (messageId: string) => void;
	revisionNav?: ChatMessageRevisionNav;
	onBranch?: (messageId: string) => void;
	showFeedbackControls?: boolean;
	feedback?: ChatMessageFeedback;
	feedbackPending?: boolean;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	// The active composer reasoning effort, used to flag reasoning emitted while "none" is selected.
	reasoningEffort?: ReasoningEffort;
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
	return entries.reduce<ChatToolCall[]>((accumulator, entry) => {
		if (!entry.toolName) {
			return accumulator;
		}

		accumulator.push({
			id: entry.id,
			name: entry.toolName,
			state: entry.state ?? (entry.type === "ToolResult" ? "received" : "waiting"),
			args: entry.toolArgs,
			result: entry.toolResult,
			requiresApproval: entry.requiresApproval,
		});

		return accumulator;
	}, []);
}

export function ChatMessage({
	message,
	placeholder,
	footer,
	isStreaming = false,
	streamingReasoning,
	streamingReasoningOverflowBytes = 0,
	entries = EMPTY_ENTRIES,
	onRegenerate,
	revisionNav,
	onBranch,
	showFeedbackControls = false,
	feedback,
	feedbackPending = false,
	onSubmitFeedback,
	reasoningEffort,
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
	const canCopy = hasContentStarted && !isStreaming;
	const canRegenerate = assistantMessage && !isStreaming && Boolean(onRegenerate);
	const canBranch = assistantMessage && !isStreaming && Boolean(onBranch);
	const showRevisionNav = assistantMessage && !isStreaming && Boolean(revisionNav) && (revisionNav?.total ?? 0) > 1;
	const showFeedback = assistantMessage && !isStreaming && showFeedbackControls && Boolean(onSubmitFeedback) && hasContentStarted;
	const hasActions = canCopy || canRegenerate || canBranch || showRevisionNav || showFeedback;

	const actions = hasActions ? (
		<Group gap={2} align="center" data-testid={`chat-message-actions-${message.id}`}>
			{showRevisionNav && revisionNav ? (
				<Group gap={0} align="center" data-testid={`message-revision-nav-${message.id}`}>
					<Tooltip label={t("pages.chat.revisions.previous", "Previous revision")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.revisions.previous", "Previous revision")}
							color="gray"
							variant="subtle"
							size="sm"
							disabled={revisionNav.activeIndex <= 0}
							onClick={revisionNav.onPrevious}
							data-testid={`message-revision-prev-${message.id}`}
						>
							<IconChevronLeft size={14} />
						</ActionIcon>
					</Tooltip>
					<Text size="xs" c="dimmed" data-testid={`message-revision-count-${message.id}`}>
						{revisionNav.activeIndex + 1}/{revisionNav.total}
					</Text>
					<Tooltip label={t("pages.chat.revisions.next", "Next revision")} withArrow={true}>
						<ActionIcon
							aria-label={t("pages.chat.revisions.next", "Next revision")}
							color="gray"
							variant="subtle"
							size="sm"
							disabled={revisionNav.activeIndex >= revisionNav.total - 1}
							onClick={revisionNav.onNext}
							data-testid={`message-revision-next-${message.id}`}
						>
							<IconChevronRight size={14} />
						</ActionIcon>
					</Tooltip>
				</Group>
			) : null}
			{canCopy ? (
				<CopyButton value={message.content} timeout={2000}>
					{({ copied, copy }) => (
						<Tooltip
							label={
								copied
									? t("pages.chat.actions.copySuccess", "Message copied to clipboard.")
									: t("pages.chat.actions.copy", "Copy message")
							}
							withArrow={true}
						>
							<ActionIcon
								aria-label={t("pages.chat.actions.copy", "Copy message")}
								color={copied ? "teal" : "gray"}
								variant="subtle"
								size="sm"
								onClick={copy}
							>
								{copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
							</ActionIcon>
						</Tooltip>
					)}
				</CopyButton>
			) : null}
			{canRegenerate ? (
				<Tooltip label={t("pages.chat.actions.regenerate", "Regenerate response")} withArrow={true}>
					<ActionIcon
						aria-label={t("pages.chat.actions.regenerate", "Regenerate response")}
						color="gray"
						variant="subtle"
						size="sm"
						onClick={() => onRegenerate?.(message.id)}
					>
						<IconRefresh size={14} />
					</ActionIcon>
				</Tooltip>
			) : null}
			{canBranch ? (
				<Tooltip label={t("pages.chat.actions.branch", "Branch from here")} withArrow={true}>
					<ActionIcon
						aria-label={t("pages.chat.actions.branch", "Branch from here")}
						color="gray"
						variant="subtle"
						size="sm"
						onClick={() => onBranch?.(message.id)}
						data-testid={`message-branch-${message.id}`}
					>
						<IconGitBranch size={14} />
					</ActionIcon>
				</Tooltip>
			) : null}
			{showFeedback && onSubmitFeedback ? (
				<MessageFeedbackControl
					messageId={message.id}
					feedback={feedback}
					pending={feedbackPending}
					onSubmit={(rating, comment) => onSubmitFeedback(message.id, rating, comment)}
				/>
			) : null}
		</Group>
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
						<IconChecks size={12} color="var(--mantine-color-teal-6)" />
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
						reasoningBypassed={reasoningEffort === "none" && (message.reasoning?.trim().length ?? 0) > 0}
					/>
				) : null}
				{assistantMessage && isStreaming ? <ToolCallDisplay calls={toolCalls} /> : null}
				{assistantMessage && !isStreaming ? <ChatActivityTimeline entries={entries} /> : null}
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
				{actions}
				{footer}
			</Stack>
		</Group>
	);
}
