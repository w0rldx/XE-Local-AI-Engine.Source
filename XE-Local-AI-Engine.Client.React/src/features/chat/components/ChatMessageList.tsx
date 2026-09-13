import { Button, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Fragment, useMemo, useRef } from "react";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ChatMessageRow } from "@/features/chat/components/ChatMessageList/ChatMessageRow";
import { useStickToBottomScroll } from "@/features/chat/hooks/useStickToBottomScroll";
import type { ListRow } from "@/features/chat/models/ChatMessageListRow";
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
// Above this many turns the list windows its rows (@tanstack/react-virtual) so a long thread does not keep
// every markdown/code-block subtree mounted. At or below it the plain path renders — byte-identical DOM to the
// pre-virtualization list — because a short thread gains nothing from windowing and the plain path keeps the
// common case (and every existing test) untouched.
const VIRTUALIZATION_ROW_THRESHOLD = 30;
// Row spacing in the virtualized path, matching the plain path's <Stack gap="sm"> (12px).
const VIRTUAL_ROW_GAP_PX = 12;
// Estimated unmeasured row height. Only affects initial paint/scrollbar until rows are measured.
const VIRTUAL_ROW_ESTIMATE_PX = 140;

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
	// The selected conversation's full payload failed to load. Renders an inline error + Retry in place of the
	// otherwise-infinite loading spinner. Only applies when there are no messages to show for the selection.
	messagesLoadFailed?: boolean;
	// Resolved error reason shown beneath the generic failure copy for context (optional).
	messagesLoadErrorText?: string;
	// Retries the failed selected-conversation load (query refetch).
	onRetryLoadMessages?: () => void;
	// Active composer reasoning effort; forwarded to each message so the bypass note can flag reasoning
	// emitted while "none" is selected.
	reasoningEffort?: ReasoningEffort;
	// The conversation is owned by a work session. Only used to render a step that ended on its own provider-call
	// cap as a neutral notice rather than a failure (see ChatMessage).
	isWorkSessionConversation?: boolean;
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
	messagesLoadFailed = false,
	messagesLoadErrorText,
	onRetryLoadMessages,
	reasoningEffort,
	isWorkSessionConversation = false,
}: ChatMessageListProps) {
	const { t } = useTranslation();
	// Owned here rather than by useStickToBottomScroll: the row virtualizer below reads the viewport ref, and the
	// hook is deliberately called AFTER it so its re-pin effect keeps the declaration order it had before the cut.
	const viewportRef = useRef<HTMLDivElement>(null);
	const endRef = useRef<HTMLDivElement>(null);
	const normalizedMessages = useMemo(
		() =>
			(messages ?? conversation?.messages ?? [])
				// Keep failed turns even though they have no content/reasoning: they carry an `error` that must
				// survive reload (rendered as an error block inside the bubble, with a regenerate affordance).
				// Keep cancelled turns for the same reason: a user-cancelled turn with no partial output still
				// renders its neutral "Generation stopped" line so the stop is honestly reflected on reload.
				.filter(
					(message) =>
						hasText(message.content) ||
						hasText(message.reasoning) ||
						message.status === "failed" ||
						message.status === "cancelled" ||
						hasText(message.error),
				)
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
	// The stream error is NOT a placeholder: it renders once as an error block inside the assistant bubble
	// (via the message's `error` field below), never as the body text or in the StreamingIndicator footer.
	const streamingPlaceholder = hasStreamingContent
		? undefined
		: scopedStreamingMessage?.isQueued
			? // Queued is surfaced solely by the StreamingIndicator pill below the turn; emitting it as the body
				// placeholder too would show the same text twice.
				undefined
			: scopedStreamingMessage?.isActive
				? t("pages.chat.waitingForResponse", "Waiting for response")
				: undefined;
	const hasPersistedStreamingMessage = scopedStreamingMessage
		? normalizedMessages.some((message) => message.id === scopedStreamingMessage.messageId && message.role === "assistant")
		: false;
	// The optimistic assistant row stamped at send time (appendOptimisticNodeChatSend). It carries the agent
	// attribution but has empty content, so it is filtered out of normalizedMessages — the synthesized streaming
	// turn below must read agentName/createdAt from it directly, otherwise the live turn falls back to the default
	// agent label until the post-stream refetch.
	const streamingAssistantMessage = (conversation?.messages ?? []).find(
		(message) => message.id === scopedStreamingMessage?.messageId && message.role === "assistant",
	);
	// Source the transient placeholder's timestamp from the assistant turn itself: the stream's own
	// startedAt, or the optimistic assistant row's createdAt — never the conversation's updatedAt,
	// which tracks the latest mutation (the just-sent user message) and would mislabel the reply.
	const streamingStartedAt = scopedStreamingMessage?.startedAt ?? streamingAssistantMessage?.createdAt;
	const isStreamingActive = scopedStreamingMessage?.isActive ?? false;
	const streamingTurnId = scopedStreamingMessage?.messageId;
	const scrollKey = `${revisionGroups.length}:${timelineEntries.length}:${streamingContent.length}:${isStreamingActive}`;
	const showSyntheticStreamingTurn = Boolean(conversation && scopedStreamingMessage && !hasPersistedStreamingMessage);
	// One entry per rendered turn: the revision groups plus (while live) the synthetic streaming turn. This is the
	// single row source for BOTH render paths below, so plain and virtualized rendering can never disagree on content.
	const rows = useMemo<ListRow[]>(() => {
		const result: ListRow[] = revisionGroups.map((group) => ({ kind: "group", key: group.active.id, group }));
		if (showSyntheticStreamingTurn && streamingTurnId) {
			result.push({ kind: "streaming", key: `streaming:${streamingTurnId}` });
		}
		return result;
	}, [revisionGroups, showSyntheticStreamingTurn, streamingTurnId]);
	const virtualize = rows.length > VIRTUALIZATION_ROW_THRESHOLD;
	const rowVirtualizer = useVirtualizer({
		count: rows.length,
		getScrollElement: () => viewportRef.current,
		estimateSize: () => VIRTUAL_ROW_ESTIMATE_PX,
		overscan: 6,
		getItemKey: (index) => rows[index]?.key ?? index,
		enabled: virtualize,
	});
	const virtualTotalSize = virtualize ? rowVirtualizer.getTotalSize() : 0;
	useStickToBottomScroll({
		viewportRef,
		endRef,
		virtualTotalSize,
		scrollKey,
		isStreamingActive,
		conversationId: conversation?.id,
		streamingTurnId,
	});

	// Everything a row needs beyond the row itself; identical for both render paths.
	const rowContext = {
		conversation,
		scopedStreamingMessage,
		streamingPlaceholder,
		hasStreamingContent,
		streamingStartedAt,
		streamingAssistantMessage,
		normalizedMessageCount: normalizedMessages.length,
		onRegenerate,
		onBranch,
		onSelectRevision,
		showFeedbackControls,
		feedbackByMessageId,
		pendingFeedbackMessageId,
		onSubmitFeedback,
		reasoningEffort,
		isWorkSessionConversation,
	};

	return (
		<ScrollArea type="hover" scrollbarSize={8} offsetScrollbars="y" viewportRef={viewportRef} style={{ flex: 1, minHeight: 0 }}>
			{virtualize ? (
				// Windowed path for long threads: absolutely-positioned measured rows inside a total-height spacer.
				// Row spacing rides each row wrapper's paddingBottom so measured heights include the gap.
				<div style={{ height: virtualTotalSize, width: "100%", position: "relative" }} data-testid="chat-message-list-virtual">
					{rowVirtualizer.getVirtualItems().map((virtualRow) => {
						const row = rows[virtualRow.index];
						return row === undefined ? null : (
							<div
								key={virtualRow.key}
								data-index={virtualRow.index}
								ref={rowVirtualizer.measureElement}
								style={{
									position: "absolute",
									top: 0,
									left: 0,
									width: "100%",
									transform: `translateY(${virtualRow.start}px)`,
									paddingBottom: VIRTUAL_ROW_GAP_PX,
								}}
							>
								<ChatMessageRow row={row} {...rowContext} />
							</div>
						);
					})}
				</div>
			) : (
				<Stack gap="sm">
					{rows.map((row) => (
						<Fragment key={row.key}>
							<ChatMessageRow row={row} {...rowContext} />
						</Fragment>
					))}
				</Stack>
			)}
			<Stack gap="sm">
				{!conversation ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.emptyState", "Create or select a conversation to start chatting.")}
					</Text>
				) : null}

				{/* Load failure takes precedence over the spinner and the empty-state: a permanently-failing
				    getConversation must surface an actionable error with Retry, never spin forever. Only shown when
				    there is nothing else to display for the selection (no messages, no live stream). */}
				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage && messagesLoadFailed ? (
					<InlineErrorAlert
						variant="light"
						title={t("pages.chat.loadError.title", "Couldn't load this conversation")}
						message={t("pages.chat.loadError.body", "Something went wrong loading these messages.")}
						data-testid="chat-messages-load-error"
					>
						{messagesLoadErrorText ? (
							<Text size="xs" c="dimmed">
								{messagesLoadErrorText}
							</Text>
						) : null}
						{onRetryLoadMessages ? (
							<Button size="xs" variant="light" onClick={onRetryLoadMessages} data-testid="chat-messages-load-retry">
								{t("pages.chat.loadError.retry", "Retry")}
							</Button>
						) : null}
					</InlineErrorAlert>
				) : null}

				{conversation &&
				normalizedMessages.length === 0 &&
				!scopedStreamingMessage &&
				!messagesLoadFailed &&
				isLoadingMessages ? (
					<Stack align="center" py="md" gap="xs" role="status" aria-busy={true} aria-live="polite">
						<Loader size="sm" />
						<Text size="sm" c="dimmed">
							{t("pages.chat.loadingMessages", "Loading messages…")}
						</Text>
					</Stack>
				) : null}

				{conversation &&
				normalizedMessages.length === 0 &&
				!scopedStreamingMessage &&
				!messagesLoadFailed &&
				!isLoadingMessages ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.noMessages", "No messages yet.")}
					</Text>
				) : null}

				<div ref={endRef} />
			</Stack>
		</ScrollArea>
	);
}
