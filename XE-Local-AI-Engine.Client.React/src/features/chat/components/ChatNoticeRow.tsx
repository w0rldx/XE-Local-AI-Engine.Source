import { Group, Text, ThemeIcon } from "@mantine/core";
import {
	IconArrowsExchange,
	IconBolt,
	IconBookOff,
	IconCloudOff,
	IconCut,
	IconFileOff,
	IconFileText,
	IconFilter,
	IconHistory,
	IconHistoryOff,
	IconInfoCircle,
	IconMessageOff,
	IconToolsOff,
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
			return IconFileText;
		case "AttachmentsNotSent":
			return IconFileOff;
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
		default:
			return undefined;
	}
}

// Server notice sentences the SPA localizes, keyed by their exact English text (InvocationRunner's
// StoppedWhileThinkingNoticeMessage, OutputLimitReachedNoticeMessage, ToolCallInReasoningNoticeMessage,
// ToolCallAsTextNoticeMessage, and KnowledgeUnavailableNotice.Message and AttachmentShortenedNoticeMessage, and
// NodeChatStreamService's AttachmentsNotSentNoticeMessage — change both together). Any other text renders verbatim.
const localizedNoticeKeys: Readonly<Record<string, string>> = {
	"The model wrote a tool call as text instead of calling the tool, so the call did not run and there is no answer.":
		"chat.notices.toolCallAsTextText",
	"The model stopped while thinking before it could answer; its thoughts are kept above.":
		"chat.notices.stoppedWhileThinkingText",
	"The answer stopped at the length limit before the model finished.": "chat.notices.outputLimitReachedText",
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
};

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

	return (
		<Group gap="xs" wrap="nowrap" align="center" data-testid="chat-notice-row" data-notice-kind={part.noticeKind}>
			<ThemeIcon size={20} radius="xl" variant="light" color="gray" aria-label={label} title={label}>
				<Icon size={12} />
			</ThemeIcon>
			<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>
				{text}
				{part.detail ? (
					<Text component="span" size="xs" c="dimmed" ff="monospace" opacity={0.7} ml={6} data-testid="chat-notice-detail">
						{part.detail}
					</Text>
				) : null}
			</Text>
		</Group>
	);
});
