import type { ReactNode } from "react";

import { ChatMessage } from "@/features/chat/components/ChatMessage";
import { StreamingIndicator } from "@/features/chat/components/StreamingIndicator";
import type { ListRow } from "@/features/chat/models/ChatMessageListRow";
import type {
	ChatConversationModel,
	ChatFeedbackRating,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatStreamingState,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";

interface ChatMessageRowProps {
	row: ListRow;
	conversation?: ChatConversationModel;
	// The streaming state already narrowed to this conversation (see ChatMessageList).
	scopedStreamingMessage?: ChatStreamingState;
	streamingPlaceholder?: string;
	hasStreamingContent: boolean;
	streamingStartedAt?: string;
	// The optimistic assistant row stamped at send time; carries the agent attribution the live turn shows.
	streamingAssistantMessage?: ChatMessageModel;
	normalizedMessageCount: number;
	onRegenerate?: (messageId: string) => void;
	onBranch?: (messageId: string) => void;
	onSelectRevision?: (variantGroupId: string, messageId: string) => void;
	showFeedbackControls: boolean;
	feedbackByMessageId?: Readonly<Record<string, ChatMessageFeedback>>;
	pendingFeedbackMessageId?: string;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	reasoningEffort?: ReasoningEffort;
	isWorkSessionConversation: boolean;
	renderAfterMessage?: (messageId: string) => ReactNode;
}

// The single row renderer shared by the plain and the virtualized paths, so the two can never disagree on
// what a turn looks like.
export function ChatMessageRow({
	row,
	conversation,
	scopedStreamingMessage,
	streamingPlaceholder,
	hasStreamingContent,
	streamingStartedAt,
	streamingAssistantMessage,
	normalizedMessageCount,
	onRegenerate,
	onBranch,
	onSelectRevision,
	showFeedbackControls,
	feedbackByMessageId,
	pendingFeedbackMessageId,
	onSubmitFeedback,
	reasoningEffort,
	isWorkSessionConversation,
	renderAfterMessage,
}: ChatMessageRowProps) {
	if (row.kind === "streaming") {
		return conversation && scopedStreamingMessage ? (
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
					sortOrder: normalizedMessageCount + 1,
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
				isWorkSessionConversation={isWorkSessionConversation}
				footer={
					<StreamingIndicator
						hasContent={hasStreamingContent}
						isDelayed={scopedStreamingMessage.isDelayed}
						isQueued={scopedStreamingMessage.isQueued}
						isActive={scopedStreamingMessage.isActive}
						runtimePhase={scopedStreamingMessage.runtimePhase}
						runtimePhaseChangedAtUtc={scopedStreamingMessage.runtimePhaseChangedAtUtc}
					/>
				}
			/>
		) : null;
	}

	const group = row.group;
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

	const messageRow = (
		<ChatMessage
			key={message.id}
			message={message}
			isStreaming={isStreamingTarget ? (scopedStreamingMessage?.isActive ?? false) && !scopedStreamingMessage?.isQueued : false}
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
			isWorkSessionConversation={isWorkSessionConversation}
			footer={
				isStreamingTarget ? (
					<StreamingIndicator
						hasContent={hasStreamingContent}
						isDelayed={scopedStreamingMessage?.isDelayed}
						isQueued={scopedStreamingMessage?.isQueued}
						isActive={scopedStreamingMessage?.isActive ?? false}
						runtimePhase={scopedStreamingMessage?.runtimePhase}
						runtimePhaseChangedAtUtc={scopedStreamingMessage?.runtimePhaseChangedAtUtc}
					/>
				) : undefined
			}
		/>
	);
	const after = renderAfterMessage?.(message.id);
	// Only a row something follows gets a wrapper: the plain path keeps its exact DOM otherwise.
	return after ? (
		<>
			{messageRow}
			{after}
		</>
	) : (
		messageRow
	);
}
