import { Group, Text, ThemeIcon } from "@mantine/core";
import { IconArrowsExchange, IconFilter, IconHistory, IconInfoCircle, IconToolsOff, IconUsersGroup } from "@tabler/icons-react";
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
		default:
			return undefined;
	}
}

/**
 * A small muted system-style row for a non-fatal "turn notice" (model substitution, tool disabled, history
 * truncated, orchestration degraded to a single agent) — rendered inline in the ordered parts interleave, visually distinct from both the plain answer
 * (`ChatMarkdown`) and an error state: neutral/dimmed color, no red, no collapse/expand. `part.text` is the
 * backend-sanitized, user-facing sentence and is displayed verbatim (not translated).
 */
export const ChatNoticeRow = memo(function ChatNoticeRow({ part }: ChatNoticeRowProps) {
	const { t } = useTranslation();
	const Icon = noticeIcon(part.noticeKind);
	const labelKey = noticeLabelKey(part.noticeKind);
	const label = labelKey ? t(labelKey) : undefined;

	return (
		<Group gap="xs" wrap="nowrap" align="center" data-testid="chat-notice-row" data-notice-kind={part.noticeKind}>
			<ThemeIcon size={20} radius="xl" variant="light" color="gray" aria-label={label} title={label}>
				<Icon size={12} />
			</ThemeIcon>
			<Text size="xs" c="dimmed" style={{ overflowWrap: "anywhere" }}>
				{part.text}
			</Text>
		</Group>
	);
});
