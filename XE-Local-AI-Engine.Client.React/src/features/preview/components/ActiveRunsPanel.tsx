import { ActionIcon, Badge, Button, Card, Group, Stack, Text } from "@mantine/core";
import { IconPlayerStop, IconRefresh } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { PreviewRunSummary } from "@/features/preview/models/PreviewWorkflowModels";

// Server-truth list of the runs this NODE knows about, not just the ones this tab started. It is the visible half of
// the reload fix: a run whose id lived only in a page that has since reloaded is otherwise unreachable — no id, no
// cancel, and (with the concurrency cap at 4) four such runs used to brick Execute until the node restarted. Every
// row offers Cancel, and Cancel all clears the lot; both free concurrency slots immediately rather than waiting for
// the abandoned-subscriber sweep. Rows for terminal-but-still-replayable runs are shown too, because that is how a
// result survives a reload long enough to be reattached.

interface ActiveRunsPanelProps {
	runs: readonly PreviewRunSummary[];
	isCancelling: boolean;
	// Reattach: register the run with this tab so its buffered events replay over the hub and its live state shows.
	onReattach: (runId: string) => void;
	onCancel: (runId: string) => void;
	onCancelAll: () => void;
}

const RUN_STATE_COLOR: Readonly<Record<PreviewRunSummary["state"], string>> = {
	Running: "cyan",
	Paused: "yellow",
	Completing: "cyan",
	Completed: "green",
	Cancelled: "gray",
	Faulted: "red",
};

export function ActiveRunsPanel({ runs, isCancelling, onReattach, onCancel, onCancelAll }: ActiveRunsPanelProps) {
	const { t } = useTranslation();

	if (runs.length === 0) {
		return null;
	}

	const liveCount = runs.filter((run) => run.isLive).length;

	return (
		<Card withBorder={true} padding="md" data-testid="preview-active-runs">
			<Group justify="space-between" align="center" mb="sm">
				<Text fw={600}>{t("pages.preview.runs.title", "Runs on this node")}</Text>
				<Button
					size="xs"
					variant="light"
					color="red"
					disabled={isCancelling || liveCount === 0}
					onClick={onCancelAll}
					data-testid="preview-runs-cancel-all"
				>
					{t("pages.preview.runs.cancelAll", "Cancel all runs")}
				</Button>
			</Group>
			<Text size="sm" c="dimmed" mb="sm">
				{t(
					"pages.preview.runs.description",
					"Runs are held on the node, not in this browser tab. Reattach to follow a run again after a reload, or cancel it to free its slot.",
				)}
			</Text>
			<Stack gap="xs">
				{runs.map((run) => (
					<Group key={run.runId} justify="space-between" align="center" data-testid={`preview-run-row-${run.runId}`}>
						<Group gap="xs" align="center">
							<Badge color={RUN_STATE_COLOR[run.state]} variant="light">
								{run.state}
							</Badge>
							<Text size="sm" ff="monospace">
								{run.runId}
							</Text>
							<Text size="xs" c="dimmed">
								{new Date(run.startedAtUtc).toLocaleTimeString()}
							</Text>
						</Group>
						<Group gap="xs">
							<ActionIcon
								variant="subtle"
								aria-label={t("pages.preview.runs.reattachAria", "Reattach to run")}
								onClick={() => onReattach(run.runId)}
								data-testid={`preview-run-reattach-${run.runId}`}
							>
								<IconRefresh size={16} />
							</ActionIcon>
							<ActionIcon
								variant="subtle"
								color="red"
								disabled={isCancelling || !run.isLive}
								aria-label={t("pages.preview.runs.cancelAria", "Cancel run")}
								onClick={() => onCancel(run.runId)}
								data-testid={`preview-run-cancel-${run.runId}`}
							>
								<IconPlayerStop size={16} />
							</ActionIcon>
						</Group>
					</Group>
				))}
			</Stack>
		</Card>
	);
}
