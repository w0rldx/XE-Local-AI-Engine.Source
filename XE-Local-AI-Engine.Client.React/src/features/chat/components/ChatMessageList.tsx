import { Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";

import { ChatMessage } from "@/features/chat/components/ChatMessage";
import { StreamingIndicator } from "@/features/chat/components/StreamingIndicator";
import type {
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatStreamingState,
	ChatTimelineEntry,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { groupMessageRevisions } from "@/features/chat/models/MessageRevisionGrouping";

const EMPTY_TIMELINE_ENTRIES: ChatTimelineEntry[] = [];

interface ChatMessageListProps {
	conversation?: ChatConversationModel;
	messages?: ChatMessageModel[];
	streamingMessage?: ChatStreamingState;
	timelineEntries?: ChatTimelineEntry[];
	onRegenerate?: (messageId: string) => void;
	onBranch?: (messageId: string) => void;
	activeRevisionByGroup?: Readonly<Record<string, string>>;
	onSelectRevision?: (variantGroupId: string, messageId: string) => void;
	showFeedbackControls?: boolean;
	feedbackByMessageId?: Readonly<Record<string, ChatMessageFeedback>>;
	pendingFeedbackMessageId?: string;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	// True while the selected conversation's full payload (with messages) is loading. Suppresses the
	// "No messages yet" empty-state so it never flashes over a populated thread mid-fetch.
	isLoadingMessages?: boolean;
	// Active composer reasoning effort; forwarded to each message so the bypass note can flag reasoning
	// emitted while "none" is selected.
	reasoningEffort?: ReasoningEffort;
}

function bySortOrder(left: ChatMessageModel, right: ChatMessageModel): number {
	return left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt);
}

function hasText(value?: string): boolean {
	return typeof value === "string" && value.trim().length > 0;
}

export function ChatMessageList({
	conversation,
	messages,
	streamingMessage,
	timelineEntries = EMPTY_TIMELINE_ENTRIES,
	onRegenerate,
	onBranch,
	activeRevisionByGroup,
	onSelectRevision,
	showFeedbackControls = false,
	feedbackByMessageId,
	pendingFeedbackMessageId,
	onSubmitFeedback,
	isLoadingMessages = false,
	reasoningEffort,
}: ChatMessageListProps) {
	const { t } = useTranslation();
	const endRef = useRef<HTMLDivElement>(null);
	const normalizedMessages = useMemo(
		() =>
			(messages ?? conversation?.messages ?? [])
				.filter((message) => hasText(message.content) || hasText(message.reasoning))
				.toSorted(bySortOrder),
		[conversation?.messages, messages],
	);
	// Collapse sibling assistant variants (shared variant_group_id) to one entry with prev/next nav (assistant revision flow).
	const revisionGroups = useMemo(
		() => groupMessageRevisions(normalizedMessages, activeRevisionByGroup ?? {}),
		[activeRevisionByGroup, normalizedMessages],
	);
	const scopedStreamingMessage =
		conversation?.id && streamingMessage?.conversationId === conversation.id ? streamingMessage : undefined;
	const streamingContent = scopedStreamingMessage?.content ?? "";
	const hasStreamingContent = streamingContent.trim().length > 0;
	const streamingPlaceholder = hasStreamingContent
		? undefined
		: scopedStreamingMessage?.error ||
			(scopedStreamingMessage?.isQueued
				? // Queued is surfaced solely by the StreamingIndicator pill below the turn; emitting it as the body
					// placeholder too would show the same text twice.
					undefined
				: scopedStreamingMessage?.isActive
					? t("pages.chat.waitingForResponse", "Waiting for response")
					: undefined);
	const hasPersistedStreamingMessage = scopedStreamingMessage
		? normalizedMessages.some((message) => message.id === scopedStreamingMessage.messageId && message.role === "assistant")
		: false;
	// Source the transient placeholder's timestamp from the assistant turn itself: the stream's own
	// startedAt, or the optimistic assistant row's createdAt — never the conversation's updatedAt,
	// which tracks the latest mutation (the just-sent user message) and would mislabel the reply.
	const streamingStartedAt =
		scopedStreamingMessage?.startedAt ??
		(conversation?.messages ?? []).find(
			(message) => message.id === scopedStreamingMessage?.messageId && message.role === "assistant",
		)?.createdAt;
	const scrollKey = `${revisionGroups.length}:${timelineEntries.length}:${streamingContent.length}:${scopedStreamingMessage?.isActive ?? false}`;

	useEffect(() => {
		if (scrollKey.length === 0) {
			return;
		}

		endRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
	}, [scrollKey]);

	return (
		<ScrollArea type="hover" scrollbarSize={8} offsetScrollbars="y" style={{ flex: 1, minHeight: 0 }}>
			<Stack gap="sm">
				{revisionGroups.map((group) => {
					const message = group.active;
					const isStreamingTarget = scopedStreamingMessage?.messageId === message.id && message.role === "assistant";
					const isAssistant = message.role === "assistant";
					const variantGroupId = message.variantGroupId;
					const previousRevision = group.revisions[Math.max(0, group.activeIndex - 1)];
					const nextRevision = group.revisions[Math.min(group.revisions.length - 1, group.activeIndex + 1)];
					const revisionNav =
						isAssistant && group.revisions.length > 1 && variantGroupId
							? {
									activeIndex: group.activeIndex,
									total: group.revisions.length,
									onPrevious: () => previousRevision && onSelectRevision?.(variantGroupId, previousRevision.id),
									onNext: () => nextRevision && onSelectRevision?.(variantGroupId, nextRevision.id),
								}
							: undefined;

					return (
						<ChatMessage
							key={message.id}
							message={message}
							isStreaming={
								isStreamingTarget ? (scopedStreamingMessage?.isActive ?? false) && !scopedStreamingMessage?.isQueued : false
							}
							streamingParts={isStreamingTarget ? scopedStreamingMessage?.parts : undefined}
							streamingReasoningOverflowBytes={isStreamingTarget ? scopedStreamingMessage?.reasoningOverflowBytes : undefined}
							placeholder={isStreamingTarget ? streamingPlaceholder : undefined}
							onRegenerate={isAssistant ? onRegenerate : undefined}
							onBranch={isAssistant ? onBranch : undefined}
							revisionNav={revisionNav}
							showFeedbackControls={showFeedbackControls}
							feedback={feedbackByMessageId?.[message.id]}
							feedbackPending={pendingFeedbackMessageId === message.id}
							onSubmitFeedback={onSubmitFeedback}
							reasoningEffort={reasoningEffort}
							footer={
								isStreamingTarget ? (
									<StreamingIndicator
										error={scopedStreamingMessage?.error}
										failureCategory={scopedStreamingMessage?.failureCategory}
										hasContent={hasStreamingContent}
										isDelayed={scopedStreamingMessage?.isDelayed}
										isQueued={scopedStreamingMessage?.isQueued}
										isActive={scopedStreamingMessage?.isActive ?? false}
									/>
								) : undefined
							}
						/>
					);
				})}

				{conversation && scopedStreamingMessage && !hasPersistedStreamingMessage ? (
					<ChatMessage
						message={{
							id: scopedStreamingMessage.messageId,
							conversationId: conversation.id,
							role: "assistant",
							content: scopedStreamingMessage.content,
							status: scopedStreamingMessage.isQueued ? "queued" : scopedStreamingMessage.isActive ? "streaming" : "completed",
							createdAt: streamingStartedAt ?? conversation.updatedAt,
							sortOrder: normalizedMessages.length + 1,
						}}
						placeholder={streamingPlaceholder}
						streamingParts={scopedStreamingMessage.parts}
						streamingReasoningOverflowBytes={scopedStreamingMessage.reasoningOverflowBytes}
						isStreaming={scopedStreamingMessage.isActive && !scopedStreamingMessage.isQueued}
						reasoningEffort={reasoningEffort}
						footer={
							<StreamingIndicator
								error={scopedStreamingMessage.error}
								failureCategory={scopedStreamingMessage.failureCategory}
								hasContent={hasStreamingContent}
								isDelayed={scopedStreamingMessage.isDelayed}
								isQueued={scopedStreamingMessage.isQueued}
								isActive={scopedStreamingMessage.isActive}
							/>
						}
					/>
				) : null}

				{!conversation ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.emptyState", "Select a conversation to start chatting.")}
					</Text>
				) : null}

				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage && isLoadingMessages ? (
					<Stack align="center" py="md" gap="xs">
						<Loader size="sm" />
						<Text size="sm" c="dimmed">
							{t("pages.chat.loadingMessages", "Loading messages…")}
						</Text>
					</Stack>
				) : null}

				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage && !isLoadingMessages ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.noMessages", "No messages yet.")}
					</Text>
				) : null}

				<div ref={endRef} />
			</Stack>
		</ScrollArea>
	);
}
