import { Alert, Button, Group, Text } from "@mantine/core";
import { IconPlayerPause, IconPlayerPlay, IconWifiOff, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { DevWorkflowRunStatusBadge } from "@/features/devWorkflows/components/DevWorkflowStatusBadge";
import { type DevWorkflowRunStatus, isTerminalDevWorkflowRunStatus } from "@/features/devWorkflows/models/DevWorkflowModels";

export interface DevWorkflowRunToolbarProps {
	readonly status: DevWorkflowRunStatus;
	readonly definitionName?: string;
	/** Y20: gates AND needs-intervention nodes. This is the only number that may say "decisions needed". */
	readonly pendingDecisionCount: number;
	readonly blockingGateNodeRunId?: string;
	readonly liveUpdatesUnavailable: boolean;
	readonly isCommandPending: boolean;
	readonly commandError?: string;
	readonly onPause: () => void;
	readonly onResume: () => void;
	readonly onCancel: () => void;
	readonly onJumpToDecision: (nodeRunId: string) => void;
}

export function DevWorkflowRunToolbar({
	status,
	definitionName,
	pendingDecisionCount,
	blockingGateNodeRunId,
	liveUpdatesUnavailable,
	isCommandPending,
	commandError,
	onPause,
	onResume,
	onCancel,
	onJumpToDecision,
}: DevWorkflowRunToolbarProps) {
	const { t } = useTranslation();
	const terminal = isTerminalDevWorkflowRunStatus(status);
	// `Pausing` and `Cancelling` are the drain: the command was accepted and live nodes are winding down. Offering the
	// buttons again would invite a second command that changes nothing, so the toolbar shows the state and waits.
	const draining = status === "Pausing" || status === "Cancelling";
	const canPause = !terminal && !draining && status !== "Paused";
	const canResume = status === "Paused";
	const canCancel = !terminal && !draining;

	return (
		<Group gap="xs" wrap="wrap" data-testid="dev-workflow-run-toolbar">
			<DevWorkflowRunStatusBadge status={status} />
			{definitionName ? (
				<Text size="xs" c="dimmed" lineClamp={1}>
					{definitionName}
				</Text>
			) : null}

			{/* Never derived from the run badge: a run reads WaitingForApproval for an open gate AND for a stopped node
			    needing intervention, so the number of things that actually need a human is this count and nothing else. */}
			{pendingDecisionCount > 0 ? (
				<Button
					size="xs"
					color="orange"
					variant="light"
					disabled={!blockingGateNodeRunId}
					onClick={() => {
						if (blockingGateNodeRunId) {
							onJumpToDecision(blockingGateNodeRunId);
						}
					}}
					data-testid="dev-workflow-decisions-needed"
				>
					{t("pages.devWorkflows.detail.decisionsNeeded", "{{count}} decision needed", { count: pendingDecisionCount })}
				</Button>
			) : null}

			<Group gap="xs" wrap="wrap" ml="auto">
				{canResume ? (
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlayerPlay size={14} />}
						loading={isCommandPending}
						onClick={onResume}
						data-testid="dev-workflow-run-resume"
					>
						{t("pages.devWorkflows.detail.resume", "Resume")}
					</Button>
				) : null}
				{canPause ? (
					<Button
						size="xs"
						variant="light"
						leftSection={<IconPlayerPause size={14} />}
						loading={isCommandPending}
						onClick={onPause}
						data-testid="dev-workflow-run-pause"
					>
						{t("pages.devWorkflows.detail.pause", "Pause")}
					</Button>
				) : null}
				{canCancel ? (
					<Button
						size="xs"
						variant="light"
						color="red"
						leftSection={<IconX size={14} />}
						loading={isCommandPending}
						onClick={onCancel}
						data-testid="dev-workflow-run-cancel"
					>
						{t("pages.devWorkflows.detail.cancel", "Cancel run")}
					</Button>
				) : null}
			</Group>

			{liveUpdatesUnavailable ? (
				<Alert
					color="yellow"
					variant="light"
					icon={<IconWifiOff size={16} />}
					p="xs"
					w="100%"
					data-testid="dev-workflow-live-unavailable"
				>
					<Text size="xs">
						{t("pages.devWorkflows.detail.liveUnavailable", "Live updates are unavailable — this page is polling instead.")}
					</Text>
				</Alert>
			) : null}
			{commandError ? (
				<Alert color="red" variant="light" p="xs" w="100%" data-testid="dev-workflow-run-command-error">
					<Text size="xs">{commandError}</Text>
				</Alert>
			) : null}
		</Group>
	);
}
