import { Alert, Badge, Button, Group, Paper, ScrollArea, Skeleton, Stack, Text, Tooltip } from "@mantine/core";
import { IconAlertTriangle, IconPlayerPause, IconPlayerPlay, IconPlugConnectedX, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { WorkSessionStatusBadge, WorkSessionTaskStatusBadge } from "@/features/workSessions/components/WorkSessionStatusBadge";
import {
	isActiveWorkSessionStatus,
	toWorkSessionTaskStatus,
	type WorkSessionStatus,
	type WorkSessionTaskResponse,
} from "@/features/workSessions/models/WorkSessionModels";

export interface WorkSessionPlanPanelProps {
	readonly status: WorkSessionStatus;
	readonly stepCount: number;
	readonly maxStepsPerRun: number;
	readonly currentTaskId?: string | null;
	readonly tasks: readonly WorkSessionTaskResponse[];
	readonly isLoadingTasks: boolean;
	/** Step of the newest checkpoint, shown as the recovery point for a paused/interrupted session. */
	readonly latestCheckpointStep?: number;
	/** `outcome` of the newest event, shown beside a `Failed` badge. */
	readonly lastFailureOutcome?: string;
	/** True while the live hub is down and the page is polling instead. Informational, never blocking. */
	readonly liveUpdatesUnavailable: boolean;
	readonly isCommandPending: boolean;
	readonly onStart: () => void;
	readonly onPause: () => void;
	readonly onResume: () => void;
	readonly onCancel: () => void;
}

interface TaskNode {
	readonly task: WorkSessionTaskResponse;
	readonly children: readonly TaskNode[];
}

/** Nests by `parentTaskId` and orders each level by `sequence`. An orphaned child renders at the root, never dropped. */
function buildTaskTree(tasks: readonly WorkSessionTaskResponse[]): readonly TaskNode[] {
	const byParent = new Map<string, WorkSessionTaskResponse[]>();
	const ids = new Set(tasks.map((task) => task.id ?? ""));
	for (const task of tasks) {
		const parent = task.parentTaskId && ids.has(task.parentTaskId) ? task.parentTaskId : "";
		const siblings = byParent.get(parent) ?? [];
		siblings.push(task);
		byParent.set(parent, siblings);
	}
	const build = (parentId: string): readonly TaskNode[] =>
		(byParent.get(parentId) ?? [])
			.toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0))
			.map((task) => ({ task, children: build(task.id ?? "") }));
	return build("");
}

function TaskRow({ node, depth, currentTaskId }: { node: TaskNode; depth: number; currentTaskId?: string | null }) {
	const { t } = useTranslation();
	const status = toWorkSessionTaskStatus(node.task.status);
	const isCurrent = Boolean(node.task.id) && node.task.id === currentTaskId;
	return (
		<>
			<Stack
				gap={2}
				pl={depth * 16}
				data-testid={`work-session-task-${node.task.id}`}
				data-current={isCurrent ? "true" : undefined}
			>
				<Group gap="xs" wrap="nowrap" align="flex-start">
					<Text size="sm" fw={isCurrent ? 700 : 400} style={{ flex: 1, minWidth: 0 }}>
						{node.task.title}
					</Text>
					<WorkSessionTaskStatusBadge status={status} testId={`work-session-task-status-${node.task.id}`} />
				</Group>
				{status === "Blocked" && node.task.blockedReason ? (
					<Tooltip label={node.task.blockedReason} multiline={true} w={240} withArrow={true}>
						<Text size="xs" c="orange" lineClamp={1} data-testid={`work-session-task-blocked-${node.task.id}`}>
							{t("pages.workSessions.plan.blocked", "Blocked: {{reason}}", { reason: node.task.blockedReason })}
						</Text>
					</Tooltip>
				) : null}
			</Stack>
			{node.children.map((child) => (
				<TaskRow key={child.task.id} node={child} depth={depth + 1} currentTaskId={currentTaskId} />
			))}
		</>
	);
}

