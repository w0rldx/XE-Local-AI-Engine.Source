import { Alert, Button, Group, Text } from "@mantine/core";
import { IconArrowLeft, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { GraphWorkflowRunStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	type GraphWorkflowRunSummaryResponse,
	isTerminalGraphWorkflowRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { useCancelGraphWorkflowRun } from "@/features/graphWorkflows/queries/useGraphWorkflows";

export interface GraphWorkflowRunToolbarProps {
	readonly run: GraphWorkflowRunSummaryResponse;
	/** Clears `runId` in the page selection, which is what puts the editor back in the centre pane. */
	readonly onBackToEditor: () => void;
}

function timestamp(value: number | null | undefined): string | undefined {
	return value == null ? undefined : new Date(value).toLocaleString();
}

export function GraphWorkflowRunToolbar({ run, onBackToEditor }: GraphWorkflowRunToolbarProps) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const cancel = useCancelGraphWorkflowRun();

	const status = narrowGraphWorkflowRunStatus(run.status);
	// `Cancelling` is the drain: the command was accepted and the live nodes are winding down, so offering Cancel
	// again would invite a second command that changes nothing.
	const canCancel = !isTerminalGraphWorkflowRunStatus(status) && status !== "Cancelling";

	const requestCancel = async (): Promise<void> => {
		const confirmed = await confirm({
			title: t("pages.graphWorkflows.toolbar.cancelConfirmTitle", "Cancel this run?"),
			description: t(
				"pages.graphWorkflows.toolbar.cancelConfirmBody",
				"The nodes that are already running wind down first, and nothing new is dispatched.",
			),
			confirmationText: t("pages.graphWorkflows.toolbar.cancel", "Cancel run"),
			cancellationText: t("common.close", "Close"),
		});
		if (!confirmed) {
			return;
		}
		cancel.mutate({ path: { runId: run.id ?? "" } });
	};

	return (
		<Group gap="xs" wrap="wrap" data-testid="graph-workflow-run-toolbar">
			<GraphWorkflowRunStatusBadge status={run.status} />
			<Text size="xs" c="dimmed" data-testid="graph-workflow-run-toolbar-version">
				{t("pages.graphWorkflows.toolbar.version", "definition version {{version}}", { version: run.definitionVersion ?? 1 })}
			</Text>
			{timestamp(run.startedAtUtc) ? (
				<Text size="xs" c="dimmed" data-testid="graph-workflow-run-toolbar-started">
					{t("pages.graphWorkflows.toolbar.started", "started {{when}}", { when: timestamp(run.startedAtUtc) })}
				</Text>
			) : null}
			{timestamp(run.completedAtUtc) ? (
				<Text size="xs" c="dimmed" data-testid="graph-workflow-run-toolbar-completed">
					{t("pages.graphWorkflows.toolbar.completed", "finished {{when}}", { when: timestamp(run.completedAtUtc) })}
				</Text>
			) : null}

			<Group gap="xs" wrap="wrap" ml="auto">
				<Button
					size="xs"
					variant="subtle"
					leftSection={<IconArrowLeft size={14} />}
					onClick={onBackToEditor}
					data-testid="graph-workflow-run-toolbar-back"
				>
					{t("pages.graphWorkflows.toolbar.back", "Back to editor")}
				</Button>
				<Button
					size="xs"
					variant="light"
					color="red"
					leftSection={<IconX size={14} />}
					loading={cancel.isPending}
					disabled={!canCancel || cancel.isPending}
					onClick={() => {
						requestCancel().catch(() => undefined);
					}}
					data-testid="graph-workflow-run-toolbar-cancel"
				>
					{t("pages.graphWorkflows.toolbar.cancel", "Cancel run")}
				</Button>
			</Group>

			{cancel.error ? (
				<Alert color="red" variant="light" p="xs" w="100%" data-testid="graph-workflow-run-toolbar-error">
					<Text size="xs">
						{apiErrorMessage(cancel.error, t("pages.graphWorkflows.toolbar.cancelFailed", "The run could not be cancelled."))}
					</Text>
				</Alert>
			) : null}
		</Group>
	);
}
