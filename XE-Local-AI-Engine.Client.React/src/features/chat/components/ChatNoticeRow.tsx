import { Group, Text, ThemeIcon } from "@mantine/core";
import {
	IconArrowsExchange,
	IconBolt,
	IconBookOff,
	IconCloudOff,
	IconCpu,
	IconCut,
	IconFileOff,
	IconFileText,
	IconFilter,
	IconHistory,
	IconHistoryOff,
	IconHourglass,
	IconInfoCircle,
	IconMessageOff,
	IconToolsOff,
	IconUserOff,
	IconUsersGroup,
} from "@tabler/icons-react";
import { memo } from "react";
import { useTranslation } from "react-i18next";

import type { ChatNoticePart } from "@/features/chat/models/ChatModels";

interface ChatNoticeRowProps {
	part: ChatNoticePart;
}

/** Icon per server `noticeKind` enum name; unknown/forward-compat kinds fall back to a generic info icon. */
function noticeIcon(noticeKind: string) {
	switch (noticeKind) {
		case "ModelSubstituted":
			return IconArrowsExchange;
		case "ToolDisabled":
			return IconToolsOff;
		case "HistoryTruncated":
			return IconHistory;
		case "QueuedBehindWorkSession":
			return IconHourglass;
		case "OrchestrationDegraded":
			return IconUsersGroup;
		// Its own glyph, not ToolDisabled's: a tool that was turned OFF and a tool that was merely held back but
		// stays callable are opposite outcomes, and sharing an icon reads the optimisation as a degradation.
		case "ToolsFiltered":
			return IconFilter;
		case "EffortDispatched":
			return IconBolt;
		case "PlaybookWithheld":
			return IconBookOff;
		case "EmptyAnswer":
			return IconMessageOff;
		case "ToolHistoryWithheld":
			return IconHistoryOff;
		case "ToolsWithheld":
			return IconToolsOff;
		case "CloudToolsWithheld":
			return IconCloudOff;
		case "OutputLimitReached":
			return IconCut;
		case "KnowledgeUnavailable":
			return IconBookOff;
		case "AttachmentShortened":
		case "AttachmentsTruncated":
			return IconFileText;
		case "AttachmentsNotSent":
			return IconFileOff;
		case "CpuFallback":
			return IconCpu;
		case "AgentUnavailable":
			return IconUserOff;
		case "QueuedBehindParkedTurn":
		case "QueuedBehindGpuWork":
		case "ResumeQueuedBehindTurn":
			return IconHourglass;
		default:
			return IconInfoCircle;
	}
}

/** i18n key per `noticeKind`, used only as the icon's accessible label — never the notice message itself. */
function noticeLabelKey(noticeKind: string): string | undefined {
	switch (noticeKind) {
		case "ModelSubstituted":
			return "chat.notices.modelSubstituted";
		case "ToolDisabled":
			return "chat.notices.toolDisabled";
		case "HistoryTruncated":
			return "chat.notices.historyTruncated";
		case "QueuedBehindWorkSession":
			return "chat.notices.queuedBehindWorkSession";
		case "OrchestrationDegraded":
			return "chat.notices.orchestrationDegraded";
		case "ToolsFiltered":
			return "chat.notices.toolsFiltered";
		case "EffortDispatched":
			return "chat.notices.effortDispatched";
		case "PlaybookWithheld":
			return "chat.notices.playbookWithheld";
		case "EmptyAnswer":
			return "chat.notices.emptyAnswer";
		case "ToolHistoryWithheld":
			return "chat.notices.toolHistoryWithheld";
		case "ToolsWithheld":
			return "chat.notices.toolsWithheld";
		case "CloudToolsWithheld":
			return "chat.notices.cloudToolsWithheld";
		case "OutputLimitReached":
			return "chat.notices.outputLimitReached";
		case "KnowledgeUnavailable":
			return "chat.notices.knowledgeUnavailable";
		case "AttachmentShortened":
			return "chat.notices.attachmentShortened";
		case "AttachmentsNotSent":
			return "chat.notices.attachmentsNotSent";
		case "AttachmentsTruncated":
			return "chat.notices.attachmentsTruncated";
		case "CpuFallback":
			return "chat.notices.cpuFallback";
		case "AgentUnavailable":
			return "chat.notices.agentUnavailable";
		case "QueuedBehindParkedTurn":
			return "chat.notices.queuedBehindParkedTurn";
		case "QueuedBehindGpuWork":
			return "chat.notices.queuedBehindGpuWork";
		case "ResumeQueuedBehindTurn":
			return "chat.notices.resumeQueuedBehindTurn";
		default:
			return undefined;
	}
}

