import { memo, type ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { AssistantMessageBubble } from "@/features/chat/components/ChatMessage/AssistantMessageBubble";
import { UserMessageBubble } from "@/features/chat/components/ChatMessage/UserMessageBubble";
import { ChatMessageActions } from "@/features/chat/components/ChatMessageActions";
import { deriveChatMessageDisplay } from "@/features/chat/models/ChatMessageDisplay";
import type {
	ChatFeedbackRating,
	ChatMessageActionCapabilities,
	ChatMessageFeedback,
	ChatMessageModel,
	ChatMessagePart,
	ChatMessageRevisionNav,
	ReasoningEffort,
} from "@/features/chat/models/ChatModels";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

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
	// The conversation belongs to a work session. Scopes the step-cap notice below so the same text in an ordinary
	// chat still renders as the red failure it is there.
	isWorkSessionConversation?: boolean;
}

// Memoized: during a streaming turn the parent ChatMessageList re-renders every frame, but prior turns receive
// referentially stable props (message from a cached array, callbacks are useCallback'd, streaming-only props are
// undefined for non-target rows) so React.memo skips re-rendering — and re-tokenizing their code blocks — for the
// whole thread on every token of the active turn.
export const ChatMessage = memo(function ChatMessage({
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
	isWorkSessionConversation = false,
}: ChatMessageProps) {
	const { t } = useTranslation();
	const showTokensPerSecond = useNodeChatPreferencesStore((state) => state.showTokensPerSecond);
	const setShowTokensPerSecond = useNodeChatPreferencesStore((state) => state.actions.setShowTokensPerSecond);
	const display = deriveChatMessageDisplay({
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
	});

	const actionCapabilities: ChatMessageActionCapabilities = {
		copy: display.canCopy,
		regenerate: display.canRegenerate,
		branch: display.canBranch,
		revisionNav: display.showRevisionNav,
		feedback: display.showFeedback,
		menu: display.showMenu,
		showTokensPerSecond,
	};

	const actions = display.hasActions ? (
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

	if (display.userMessage) {
		return <UserMessageBubble message={message} display={display} actions={actions} footer={footer} />;
	}

	return (
		<AssistantMessageBubble
			message={message}
			display={display}
			actions={actions}
			footer={footer}
			isStreaming={isStreaming}
			streamingReasoningOverflowBytes={streamingReasoningOverflowBytes}
			reasoningEffort={reasoningEffort}
			failureCategory={failureCategory}
			showTokensPerSecond={showTokensPerSecond}
		/>
	);
});
