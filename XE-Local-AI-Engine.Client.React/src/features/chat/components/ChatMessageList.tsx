import { ScrollArea, Stack, Text } from "@mantine/core";
import { useEffect, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";

import { ChatMessage } from "@/features/chat/components/ChatMessage";
import { StreamingIndicator } from "@/features/chat/components/StreamingIndicator";
import type { ChatConversationModel, ChatMessageModel, ChatStreamingState, ChatTimelineEntry } from "@/features/chat/models/ChatModels";

interface ChatMessageListProps {
	conversation?: ChatConversationModel;
	messages?: ChatMessageModel[];
	streamingMessage?: ChatStreamingState;
	timelineEntries?: ChatTimelineEntry[];
}

function bySortOrder(left: ChatMessageModel, right: ChatMessageModel): number {
	return left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt);
}

function hasText(value?: string): boolean {
	return typeof value === "string" && value.trim().length > 0;
}

export function ChatMessageList({ conversation, messages, streamingMessage, timelineEntries = [] }: ChatMessageListProps) {
	const { t } = useTranslation();
	const endRef = useRef<HTMLDivElement>(null);
	const normalizedMessages = useMemo(
		() => (messages ?? conversation?.messages ?? []).filter((message) => hasText(message.content) || hasText(message.reasoning)).toSorted(bySortOrder),
		[conversation?.messages, messages],
	);
	const scopedStreamingMessage = conversation?.id && streamingMessage?.conversationId === conversation.id ? streamingMessage : undefined;
	const streamingContent = scopedStreamingMessage?.content ?? "";
	const hasStreamingContent = streamingContent.trim().length > 0;
	const streamingPlaceholder = hasStreamingContent
		? undefined
		: scopedStreamingMessage?.error || (scopedStreamingMessage?.isActive ? t("pages.chat.waitingForResponse", "Waiting for response") : undefined);
	const hasPersistedStreamingMessage = scopedStreamingMessage
		? normalizedMessages.some((message) => message.id === scopedStreamingMessage.messageId && message.role === "assistant")
		: false;
	const scrollKey = `${normalizedMessages.length}:${timelineEntries.length}:${streamingContent.length}:${scopedStreamingMessage?.isActive ?? false}`;

	useEffect(() => {
		if (scrollKey.length === 0) {
			return;
		}

		endRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
	}, [scrollKey]);

	return (
		<ScrollArea type="auto" style={{ flex: 1, minHeight: 0 }}>
			<Stack gap="sm">
				{normalizedMessages.map((message) => {
					const messageEntries = timelineEntries.filter((entry) => entry.messageId === message.id);
					const isStreamingTarget = scopedStreamingMessage?.messageId === message.id && message.role === "assistant";

					return (
						<ChatMessage
							key={message.id}
							message={message}
							entries={messageEntries}
							isStreaming={isStreamingTarget ? scopedStreamingMessage?.isActive : false}
							streamingReasoning={isStreamingTarget ? scopedStreamingMessage?.reasoning : undefined}
							streamingReasoningOverflowBytes={isStreamingTarget ? scopedStreamingMessage?.reasoningOverflowBytes : undefined}
							placeholder={isStreamingTarget ? streamingPlaceholder : undefined}
							footer={
								isStreamingTarget ? (
									<StreamingIndicator
										error={scopedStreamingMessage?.error}
										failureCategory={scopedStreamingMessage?.failureCategory}
										hasContent={hasStreamingContent}
										isDelayed={scopedStreamingMessage?.isDelayed}
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
							status: scopedStreamingMessage.isActive ? "streaming" : "completed",
							createdAt: conversation.updatedAt,
							sortOrder: normalizedMessages.length + 1,
						}}
						placeholder={streamingPlaceholder}
						streamingReasoning={scopedStreamingMessage.reasoning}
						streamingReasoningOverflowBytes={scopedStreamingMessage.reasoningOverflowBytes}
						isStreaming={scopedStreamingMessage.isActive}
						entries={timelineEntries.filter((entry) => entry.messageId === scopedStreamingMessage.messageId)}
						footer={
							<StreamingIndicator
								error={scopedStreamingMessage.error}
								failureCategory={scopedStreamingMessage.failureCategory}
								hasContent={hasStreamingContent}
								isDelayed={scopedStreamingMessage.isDelayed}
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

				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.noMessages", "No messages yet.")}
					</Text>
				) : null}

				<div ref={endRef} />
			</Stack>
		</ScrollArea>
	);
}