// Server notice sentences the SPA localizes, keyed by their exact English text (InvocationRunner's
// StoppedWhileThinkingNoticeMessage, OutputLimitReachedNoticeMessage, ToolCallInReasoningNoticeMessage,
// ToolCallAsTextNoticeMessage, and KnowledgeUnavailableNotice.Message and AttachmentShortenedNoticeMessage, and
// NodeChatStreamService's AttachmentsNotSentNoticeMessage and AttachmentsTruncatedNoticeMessage, ChatTurnResolution's
// AgentUnavailableNoticeMessage, the retired QueuedBehindParkedTurn text kept for persisted replies, HumanPark's
// ResumeQueuedMessage, WorkSessionSlotHolderNotice.Message and GpuWorkHolderNotice's four messages — change both together).
// Any other text renders verbatim.
const localizedNoticeKeys: Readonly<Record<string, string>> = {
	"This message was queued behind a running work session step.": "chat.notices.queuedBehindWorkSessionText",
	"This message was queued behind a running benchmark.": "chat.notices.queuedBehindBenchmarkText",
	"This message was queued behind a running training run.": "chat.notices.queuedBehindTrainingRunText",
	"This message was queued behind a running training evaluation.": "chat.notices.queuedBehindEvaluationRunText",
	"This message was queued behind a running model export.": "chat.notices.queuedBehindExportText",
	"The model wrote a tool call as text instead of calling the tool, so the call did not run and there is no answer.":
		"chat.notices.toolCallAsTextText",
	"The model stopped while thinking before it could answer; its thoughts are kept above.":
		"chat.notices.stoppedWhileThinkingText",
	"The answer stopped at the length limit before the model finished.": "chat.notices.outputLimitReachedText",
	"run_python was not offered because Compute tools is turned off. Turn it on in Node Settings, Features.":
		"chat.notices.computeWithheldText",
	"The model tried to call a tool inside its reasoning, where the call cannot run, and stopped without an answer.":
		"chat.notices.toolCallInReasoningText",
	"Your knowledge base was not used for this message because no embedding model is installed, so your documents could not be indexed.":
		"chat.notices.knowledgeUnavailableText",
	"The attached file was shortened to fit this model's context window; the model was told it is incomplete.":
		"chat.notices.attachmentShortenedText",
	"Some attached files were not sent to the model: no text could be read from them, or the model cannot see images.":
		"chat.notices.attachmentsNotSentText",
	"Some attached files were not sent to the model: this turn offers tools but not the file tools, so attachment text is left out, or the model cannot see images.":
		"chat.notices.attachmentsNotSentWithoutFileToolsText",
	"Some attached files were too long for the attachment limit, so the model received only their beginning.":
		"chat.notices.attachmentsTruncatedText",
	"The agent this conversation uses no longer exists, so the default assistant answered.": "chat.notices.agentUnavailableText",
	"This message is waiting for an approval or a question in another conversation. Answer it there to continue.":
		"chat.notices.queuedBehindParkedTurnText",
	"After your answer, this reply was queued behind another running reply.": "chat.notices.resumeQueuedBehindTurnText",
};

// A bare id (an agent or conversation id) is not a reason a reader can use, so it is never printed as the detail line.
const bareIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * A small muted system-style row for a non-fatal "turn notice" (model substitution, tool disabled, history
 * truncated, orchestration degraded to a single agent) — rendered inline in the ordered parts interleave, visually distinct from both the plain answer
 * (`ChatMarkdown`) and an error state: neutral/dimmed color, no red, no collapse/expand. `part.text` is the
 * backend-sanitized, user-facing sentence and is displayed verbatim (not translated).
 *
 * `part.detail`, when the backend sent one, follows the sentence as a small monospace code — the stable machine
 * reason the notice fired (which adaptive-effort rule decided the turn, which model withheld the attachments). It is
 * an identifier, not prose, so it is rendered verbatim and never translated; a notice without one renders nothing
 * extra.
 */
export const ChatNoticeRow = memo(function ChatNoticeRow({ part }: ChatNoticeRowProps) {
	const { t } = useTranslation();
	const Icon = noticeIcon(part.noticeKind);
	const labelKey = noticeLabelKey(part.noticeKind);
	const label = labelKey ? t(labelKey) : undefined;
	const textKey = localizedNoticeKeys[part.text];
	const text = textKey ? t(textKey, part.text) : part.text;
	const detail = part.detail && !bareIdPattern.test(part.detail.trim()) ? part.detail : undefined;

	return (
		<Group gap="xs" wrap="nowrap" align="center" data-testid="chat-notice-row" data-notice-kind={part.noticeKind}>
			<ThemeIcon size={20} radius="xl" variant="light" color="gray" aria-label={label} title={label}>
				<Icon size={12} />
			</ThemeIcon>
			<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>
				{text}
				{/* A real space, not a margin, so copied text and screen readers do not run the sentence into the detail. */}
				{detail ? (
					<>
						{" "}
						<Text component="span" size="xs" c="dimmed" ff="monospace" opacity={0.7} data-testid="chat-notice-detail">
							{detail}
						</Text>
					</>
				) : null}
			</Text>
		</Group>
	);
});
