import { Anchor, Button, CloseButton, Group, Paper, Stack, Text } from "@mantine/core";
import { IconPlayerStopFilled } from "@tabler/icons-react";
import { Link } from "@tanstack/react-router";
import { Fragment, type ReactNode, useEffect, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import {
	activeWorkflowNode,
	formatWorkflowElapsed,
	isWaitingForWorkflowInput,
	type WorkflowPath,
	workflowNodeGlyph,
	workflowNodeWithStatus,
} from "@/features/chat/workflow/ChatWorkflowModels";
import { GraphWorkflowRunStatusBadge } from "@/features/graphWorkflows/components/GraphWorkflowStatusBadge";
import {
	isTerminalGraphWorkflowRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

const ELAPSED_TICK_MS = 1_000;

interface WorkflowRunStatusCardProps {
	readonly runId: string;
	readonly definitionId: string;
	readonly workflowName: string;
	readonly status: string;
	readonly path: WorkflowPath;
	/** The failed node's error, when the run failed. */
	readonly failureReason?: string;
	readonly stopping: boolean;
	readonly onStop: () => void;
	/** Offered on a terminal run only: acknowledging it hides the card. */
	readonly onDismiss: () => void;
	/** Extension point for the active node's live output (S2). Rendered under the path while the run is live. */
	readonly liveDetail?: ReactNode;
}

function useNow(active: boolean): number {
	const [now, setNow] = useState(() => Date.now());
	useEffect(() => {
		if (!active) {
			return;
		}
		// Re-read the clock at once: the value from mount may be minutes old by the time a node starts.
		setNow(Date.now());
		// real-timer: the elapsed readout is wall-clock by definition; nothing here is awaited by a test.
		const handle = setInterval(() => setNow(Date.now()), ELAPSED_TICK_MS);
		return () => clearInterval(handle);
	}, [active]);
	return now;
}

/** Above the composer while the conversation's newest bound run is live, or finished and not yet dismissed. */
export function WorkflowRunStatusCard({
	runId,
	definitionId,
	workflowName,
	status,
	path,
	failureReason,
	stopping,
	onStop,
	onDismiss,
	liveDetail,
}: WorkflowRunStatusCardProps) {
	const { t } = useTranslation();
	const narrowed = narrowGraphWorkflowRunStatus(status);
	const terminal = isTerminalGraphWorkflowRunStatus(narrowed);
	const active = terminal ? undefined : activeWorkflowNode(path);
	const now = useNow(active?.startedAtUtc != null);

	let terminalText: string | undefined;
	if (narrowed === "Completed") {
		terminalText = t("pages.chat.workflow.status.completed", "Completed");
	} else if (narrowed === "Cancelled") {
		const node = workflowNodeWithStatus(path, "Cancelled");
		terminalText = node
			? t("pages.chat.workflow.status.cancelledDuring", "Cancelled during {{node}}", { node: node.label })
			: t("pages.chat.workflow.status.cancelled", "Cancelled");
	} else if (narrowed === "Failed") {
		const node = workflowNodeWithStatus(path, "Failed");
		terminalText = t("pages.chat.workflow.status.failedAt", "Failed at {{node}}: {{reason}}", {
			node: node?.label ?? "—",
			reason: failureReason ?? t("pages.chat.workflow.status.unknownReason", "no reason was recorded"),
		});
	}

	return (
		<Paper withBorder={true} radius="md" p="xs" data-testid="chat-workflow-status-card">
			<Stack gap={6}>
				<Group justify="space-between" wrap="nowrap" gap="xs">
					<Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
						<Text size="sm" fw={700} lineClamp={1} data-testid="chat-workflow-status-name">
							{workflowName}
						</Text>
						<GraphWorkflowRunStatusBadge
							status={status}
							waitingForInput={isWaitingForWorkflowInput(path)}
							data-testid="chat-workflow-status-badge"
						/>
					</Group>
					<Group gap={4} wrap="nowrap">
						{/* `Link` directly, not `Anchor component={Link}`: the polymorphic prop erases the router's search typing. */}
						<Link to={nodeRoutePaths.graphWorkflows} search={{ runId, definitionId }} data-testid="chat-workflow-status-view">
							<Anchor component="span" size="xs">
								{t("pages.chat.workflow.status.view", "View workflow")}
							</Anchor>
						</Link>
						{terminal ? (
							<CloseButton
								size="sm"
								onClick={onDismiss}
								aria-label={t("pages.chat.workflow.status.dismiss", "Dismiss")}
								data-testid="chat-workflow-status-dismiss"
							/>
						) : (
							<Button
								size="compact-xs"
								color="red"
								variant="light"
								leftSection={<IconPlayerStopFilled size={12} />}
								loading={stopping}
								disabled={narrowed === "Cancelling"}
								onClick={onStop}
								data-testid="chat-workflow-status-stop"
							>
								{t("pages.chat.workflow.status.stop", "Stop")}
							</Button>
						)}
					</Group>
				</Group>
				{path.length > 0 ? (
					<Group gap={6} wrap="wrap" data-testid="chat-workflow-status-path">
						{path.map((rank, index) => (
							<Fragment key={rank.map((node) => node.key).join("|")}>
								{index > 0 ? (
									<Text size="xs" c="dimmed" aria-hidden={true}>
										›
									</Text>
								) : null}
								<Group gap={8} wrap="nowrap">
									{rank.map((node) => (
										<Text
											key={node.key}
											size="xs"
											c={node.status === "Skipped" || node.status === "Pending" ? "dimmed" : undefined}
											td={node.status === "Skipped" ? "line-through" : undefined}
											fw={node.key === active?.key ? 700 : undefined}
											data-testid={`chat-workflow-status-node-${node.key}`}
											data-status={node.status}
										>
											{`${workflowNodeGlyph(node.status)} ${node.label}`}
										</Text>
									))}
								</Group>
							</Fragment>
						))}
					</Group>
				) : null}
				{active ? (
					<Text size="xs" c="dimmed" data-testid="chat-workflow-status-active">
						{[
							active.label,
							...(active.model === undefined
								? []
								: [active.model ?? t("pages.chat.workflow.status.defaultModel", "default model")]),
							...(active.startedAtUtc == null ? [] : [formatWorkflowElapsed(now - active.startedAtUtc)]),
						].join(" · ")}
					</Text>
				) : null}
				{active ? liveDetail : null}
				{terminalText ? (
					<Text size="xs" c={narrowed === "Failed" ? "red" : "dimmed"} data-testid="chat-workflow-status-terminal">
						{terminalText}
					</Text>
				) : null}
			</Stack>
		</Paper>
	);
}