export function WorkSessionPlanPanel({
	status,
	stepCount,
	maxStepsPerRun,
	currentTaskId,
	tasks,
	isLoadingTasks,
	latestCheckpointStep,
	lastFailureOutcome,
	liveUpdatesUnavailable,
	isCommandPending,
	onStart,
	onPause,
	onResume,
	onCancel,
}: WorkSessionPlanPanelProps) {
	const { t } = useTranslation();
	const tree = buildTaskTree(tasks);
	const canResume = status === "Paused" || status === "Interrupted";
	const active = isActiveWorkSessionStatus(status);

	return (
		<Paper withBorder={true} p="md" h="100%" data-testid="work-session-plan-panel" style={{ display: "flex", flexDirection: "column", minHeight: 0 }}>
			<Stack gap="sm" style={{ flex: 1, minHeight: 0 }}>
				<Group gap="xs" wrap="wrap">
					<WorkSessionStatusBadge status={status} />
					<Text size="xs" c="dimmed" data-testid="work-session-step-counter">
						{t("pages.workSessions.plan.stepOf", "Step {{step}} of {{max}}", { step: stepCount, max: maxStepsPerRun })}
					</Text>
					{liveUpdatesUnavailable ? (
						<Tooltip label={t("pages.workSessions.plan.liveUnavailableHint", "Live updates are unavailable; this page is polling instead.")}>
							<Badge size="xs" variant="light" color="gray" leftSection={<IconPlugConnectedX size={10} />} data-testid="work-session-live-unavailable">
								{t("pages.workSessions.plan.liveUnavailable", "Polling")}
							</Badge>
						</Tooltip>
					) : null}
				</Group>

				{status === "Interrupted" ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-interrupted-alert">
						{t("pages.workSessions.plan.interrupted", "The engine restarted mid-run. Resume to continue from the last checkpoint.")}
					</Alert>
				) : null}
				{status === "Failed" && lastFailureOutcome ? (
					<Alert color="red" variant="light" icon={<IconAlertTriangle size={16} />} data-testid="work-session-failed-alert">
						{lastFailureOutcome}
					</Alert>
				) : null}
				{status === "WaitingForApproval" ? (
					<Text size="xs" c="orange" data-testid="work-session-waiting-approval-hint">
						{t(
							"pages.workSessions.plan.waitingForApproval",
							"Waiting for your approval in the conversation — the session is holding this node's model while it waits.",
						)}
					</Text>
				) : null}
				{status === "WaitingForInput" ? (
					<Text size="xs" c="orange" data-testid="work-session-waiting-input-hint">
						{t(
							"pages.workSessions.plan.waitingForInput",
							"The agent asked you a question in the conversation — the session is holding this node's model while it waits.",
						)}
					</Text>
				) : null}
				{canResume && latestCheckpointStep !== undefined ? (
					<Text size="xs" c="dimmed" data-testid="work-session-checkpoint-hint">
						{t("pages.workSessions.plan.pausedAtCheckpoint", "Paused at checkpoint {{step}}", { step: latestCheckpointStep })}
					</Text>
				) : null}

				<Group gap="xs" wrap="wrap" data-testid="work-session-controls">
					{status === "Draft" ? (
						<Button size="xs" leftSection={<IconPlayerPlay size={14} />} onClick={onStart} disabled={isCommandPending} data-testid="work-session-start">
							{t("pages.workSessions.plan.start", "Start")}
						</Button>
					) : null}
					{active ? (
						<Button size="xs" variant="light" leftSection={<IconPlayerPause size={14} />} onClick={onPause} disabled={isCommandPending} data-testid="work-session-pause">
							{t("pages.workSessions.plan.pause", "Pause")}
						</Button>
					) : null}
					{canResume ? (
						<Button size="xs" leftSection={<IconPlayerPlay size={14} />} onClick={onResume} disabled={isCommandPending} data-testid="work-session-resume">
							{t("pages.workSessions.plan.resume", "Resume")}
						</Button>
					) : null}
					{active || status === "Paused" ? (
						<Button size="xs" variant="subtle" color="red" leftSection={<IconX size={14} />} onClick={onCancel} disabled={isCommandPending} data-testid="work-session-cancel">
							{t("pages.workSessions.plan.cancel", "Cancel")}
						</Button>
					) : null}
				</Group>

				<Text size="xs" fw={600} c="dimmed">
					{t("pages.workSessions.plan.title", "Plan")}
				</Text>
				<ScrollArea style={{ flex: 1, minHeight: 0 }} data-testid="work-session-task-tree">
					{isLoadingTasks && tasks.length === 0 ? (
						<Stack gap="xs">
							<Skeleton height={18} />
							<Skeleton height={18} />
						</Stack>
					) : tree.length === 0 ? (
						<Text size="xs" c="dimmed" data-testid="work-session-task-tree-empty">
							{status === "Draft"
								? t("pages.workSessions.plan.emptyDraft", "The agent will draft the plan on its first step.")
								: t("pages.workSessions.plan.empty", "No tasks yet.")}
						</Text>
					) : (
						<Stack gap="xs">
							{tree.map((node) => (
								<TaskRow key={node.task.id} node={node} depth={0} currentTaskId={currentTaskId} />
							))}
						</Stack>
					)}
				</ScrollArea>
			</Stack>
		</Paper>
	);
}
