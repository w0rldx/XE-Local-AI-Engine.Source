import { Alert, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { isTerminalWorkSessionStatus, resumesOnFollowUp, type WorkSessionStatus } from "@/features/workSessions/models/WorkSessionModels";

/**
 * What happens to a follow-up the operator types, by status. A follow-up is ALWAYS persisted at post time, so the
 * notice only promises when it gets *used* — and the three promises are not interchangeable:
 *
 * - `Draft` has no next step until Start, so it must never say "queued for the next step".
 * - `Paused`/`Interrupted` auto-resume on post, so the notice says "sent — resuming".
 * - `WaitingFor*` behave like `Running`: a step is live right now, parked on a card and holding the node's single
 *   invocation slot, so there is nothing to resume and the message waits for the next step.
 */
export function WorkSessionFollowUpNotice({ status, error }: { status: WorkSessionStatus; error?: string }) {
	const { t } = useTranslation();

	if (error) {
		return (
			<Alert color="red" variant="light" data-testid="work-session-follow-up-error">
				{error}
			</Alert>
		);
	}

	return (
		<Text size="xs" c="dimmed" data-testid="work-session-follow-up-notice">
			{noticeText(status, t)}
		</Text>
	);
}

function noticeText(status: WorkSessionStatus, t: (key: string, fallback: string) => string): string {
	if (status === "Draft") {
		return t("pages.workSessions.followUp.draft", "Saved — it will be used when you start this session.");
	}
	if (resumesOnFollowUp(status)) {
		return t("pages.workSessions.followUp.resuming", "Sent — resuming.");
	}
	if (isTerminalWorkSessionStatus(status)) {
		return t("pages.workSessions.followUp.closed", "This session is finished and takes no further messages.");
	}
	return t("pages.workSessions.followUp.queued", "Queued for the next step.");
}
