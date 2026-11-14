import { Button, Group, Loader, Paper, Stack, Text } from "@mantine/core";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { ChatMarkdown } from "@/core/chat/components/ChatMarkdown";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useDeleteScheduledRunAnswer, useScheduledRunAnswer } from "@/features/scheduler/queries/useScheduler";

interface ScheduledRunAnswerProps {
	readonly conversationId: string;
}

// Read-only view of a successful agent run's saved prompt and answer. Not the chat page on purpose: a scheduled
// conversation takes no new turns, so nothing here can send, regenerate or edit. Delete is the operator's own path to
// remove the stored text before the run's history retention does.
export function ScheduledRunAnswer({ conversationId }: ScheduledRunAnswerProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const answer = useScheduledRunAnswer(conversationId);
	const deleteAnswer = useDeleteScheduledRunAnswer();
	const [deleteError, setDeleteError] = useState<string | null>(null);

	async function handleDelete() {
		const confirmed = await confirm({
			title: t("pages.scheduler.runs.detail.answer.deleteTitle", "Delete this answer?"),
			description: t(
				"pages.scheduler.runs.detail.answer.deleteDescription",
				"The saved prompt and answer of this run are removed for good. The run stays in the history.",
			),
			confirmationText: t("common.delete", "Delete"),
			cancellationText: t("common.cancel", "Cancel"),
		});
		if (!confirmed) {
			return;
		}

		setDeleteError(null);
		try {
			await deleteAnswer.mutateAsync({ path: { conversationId }, body: { purgeImmediately: true } });
		} catch (error) {
			setDeleteError(
				apiErrorMessage(error, t("pages.scheduler.runs.detail.answer.deleteFailed", "Could not delete the answer.")),
			);
		}
	}

	if (answer.isPending) {
		return (
			<Group gap="sm" data-testid="scheduler-run-answer-loading">
				<Loader size="sm" />
				<Text c="dimmed">{t("pages.scheduler.runs.detail.answer.loading", "Loading answer…")}</Text>
			</Group>
		);
	}

	if (answer.isError) {
		return (
			<InlineErrorAlert
				message={apiErrorMessage(answer.error, t("pages.scheduler.runs.detail.answer.error", "Could not load the answer."))}
				data-testid="scheduler-run-answer-error"
			/>
		);
	}

	const messages = [...answer.data.messages].sort((left, right) => left.sequence - right.sequence);

	return (
		<Stack gap="sm" data-testid="scheduler-run-answer">
			{messages.map((message) => (
				<Paper key={message.messageId} withBorder={true} p="sm" data-testid={`scheduler-run-answer-${message.role}`}>
					<Text size="xs" c="dimmed" mb={4}>
						{message.role === "user"
							? t("pages.scheduler.runs.detail.answer.prompt", "Prompt")
							: t("pages.scheduler.runs.detail.answer.response", "Answer")}
					</Text>
					<ChatMarkdown content={message.content} />
				</Paper>
			))}
			{deleteError ? <InlineErrorAlert message={deleteError} data-testid="scheduler-run-answer-delete-error" /> : null}
			<Group justify="flex-end">
				<Button
					variant="light"
					color="red"
					size="xs"
					loading={deleteAnswer.isPending}
					onClick={handleDelete}
					data-testid="scheduler-run-answer-delete"
				>
					{t("pages.scheduler.runs.detail.answer.delete", "Delete answer")}
				</Button>
			</Group>
		</Stack>
	);
}
