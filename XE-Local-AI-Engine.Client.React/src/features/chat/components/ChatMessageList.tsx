import { Alert, Button, Loader, ScrollArea, Stack, Text } from "@mantine/core";
import { IconAlertTriangle } from "@tabler/icons-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { Fragment, useEffect, useMemo, useRef } from "react";
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
// A scroll landing within this many pixels of the bottom counts as "at the bottom" and keeps auto-scroll
// latched on; scrolling further up unlatches it so a user reading earlier output mid-generation is left alone.
const NEAR_BOTTOM_THRESHOLD_PX = 100;
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
}

function bySortOrder(left: ChatMessageModel, right: ChatMessageModel): number {
	return left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt);
}

type MessageRevisionGroup = ReturnType<typeof groupMessageRevisions>[number];

// One rendered turn in the list: a persisted revision group, or the transient synthetic streaming turn.
type ListRow =
	| { readonly kind: "group"; readonly key: string; readonly group: MessageRevisionGroup }
	| { readonly kind: "streaming"; readonly key: string };

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
}: ChatMessageListProps) {
	const { t } = useTranslation();
	const endRef = useRef<HTMLDivElement>(null);
	// The ScrollArea viewport: a scroll listener on it drives the stick-to-bottom latch below.
	const viewportRef = useRef<HTMLDivElement>(null);
	// Whether auto-scroll should follow new content. Latched from actual scroll position (scroll listener), NOT
	// inferred from geometry after content grows — a single large coalesced frame can add >100px in one commit,
	// so measuring distance post-growth would wrongly disengage and strand the stream off-screen. The user
	// scrolling up unlatches; scrolling back near the bottom re-latches. Defaults on so a fresh list sticks.
	const stickToBottomRef = useRef(true);
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

	// Virtualized path only: row heights land asynchronously (estimate → measured), growing the total size after
	// the scrollKey-driven follow already ran. While latched, re-pin on every total-size change so the view stays
	// at the bottom as measurements (and the streaming row's growth) arrive. "auto" — this fires per measurement,
	// stacking smooth animations would judder.
	useEffect(() => {
		if (virtualTotalSize > 0 && stickToBottomRef.current) {
			endRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
		}
	}, [virtualTotalSize]);

	// Drive the stick-to-bottom latch from real scroll events: unlatch once the user scrolls further than the
	// threshold from the bottom, re-latch when they return near it. Our own scrollIntoView also lands near the
	// bottom, so it keeps the latch engaged. Attached once; the viewport ref is populated by commit time.
	useEffect(() => {
		const viewport = viewportRef.current;
		if (!viewport) {
			return;
		}

		const handleScroll = (): void => {
			const distanceFromBottom = viewport.scrollHeight - viewport.scrollTop - viewport.clientHeight;
			stickToBottomRef.current = distanceFromBottom <= NEAR_BOTTOM_THRESHOLD_PX;
		};

		viewport.addEventListener("scroll", handleScroll, { passive: true });
		return () => viewport.removeEventListener("scroll", handleScroll);
	}, []);

	// Re-latch when the thread changes or a NEW streaming turn begins so switching conversations or sending a
	// message returns the view to the newest content — but never when a turn CLEARS on completion, which must
	// leave a scrolled-up reader exactly where they are (that terminal case is guarded by the latch below).
	const previousStreamingTurnRef = useRef<string | undefined>(undefined);
	const previousConversationIdRef = useRef<string | undefined>(undefined);
	useEffect(() => {
		const conversationId = conversation?.id;
		const turnStarted = Boolean(streamingTurnId) && streamingTurnId !== previousStreamingTurnRef.current;
		const conversationChanged = conversationId !== previousConversationIdRef.current;
		if (turnStarted || conversationChanged) {
			stickToBottomRef.current = true;
		}
		previousStreamingTurnRef.current = streamingTurnId;
		previousConversationIdRef.current = conversationId;
	}, [conversation?.id, streamingTurnId]);

	useEffect(() => {
		// scrollKey is the re-run trigger: it changes whenever rendered content grows (new revision group, tool
		// entry, or streamed character), which is exactly when we may need to follow the stream. Reading it here
		// also keeps it a declared dependency.
		if (scrollKey.length === 0) {
			return;
		}

		// Only follow the stream while latched (fixes both the large-frame disengage and the terminal-completion
		// yank). Jump with "auto" during streaming so per-frame growth doesn't stack overlapping smooth-scroll
		// animations; use "smooth" for one-off transitions (open/switch conversation, turn completion).
		if (!stickToBottomRef.current) {
			return;
		}

		endRef.current?.scrollIntoView({ behavior: isStreamingActive ? "auto" : "smooth", block: "end" });
	}, [scrollKey, isStreamingActive]);

	const renderGroupRow = (group: MessageRevisionGroup) => {
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
							hasContent={hasStreamingContent}
							isDelayed={scopedStreamingMessage?.isDelayed}
							isQueued={scopedStreamingMessage?.isQueued}
							isActive={scopedStreamingMessage?.isActive ?? false}
							runtimePhase={scopedStreamingMessage?.runtimePhase}
						/>
					) : undefined
				}
			/>
		);
	};

	const renderStreamingTurn = () =>
		conversation && scopedStreamingMessage ? (
			<ChatMessage
				message={{
					id: scopedStreamingMessage.messageId,
					conversationId: conversation.id,
					role: "assistant",
					content: scopedStreamingMessage.content,
					status: scopedStreamingMessage.isQueued
						? "queued"
						: scopedStreamingMessage.isActive
							? "streaming"
							: scopedStreamingMessage.error
								? "failed"
								: "completed",
					// Carry the live error so the transient turn renders it once as an error block inside the
					// bubble (the post-stream refetch then swaps in the persisted failed turn, same id).
					error: scopedStreamingMessage.error,
					createdAt: streamingStartedAt ?? conversation.updatedAt,
					sortOrder: normalizedMessages.length + 1,
					// Carry the optimistically-stamped agent attribution and reasoning effort so the live turn
					// shows both immediately — the persisted values replace them on the post-stream refetch.
					agentName: streamingAssistantMessage?.agentName,
					agentDefinitionId: streamingAssistantMessage?.agentDefinitionId,
					reasoningEffort: streamingAssistantMessage?.reasoningEffort,
				}}
				placeholder={streamingPlaceholder}
				streamingParts={scopedStreamingMessage.parts}
				streamingReasoningOverflowBytes={scopedStreamingMessage.reasoningOverflowBytes}
				isStreaming={scopedStreamingMessage.isActive && !scopedStreamingMessage.isQueued}
				reasoningEffort={reasoningEffort}
				failureCategory={scopedStreamingMessage.failureCategory}
				footer={
					<StreamingIndicator
						hasContent={hasStreamingContent}
						isDelayed={scopedStreamingMessage.isDelayed}
						isQueued={scopedStreamingMessage.isQueued}
						isActive={scopedStreamingMessage.isActive}
						runtimePhase={scopedStreamingMessage.runtimePhase}
					/>
				}
			/>
		) : null;

	const renderRow = (row: ListRow) => (row.kind === "group" ? renderGroupRow(row.group) : renderStreamingTurn());

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
								{renderRow(row)}
							</div>
						);
					})}
				</div>
			) : (
				<Stack gap="sm">
					{rows.map((row) => (
						<Fragment key={row.key}>{renderRow(row)}</Fragment>
					))}
				</Stack>
			)}
			<Stack gap="sm">
				{!conversation ? (
					<Text size="sm" c="dimmed">
						{t("pages.chat.emptyState", "Select a conversation to start chatting.")}
					</Text>
				) : null}

				{/* Load failure takes precedence over the spinner and the empty-state: a permanently-failing
				    getConversation must surface an actionable error with Retry, never spin forever. Only shown when
				    there is nothing else to display for the selection (no messages, no live stream). */}
				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage && messagesLoadFailed ? (
					<Alert
						role="alert"
						color="red"
						variant="light"
						icon={<IconAlertTriangle size={16} />}
						title={t("pages.chat.loadError.title", "Couldn't load this conversation")}
						data-testid="chat-messages-load-error"
					>
						<Stack gap="sm" align="flex-start">
							<Text size="sm">{t("pages.chat.loadError.body", "Something went wrong loading these messages.")}</Text>
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
						</Stack>
					</Alert>
				) : null}

				{conversation && normalizedMessages.length === 0 && !scopedStreamingMessage && !messagesLoadFailed && isLoadingMessages ? (
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
