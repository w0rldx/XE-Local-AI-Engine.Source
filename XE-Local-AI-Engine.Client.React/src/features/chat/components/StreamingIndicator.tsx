import { Badge, Text } from "@mantine/core";
import { IconClock } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

/* eslint-disable react-doctor/no-many-boolean-props */

// Errors are NOT rendered here: a failed turn shows its error as a highlighted block inside the assistant
// bubble (see ChatMessage) so it renders exactly once and survives reload. This footer only conveys the
// transient queued/streaming/delayed affordances.
interface StreamingIndicatorProps {
	hasContent?: boolean;
	isDelayed?: boolean;
	isQueued?: boolean;
	isActive: boolean;
}

export function StreamingIndicator({ hasContent = false, isDelayed = false, isQueued = false, isActive }: StreamingIndicatorProps) {
	const { t } = useTranslation();

	// Queued is distinct from streaming/typing: the turn is accepted but waiting behind another active
	// invocation, so show a paused clock affordance with no typing text.
	if (isQueued && isActive) {
		return (
			<Badge
				color="gray"
				size="sm"
				variant="light"
				leftSection={<IconClock size={12} />}
				data-testid="chat-stream-queued-indicator"
			>
				{t("pages.chat.queued", "Queued — waiting for current task")}
			</Badge>
		);
	}

	if (!isActive || !hasContent) {
		return null;
	}

	return (
		<Text size="sm" c="dimmed" data-testid={isDelayed ? "chat-stream-delayed-indicator" : "chat-streaming-indicator"}>
			{isDelayed ? t("pages.chat.waitingForResponse", "Waiting for response") : t("pages.chat.streaming", "streaming")}
		</Text>
	);
}
