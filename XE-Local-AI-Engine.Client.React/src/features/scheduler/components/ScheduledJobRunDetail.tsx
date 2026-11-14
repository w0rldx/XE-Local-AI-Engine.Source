import { Badge, Button, Group, Loader, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { formatDurationSeconds, formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { ScheduledRunAnswer } from "@/features/scheduler/components/ScheduledRunAnswer";
import { scheduledRunStatusColor } from "@/features/scheduler/components/SchedulerRunFormatters";
import type { ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";

interface ScheduledJobRunDetailProps {
	run: ScheduledJobRun | undefined;
	isLoading: boolean;
	error?: string;
}

// Redacted detail view for one run. The wire never carries the raw details/error stack — only the summary and a
// short error message — so this panel surfaces exactly those fields plus the run's timing metadata. A successful
// agent run's answer is not part of the run: it is a separate conversation, loaded only when the operator opens it.
export function ScheduledJobRunDetail({ run, isLoading, error }: ScheduledJobRunDetailProps) {
	const { t } = useTranslation();
	// Keyed by the answer's id, so switching to another run closes it without a reset effect.
	const [openAnswerId, setOpenAnswerId] = useState<string | null>(null);

	if (isLoading) {
		return (
			<Group gap="sm" data-testid="scheduler-run-detail-loading">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.scheduler.runs.detail.loading", "Loading run…")}</Text>
			</Group>
		);
	}

	if (error) {
		return <InlineErrorAlert message={error} data-testid="scheduler-run-detail-error" />;
	}

	if (!run) {
		return (
			<EmptyState
				message={t("pages.scheduler.runs.detail.empty", "Select a run to view its details.")}
				data-testid="scheduler-run-detail-empty"
			/>
		);
	}

	return (
		<Stack gap="sm" data-testid="scheduler-run-detail">
			<Group gap="xs" align="center">
				<Badge color={scheduledRunStatusColor(run.status)} variant="light">
					{t(`pages.scheduler.runs.status.${run.status}`, run.status)}
				</Badge>
				<Badge variant="outline">{t(`pages.scheduler.runs.trigger.${run.triggeredBy}`, run.triggeredBy)}</Badge>
			</Group>
			<Group gap="xl">
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.scheduler.runs.detail.fired", "Fired")}
					</Text>
					<Text size="sm">{formatTimestamp(run.actualFireTimeUtc)}</Text>
				</Stack>
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.scheduler.runs.detail.completed", "Completed")}
					</Text>
					<Text size="sm">{formatTimestamp(run.completedAtUtc)}</Text>
				</Stack>
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.scheduler.runs.detail.duration", "Duration")}
					</Text>
					<Text size="sm">{formatDurationSeconds(run.durationMs)}</Text>
				</Stack>
			</Group>
			{run.summary ? (
				<Stack gap={0}>
					<Text size="xs" c="dimmed">
						{t("pages.scheduler.runs.detail.summary", "Summary")}
					</Text>
					<Text size="sm">{run.summary}</Text>
				</Stack>
			) : null}
			{run.errorMessage ? <InlineErrorAlert message={run.errorMessage} data-testid="scheduler-run-detail-error-message" /> : null}
			{run.answerConversationId ? (
				<>
					<Group>
						<Button
							variant="light"
							size="xs"
							aria-expanded={openAnswerId === run.answerConversationId}
							onClick={() => setOpenAnswerId(openAnswerId === run.answerConversationId ? null : run.answerConversationId)}
							data-testid="scheduler-run-detail-open-answer"
						>
							{openAnswerId === run.answerConversationId
								? t("pages.scheduler.runs.detail.answer.hide", "Hide answer")
								: t("pages.scheduler.runs.detail.answer.open", "Open answer")}
						</Button>
					</Group>
					{openAnswerId === run.answerConversationId ? <ScheduledRunAnswer conversationId={run.answerConversationId} /> : null}
				</>
			) : null}
		</Stack>
	);
}
