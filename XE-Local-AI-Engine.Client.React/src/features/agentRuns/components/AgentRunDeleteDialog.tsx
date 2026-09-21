import { Button, Code, Group, Stack, Text } from "@mantine/core";
import { useCallback } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { useDeleteAgentHomeRun } from "@/features/agentRuns/queries/useAgentRuns";

interface AgentRunDeleteDialogProps {
	readonly runId: string;
	readonly onClose: () => void;
}

/**
 * Confirms removing one run before anything is removed.
 *
 * The copy says what goes and what stays, because the two are easy to confuse: the run directory holds the log and
 * the exported patch, while an apply wrote into the operator's own folders and is untouched by this. A node that
 * refuses — a run is in flight — says so here rather than closing as though it had worked.
 */
export function AgentRunDeleteDialog({ runId, onClose }: AgentRunDeleteDialogProps) {
	const { t } = useTranslation();
	const deleteRun = useDeleteAgentHomeRun();

	const handleConfirm = useCallback(() => {
		deleteRun.mutate({ path: { runId } }, { onSuccess: onClose });
	}, [deleteRun, onClose, runId]);

	return (
		<DialogShell
			opened={true}
			onClose={onClose}
			title={t("pages.agentRuns.delete.title", "Delete this run?")}
			size="md"
			enableFullScreenToggle={false}
			data-testid="agent-run-delete-dialog"
			footer={
				<Group gap="sm">
					<Button variant="default" onClick={onClose} data-testid="agent-run-delete-cancel">
						{t("common.cancel", "Cancel")}
					</Button>
					<Button color="red" onClick={handleConfirm} loading={deleteRun.isPending} data-testid="agent-run-delete-confirm">
						{t("pages.agentRuns.delete.confirm", "Delete run")}
					</Button>
				</Group>
			}
		>
			<Stack gap="sm">
				<Code>{runId}</Code>
				<Text size="sm">
					{t("pages.agentRuns.delete.body", "This run's log and its exported patch are removed from this computer.")}
				</Text>
				<Text size="sm" c="dimmed">
					{t(
						"pages.agentRuns.delete.appliedNote",
						"Changes that were already applied stay in your folders. Deleting the run removes its history, not its results.",
					)}
				</Text>
				{deleteRun.isError ? (
					<InlineErrorAlert
						message={apiErrorMessage(
							deleteRun.error,
							t("pages.agentRuns.delete.error", "This run could not be deleted. A run may be in flight; try again in a moment."),
						)}
						data-testid="agent-run-delete-error"
					/>
				) : null}
			</Stack>
		</DialogShell>
	);
}
